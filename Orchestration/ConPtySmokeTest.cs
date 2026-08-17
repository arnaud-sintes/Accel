namespace Accel.Orchestration;

using System;
using System.Diagnostics;
using System.Text;

/// <summary>
/// Hidden dev-only diagnostic for <see cref="ConPtySession"/> (P2-T2), reachable via the undocumented
/// <c>pty-smoke-test</c> verb - the same pattern as the existing <c>ui-preview</c> verb in
/// <c>Program.cs</c>, and for the same reason: the thing under test is real OS resource lifecycle
/// (a real pseudoconsole, a real child process, real kernel handles), which cannot be proven with
/// fakes in a unit test. Unit tests cover the marshalling constants and the error/validation paths;
/// this covers "does it actually work and does it actually clean up".
///
/// <para>Checks, in order:
/// <list type="number">
/// <item>interactive <c>cmd.exe</c>: write a command, read the echoed output back;</item>
/// <item><see cref="ConPtySession.Resize"/>: confirmed from inside the child via <c>mode con</c>,
/// which reports the console dimensions conhost gave it;</item>
/// <item>clean exit + double <see cref="ConPtySession.Dispose"/> (idempotency);</item>
/// <item>Dispose while the child is still alive (the tab-close path): the child must end and Dispose
/// must not block;</item>
/// <item>suspended launch + <see cref="ConPtySession.ResumeMainThread"/>;</item>
/// <item>failure path: a nonexistent image, repeated, to prove a successful
/// <c>CreatePseudoConsole</c> followed by a failing <c>CreateProcessW</c> leaks nothing;</item>
/// <item>leak trend: N full open/close cycles, comparing process handle count before/after;</item>
/// <item><see cref="ConPtySession.TryGetExitCode"/> against a child whose real exit code is 259,
/// i.e. numerically equal to <c>STILL_ACTIVE</c> - the one exit code GetExitCodeProcess cannot
/// report unaided.</item>
/// </list></para>
/// </summary>
public static class ConPtySmokeTest
{
    private const string Marker = "ACCEL_PTY_OK";

    /// <summary>Runs every check. Returns 0 if all passed, 1 otherwise; every step prints a
    /// PASS/FAIL line plus the raw numbers it measured.</summary>
    public static int Run(TextWriter output, int leakIterations = 50)
    {
        ArgumentNullException.ThrowIfNull(output);

        var failures = 0;

        failures += RunInteractiveCheck(output) ? 0 : 1;
        failures += RunDisposeWhileChildAliveCheck(output) ? 0 : 1;
        failures += RunSuspendedCheck(output) ? 0 : 1;
        failures += RunFailurePathLeakCheck(output) ? 0 : 1;
        failures += RunLeakCheck(output, leakIterations) ? 0 : 1;
        failures += RunStillActiveExitCodeCheck(output) ? 0 : 1;

        output.WriteLine();
        output.WriteLine(failures == 0 ? "pty-smoke-test: ALL CHECKS PASSED" : $"pty-smoke-test: {failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static bool RunInteractiveCheck(TextWriter output)
    {
        output.WriteLine("== check 1/6: interactive cmd.exe (write -> read back), resize, clean exit ==");

        var session = ConPtySession.Start(new ConPtyLaunchSpec
        {
            CommandLine = "cmd.exe",
            WorkingDirectory = Path.GetTempPath(),
            Columns = 80,
            Rows = 25,
        });

        OutputPump? pump = null;
        var ok = true;
        try
        {
            output.WriteLine($"  started: pid={session.ProcessId} size={session.Columns}x{session.Rows}");
            pump = OutputPump.Start(session);

            // 1. Prove the child really executed what we typed: cmd echoes the typed line AND then runs
            //    it, so a successful round trip shows the marker at least twice. Terminal-side echo
            //    alone would show it once.
            Send(session, $"echo {Marker}");
            var sawMarker = pump.WaitForOccurrences(Marker, 2, TimeSpan.FromSeconds(10));
            output.WriteLine($"  [{(sawMarker ? "PASS" : "FAIL")}] wrote 'echo {Marker}\\r' to pty stdin, saw the marker {pump.CountOccurrences(Marker)}x in pty stdout (>=2 means cmd echoed it and executed it)");
            ok &= sawMarker;

            // 2. Resize, then ask the child what size its console is. This proves the resize reached
            //    conhost and the child, not just that the API returned S_OK.
            session.Resize(120, 40);
            output.WriteLine($"  resized to {session.Columns}x{session.Rows}, asking the child via 'mode con'");
            Send(session, "mode con");
            var sawColumns = pump.WaitForRegex(@"Columns:\s*120", TimeSpan.FromSeconds(10));
            var sawLines = pump.WaitForRegex(@"Lines:\s*40", TimeSpan.FromSeconds(10));
            output.WriteLine($"  [{(sawColumns && sawLines ? "PASS" : "FAIL")}] child's 'mode con' reports Columns:120 / Lines:40 (columns={sawColumns}, lines={sawLines})");
            ok &= sawColumns && sawLines;

            // 3. Graceful exit through the pty, then exit code via the owned process handle.
            Send(session, "exit");
            var exited = session.WaitForExit(TimeSpan.FromSeconds(10));
            var exitCode = exited ? session.TryGetExitCode() : null;
            output.WriteLine($"  [{(exited ? "PASS" : "FAIL")}] child exited after 'exit\\r' (exitCode={exitCode?.ToString() ?? "still running"})");
            ok &= exited;

            output.WriteLine("  --- last 400 bytes of raw pty output (VT sequences included, decoded here only for display) ---");
            foreach (var line in pump.TailForDisplay(400))
            {
                output.WriteLine($"  | {line}");
            }
        }
        finally
        {
            // Dispose while the pump is still draining: that is the documented contract, since
            // ClosePseudoConsole can block waiting for the output pipe to drain.
            session.Dispose();
            session.Dispose(); // idempotency, on purpose
            output.WriteLine("  [PASS] Dispose() called twice without throwing");
            pump?.Join(TimeSpan.FromSeconds(5));
            if (pump is not null)
            {
                output.WriteLine($"  pump thread: sawEof={pump.SawEof} bytesRead={pump.BytesRead} error={pump.Error?.GetType().Name ?? "none"}");
            }
        }

        return ok;
    }

    /// <summary>The real tab-close path: dispose while the child is still running and was never told to
    /// exit. Proves (a) Dispose does not block for long when the output pipe is being drained, and
    /// (b) closing stdin + ClosePseudoConsole actually ends the child rather than orphaning it.</summary>
    private static bool RunDisposeWhileChildAliveCheck(TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("== check 2/6: Dispose() while the child is still alive (tab-close path) ==");

        var session = ConPtySession.Start(new ConPtyLaunchSpec
        {
            CommandLine = "cmd.exe",
            Columns = 80,
            Rows = 25,
        });
        var pump = OutputPump.Start(session);

        // Independent observer, opened before Dispose so the PID cannot be reused underneath it.
        using var observer = Process.GetProcessById(session.ProcessId);
        pump.WaitForRegex(">", TimeSpan.FromSeconds(10));

        var stopwatch = Stopwatch.StartNew();
        session.Dispose();
        stopwatch.Stop();
        pump.Join(TimeSpan.FromSeconds(5));

        var childDied = observer.WaitForExit(5000);
        output.WriteLine($"  Dispose() returned in {stopwatch.ElapsedMilliseconds} ms with the pump still draining");
        output.WriteLine($"  [{(childDied ? "PASS" : "FAIL")}] the still-running child (pid={observer.Id}) ended after Dispose (hasExited={observer.HasExited})");
        var fastEnough = stopwatch.ElapsedMilliseconds < 5000;
        output.WriteLine($"  [{(fastEnough ? "PASS" : "FAIL")}] Dispose() did not block (<5s)");
        return childDied && fastEnough;
    }

    private static bool RunSuspendedCheck(TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("== check 3/6: CREATE_SUSPENDED + ResumeMainThread (P2-T3's job-assignment window) ==");

        using var session = ConPtySession.Start(new ConPtyLaunchSpec
        {
            CommandLine = "cmd.exe /c exit 3",
            CreateSuspended = true,
        });

        var pump = OutputPump.Start(session);
        try
        {
            var exitedWhileSuspended = session.WaitForExit(TimeSpan.FromMilliseconds(400));
            output.WriteLine($"  [{(!exitedWhileSuspended ? "PASS" : "FAIL")}] suspended child had not run after 400ms (exited={exitedWhileSuspended})");

            session.ResumeMainThread();
            var exited = session.WaitForExit(TimeSpan.FromSeconds(10));
            var exitCode = exited ? session.TryGetExitCode() : null;
            output.WriteLine($"  [{(exited && exitCode == 3 ? "PASS" : "FAIL")}] after ResumeMainThread the child ran and exited (exited={exited}, exitCode={exitCode?.ToString() ?? "n/a"}, expected 3)");
            return !exitedWhileSuspended && exited && exitCode == 3;
        }
        finally
        {
            session.Dispose();
            pump.Join(TimeSpan.FromSeconds(5));
        }
    }

    private static bool RunFailurePathLeakCheck(TextWriter output)
    {
        const int iterations = 20;
        output.WriteLine();
        output.WriteLine($"== check 4/6: failure path x{iterations} (CreatePseudoConsole succeeds, CreateProcessW fails) ==");

        var before = StableHandleCount();
        var errors = 0;
        ConPtyException? sample = null;
        for (var i = 0; i < iterations; i++)
        {
            try
            {
                using var _ = ConPtySession.Start(new ConPtyLaunchSpec
                {
                    CommandLine = @"C:\this-image-does-not-exist-accel-p2t2.exe",
                });
            }
            catch (ConPtyException ex)
            {
                errors++;
                sample ??= ex;
            }
        }

        var after = StableHandleCount();
        output.WriteLine($"  threw ConPtyException {errors}/{iterations} times; sample: {sample?.Operation} / NativeErrorCode={sample?.NativeErrorCode} / {sample?.Message}");
        output.WriteLine($"  handles before={before} after={after} delta={after - before}");
        var ok = errors == iterations && after - before <= 8;
        output.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] every failed launch threw with an OS error code and no handles accumulated");
        return ok;
    }

    private static bool RunLeakCheck(TextWriter output, int iterations)
    {
        output.WriteLine();
        output.WriteLine($"== check 5/6: {iterations}x open/launch/drain/dispose leak check ==");

        // One warm-up cycle first: the very first pseudoconsole in a process loads conhost/OpenConsole
        // and its dependencies, which legitimately and permanently adds a few handles. Counting that as
        // a "leak" would just add noise.
        RunOneCycle();

        var before = StableHandleCount();
        var beforeChildren = ChildProcessCount();
        var samples = new List<int>();
        var cyclesWithCleanExit = 0;
        for (var i = 0; i < iterations; i++)
        {
            if (RunOneCycle())
            {
                cyclesWithCleanExit++;
            }

            if ((i + 1) % 10 == 0)
            {
                samples.Add(StableHandleCount());
            }
        }

        var after = StableHandleCount();
        var afterChildren = ChildProcessCount();

        output.WriteLine($"  handle count before={before} after={after} delta={after - before}");
        output.WriteLine($"  samples every 10 iterations: {string.Join(", ", samples)}");
        output.WriteLine($"  cycles whose child exited with the expected code before Dispose: {cyclesWithCleanExit}/{iterations}");
        output.WriteLine($"  cmd/conhost/OpenConsole process count on this machine before={beforeChildren} after={afterChildren} delta={afterChildren - beforeChildren} (informational: machine-wide, other processes' consoles are counted too)");

        // A real per-iteration leak of even one handle or one pty host would show up as a delta near
        // `iterations`. The thresholds are deliberately far below that so a trend cannot hide, but above
        // GC/threadpool/other-process noise. Every child is separately proven dead by its own
        // WaitForExit + exit code, which is the attributable check; the machine-wide process count is
        // only a coarse backstop for a leaked pty host.
        // Integer division on a small iteration count makes this threshold too tight (e.g. at
        // iterations=10, iterations/4=2, and the one-time first-pseudoconsole warm-up cost alone is
        // ~2) - floor it so a real per-iteration leak (which scales with iterations) still trips it
        // while the fixed warm-up cost never does, regardless of how many iterations were requested.
        var threshold = Math.Max(iterations / 4, 4);
        var handleDelta = after - before;
        var childDelta = afterChildren - beforeChildren;
        var ok = handleDelta < threshold && childDelta < threshold && cyclesWithCleanExit == iterations;
        output.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] no handle-count trend, no pty-host trend, every child reaped");
        return ok;
    }

    /// <summary>Regression check for the one exit code <c>GetExitCodeProcess</c> cannot report on its
    /// own: 259 is both a legal exit code and the value of <c>STILL_ACTIVE</c>, so a child that really
    /// exits with 259 used to be reported as "still running" forever - which on the P3-T2 teardown path
    /// (wait gracefully, then force) would mean always waiting out the timeout and then killing an
    /// already-dead process. Only reachable with a real child, hence here rather than in xUnit.</summary>
    private static bool RunStillActiveExitCodeCheck(TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("== check 6/6: exit code 259 (== STILL_ACTIVE) is reported as an exit, not as 'running' ==");

        var session = ConPtySession.Start(new ConPtyLaunchSpec
        {
            CommandLine = "cmd.exe /c exit 259",
        });
        var pump = OutputPump.Start(session);
        try
        {
            var exited = session.WaitForExit(TimeSpan.FromSeconds(10));
            var exitCode = session.TryGetExitCode();
            var ok = exited && exitCode == 259;
            output.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] WaitForExit={exited}, TryGetExitCode={exitCode?.ToString() ?? "null (still running)"}, expected 259");
            return ok;
        }
        finally
        {
            session.Dispose();
            pump.Join(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>One full lifecycle: create pipes + pseudoconsole + child, drain its output on a
    /// background thread, wait for it, dispose. Returns true if the child exited with the exit code it
    /// was asked for - i.e. this specific child is provably reaped, which is stronger than counting
    /// processes machine-wide.</summary>
    private static bool RunOneCycle()
    {
        var session = ConPtySession.Start(new ConPtyLaunchSpec
        {
            CommandLine = "cmd.exe /c exit 7",
            Columns = 80,
            Rows = 25,
        });
        var pump = OutputPump.Start(session);
        try
        {
            return session.WaitForExit(TimeSpan.FromSeconds(10)) && session.TryGetExitCode() == 7;
        }
        finally
        {
            session.Dispose();
            pump.Join(TimeSpan.FromSeconds(5));
        }
    }

    private static void Send(ConPtySession session, string command)
    {
        // Raw bytes: this class writes the terminal's stdin, it does not own any text protocol.
        var bytes = Encoding.UTF8.GetBytes(command + "\r");
        session.InputStream.Write(bytes, 0, bytes.Length);
        session.InputStream.Flush();
    }

    private static int StableHandleCount()
    {
        // Force finalization first, so a handle that is merely awaiting its finalizer is not counted as
        // a leak - and, conversely, so a handle that survives finalization definitely IS one.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var self = Process.GetCurrentProcess();
        self.Refresh();
        return self.HandleCount;
    }

    private static int ChildProcessCount()
    {
        var count = 0;
        foreach (var name in new[] { "cmd", "OpenConsole", "conhost" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                count++;
                process.Dispose();
            }
        }

        return count;
    }

    /// <summary>
    /// The dedicated background reader this class's contract requires. Deliberately lives here and not
    /// in <c>ConPty.cs</c>: the real pump (async, backpressure, stateful UTF-8 decoding) is P2-T3's
    /// <c>PtySession</c>. This one just accumulates raw bytes so the diagnostic can assert on them.
    /// </summary>
    private sealed class OutputPump
    {
        private readonly object _gate = new();
        private readonly MemoryStream _buffer = new();
        private readonly Thread _thread;

        private OutputPump(ConPtySession session)
        {
            _thread = new Thread(() => PumpLoop(session))
            {
                IsBackground = true,
                Name = "pty-smoke-test-output-pump",
            };
        }

        public bool SawEof { get; private set; }

        public long BytesRead { get; private set; }

        public Exception? Error { get; private set; }

        public static OutputPump Start(ConPtySession session)
        {
            var pump = new OutputPump(session);
            pump._thread.Start();
            return pump;
        }

        public void Join(TimeSpan timeout) => _thread.Join(timeout);

        public int CountOccurrences(string needle)
        {
            var text = Snapshot();
            var count = 0;
            var index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        public bool WaitForOccurrences(string needle, int minimum, TimeSpan timeout) =>
            WaitFor(() => CountOccurrences(needle) >= minimum, timeout);

        public bool WaitForRegex(string pattern, TimeSpan timeout) =>
            WaitFor(() => System.Text.RegularExpressions.Regex.IsMatch(Snapshot(), pattern), timeout);

        public IEnumerable<string> TailForDisplay(int bytes)
        {
            var text = Snapshot();
            var tail = text.Length <= bytes ? text : text[^bytes..];
            return tail
                .Replace("\u001b", "<ESC>", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Length > 0);
        }

        private bool WaitFor(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(25);
            }

            return condition();
        }

        private string Snapshot()
        {
            lock (_gate)
            {
                // Decoding happens HERE, in the diagnostic, purely so it can match text markers - never
                // in ConPty.cs, whose contract is raw bytes.
                return Encoding.UTF8.GetString(_buffer.GetBuffer(), 0, (int)_buffer.Length);
            }
        }

        private void PumpLoop(ConPtySession session)
        {
            var chunk = new byte[4096];
            try
            {
                while (true)
                {
                    var read = session.OutputStream.Read(chunk, 0, chunk.Length);
                    if (read <= 0)
                    {
                        SawEof = true;
                        return;
                    }

                    lock (_gate)
                    {
                        _buffer.Write(chunk, 0, read);
                        BytesRead += read;
                    }
                }
            }
            catch (Exception ex)
            {
                // A broken pipe / closed handle at teardown is the normal end of this loop, not a bug:
                // Dispose closes the read end after ClosePseudoConsole, which races this read by design.
                SawEof = true;
                Error = ex;
            }
        }
    }
}

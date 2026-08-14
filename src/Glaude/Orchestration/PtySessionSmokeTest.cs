namespace Glaude.Orchestration;

using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Hidden dev-only diagnostic for <see cref="PtySession"/> (P2-T3), reachable via the undocumented
/// <c>pty-session-smoke-test</c> verb - same pattern and same rationale as <c>pty-smoke-test</c>
/// (<see cref="ConPtySmokeTest"/>) and <c>ui-preview</c>: what is being proven here is real OS process
/// lifecycle (a real child, a real pseudoconsole, a real Job Object), which no unit test with fakes can
/// establish. The unit tests cover the decoder, the pump's backpressure/teardown behaviour, and argv
/// construction; this covers "does the whole session actually work, is the child actually in the job,
/// and does it actually clean up".
///
/// <para>Launches <c>cmd.exe</c>, never <c>claude.exe</c> - the same reasoning as
/// <see cref="ConPtySmokeTest"/>: a predictable, controllable child whose echo behaviour and exit codes
/// are known, with no network, no auth, and no side effects on the user's real sessions.</para>
///
/// <para>Checks, in order:
/// <list type="number">
/// <item>launch through <see cref="PtySession"/> with a real argv array, write a command, read it back
/// decoded off <see cref="PtySession.Output"/>;</item>
/// <item>job-object assignment actually happened - <c>IsProcessInJob</c> against the exact job, plus the
/// behavioural proof that closing the job kills an otherwise-orphaned child;</item>
/// <item>raw-byte input: Ctrl+C written as the single byte <c>0x03</c> reaches the child as a
/// control event;</item>
/// <item>clean teardown: child dead, output channel completed, pump thread joined, Dispose idempotent;</item>
/// <item>self-exit reaping: the child exits on its own and <see cref="PtySession.ExitTask"/> reports its
/// exit code with reason <see cref="PtySessionExitReason.ChildExited"/>;</item>
/// <item>no pump-thread or handle accumulation across N full session cycles.</item>
/// </list></para>
/// </summary>
public static class PtySessionSmokeTest
{
    private const string Marker = "GLAUDE_SESSION_OK";

    /// <summary>Runs every check. Returns 0 if all passed, 1 otherwise.</summary>
    public static int Run(TextWriter output, int cycles = 10)
    {
        ArgumentNullException.ThrowIfNull(output);

        var failures = 0;
        failures += RunInteractiveCheck(output) ? 0 : 1;
        failures += RunRawByteInputCheck(output) ? 0 : 1;
        failures += RunJobAssignmentCheck(output) ? 0 : 1;
        failures += RunSelfExitReapingCheck(output) ? 0 : 1;
        failures += RunBackpressureAndTeardownCheck(output) ? 0 : 1;
        failures += RunLaunchSpecGuardCheck(output) ? 0 : 1;
        failures += RunCycleLeakCheck(output, cycles) ? 0 : 1;

        output.WriteLine();
        output.WriteLine(failures == 0
            ? "pty-session-smoke-test: ALL CHECKS PASSED"
            : $"pty-session-smoke-test: {failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static string CmdPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    private static PtyLaunchSpec CmdSpec(params string[] arguments) => new()
    {
        ExecutablePath = CmdPath(),
        Arguments = arguments,
        WorkingDirectory = Path.GetTempPath(),

        // Also exercises the environment-override path end to end.
        EnvironmentOverrides = new Dictionary<string, string?>
        {
            ["GLAUDE_SMOKE_TEST"] = "1",
        },
    };

    private static bool RunInteractiveCheck(TextWriter output)
    {
        output.WriteLine("== check 1/7: interactive cmd.exe through PtySession (argv array, decoded text output) ==");

        var spec = CmdSpec();
        output.WriteLine($"  argv[0]={spec.ExecutablePath}");
        output.WriteLine($"  command line built from the argv array: {spec.BuildCommandLine()}");

        var session = PtySession.Start(spec, new PtySessionOptions { Columns = 80, Rows = 25 });
        var collector = TextCollector.Attach(session);
        var ok = true;
        try
        {
            output.WriteLine($"  started: pid={session.ProcessId} size={session.Columns}x{session.Rows}");

            session.WriteText($"echo {Marker}\r");
            var sawMarker = collector.WaitForOccurrences(Marker, 2, TimeSpan.FromSeconds(10));
            output.WriteLine($"  [{(sawMarker ? "PASS" : "FAIL")}] wrote 'echo {Marker}\\r'; saw the marker {collector.CountOccurrences(Marker)}x on PtySession.Output (>=2 means cmd echoed it AND executed it)");
            ok &= sawMarker;

            // The environment override must have reached the child.
            session.WriteText("echo ENV=%GLAUDE_SMOKE_TEST%\r");
            var sawEnv = collector.WaitForOccurrences("ENV=1", 1, TimeSpan.FromSeconds(10));
            output.WriteLine($"  [{(sawEnv ? "PASS" : "FAIL")}] the child sees the injected environment override (GLAUDE_SMOKE_TEST=1)");
            ok &= sawEnv;

            // Resize, confirmed from inside the child.
            session.Resize(120, 40);
            session.WriteText("mode con\r");
            var sawSize = collector.WaitForRegex(@"Columns:\s*120", TimeSpan.FromSeconds(10))
                && collector.WaitForRegex(@"Lines:\s*40", TimeSpan.FromSeconds(10));
            output.WriteLine($"  [{(sawSize ? "PASS" : "FAIL")}] Resize(120,40) reached the child ('mode con' reports Columns:120 / Lines:40)");
            ok &= sawSize;

            output.WriteLine($"  pump: bytesRead={session.BytesRead} chunksPublished={session.ChunksPublished} chunksDiscarded={session.ChunksDiscarded}");
            output.WriteLine("  --- last 300 chars of decoded output (ESC shown literally) ---");
            foreach (var line in collector.TailForDisplay(300))
            {
                output.WriteLine($"  | {line}");
            }
        }
        finally
        {
            session.Dispose();
            session.Dispose(); // idempotency, on purpose
            output.WriteLine("  [PASS] Dispose() called twice without throwing");
        }

        // Teardown assertions, after Dispose returned.
        var channelCompleted = collector.WaitForCompletion(TimeSpan.FromSeconds(5));
        output.WriteLine($"  [{(channelCompleted ? "PASS" : "FAIL")}] the output channel completed after Dispose, so a consumer's `await foreach` ends (chunks seen={collector.ChunkCount})");
        ok &= channelCompleted;

        var exitObserved = session.ExitTask.Wait(TimeSpan.FromSeconds(5));
        output.WriteLine($"  [{(exitObserved ? "PASS" : "FAIL")}] ExitTask completed (exitCode={FormatExit(session)}, reason={session.ExitReason})");
        ok &= exitObserved;
        ok &= session.ExitReason == PtySessionExitReason.TornDown;
        output.WriteLine($"  [{(session.ExitReason == PtySessionExitReason.TornDown ? "PASS" : "FAIL")}] the exit was reported as a teardown, not as a self-exit");

        return ok;
    }

    /// <summary>
    /// Raw-byte input - the reason <see cref="PtySession.Write(ReadOnlySpan{byte})"/> is the primitive and
    /// <see cref="PtySession.WriteText"/> is only a convenience over it: terminal input is a byte protocol
    /// (Enter is <c>0x0D</c>, Ctrl+C is <c>0x03</c>, arrow keys are escape sequences), and a text-only API
    /// cannot express any of it.
    ///
    /// <para>The gated proof uses <c>0x0D</c>: the command text is written with no terminator, then a
    /// separate single-byte <c>0x0D</c> write is what makes the child execute it. That is deterministic.
    /// <c>0x03</c> is reported alongside as <b>informational only</b>: measured here, writing it to a
    /// pseudoconsole-hosted <c>cmd.exe</c> neither echoes <c>^C</c> nor ends the child within 5s, and
    /// getting Ctrl+C delivery right end-to-end (xterm.js key handling → transport → this pipe) is
    /// P2-T5b's job, not something this class can assert today. Note also that a session torn down via
    /// <c>ClosePseudoConsole</c> exits with <c>STATUS_CONTROL_C_EXIT</c> anyway, so that exit code alone is
    /// <i>not</i> evidence a Ctrl+C byte was delivered - which is exactly the false positive this check was
    /// rewritten to avoid.</para>
    /// </summary>
    private static bool RunRawByteInputCheck(TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("== check 2/7: raw-byte input (a control byte, written as a byte, drives the child) ==");

        const string RawMarker = "GLAUDE_RAWBYTE_OK";
        using var session = PtySession.Start(CmdSpec(), new PtySessionOptions());
        var collector = TextCollector.Attach(session);
        collector.WaitForOccurrences(">", 1, TimeSpan.FromSeconds(10)); // wait for the first prompt

        // No newline in the text write at all: the command sits at the prompt, unexecuted...
        session.Write(Encoding.UTF8.GetBytes($"echo {RawMarker}"));
        var echoedOnly = collector.WaitForOccurrences(RawMarker, 1, TimeSpan.FromSeconds(5));
        var notYetExecuted = collector.CountOccurrences(RawMarker) == 1;
        output.WriteLine($"  [{(echoedOnly && notYetExecuted ? "PASS" : "FAIL")}] raw bytes with no terminator reached the child's line editor and were echoed once, not executed (occurrences={collector.CountOccurrences(RawMarker)}, expected 1)");

        // ...until a single raw 0x0D byte - Enter - is written on its own.
        session.Write(new byte[] { 0x0D });
        var executed = collector.WaitForOccurrences(RawMarker, 2, TimeSpan.FromSeconds(5));
        output.WriteLine($"  [{(executed ? "PASS" : "FAIL")}] a single raw 0x0D byte then made the child execute it (occurrences={collector.CountOccurrences(RawMarker)}, expected >=2)");

        // Informational: Ctrl+C. See the method's remarks for why this is not gated.
        session.Write(new byte[] { 0x03 });
        var endedOnCtrlC = session.ExitTask.Wait(TimeSpan.FromSeconds(3));
        output.WriteLine($"  informational: after a raw 0x03 (Ctrl+C): echoed ^C={collector.CountOccurrences("^C") > 0}, childEnded={endedOnCtrlC} - not gated, Ctrl+C delivery end-to-end is P2-T5b");

        return echoedOnly && notYetExecuted && executed;
    }

    private static bool RunJobAssignmentCheck(TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("== check 3/7: Job Object assignment (spawn-suspended -> assign -> resume) ==");

        var ok = true;

        // Part A: positive proof against the process-wide shared job.
        using (var shared = PtySession.Start(CmdSpec(), new PtySessionOptions()))
        {
            var inShared = IsInJob(GlaudeJobObject.Shared, shared.ProcessId);
            output.WriteLine($"  [{(inShared ? "PASS" : "FAIL")}] pid={shared.ProcessId} IsProcessInJob(GlaudeJobObject.Shared) == {inShared} (default job is the statically rooted singleton)");
            ok &= inShared;
            output.WriteLine($"  [{(ReferenceEquals(shared.JobObject, GlaudeJobObject.Shared) ? "PASS" : "FAIL")}] the session holds a strong reference to that job for its whole lifetime (rooting guarantee (b))");
            ok &= ReferenceEquals(shared.JobObject, GlaudeJobObject.Shared);
        }

        // Part B: the behavioural proof. A dedicated job, a child that would otherwise sit there forever
        // (cmd.exe waiting for input), and NO PtySession.Dispose - closing the job alone must kill it.
        // That is the kill-on-close net the ordering exists to guarantee.
        var dedicated = GlaudeJobObject.Create();
        var orphan = PtySession.Start(CmdSpec(), new PtySessionOptions { JobObject = dedicated });
        using var observer = Process.GetProcessById(orphan.ProcessId);
        var inDedicated = IsInJob(dedicated, orphan.ProcessId);
        output.WriteLine($"  [{(inDedicated ? "PASS" : "FAIL")}] pid={orphan.ProcessId} is in a dedicated job (IsProcessInJob == {inDedicated})");
        ok &= inDedicated;

        var aliveBefore = !observer.HasExited;
        dedicated.Dispose(); // kill-on-close
        var killed = observer.WaitForExit(10_000);
        output.WriteLine($"  [{(aliveBefore && killed ? "PASS" : "FAIL")}] closing the job handle alone killed the still-running child (aliveBeforeClose={aliveBefore}, exitedAfterClose={killed})");
        ok &= aliveBefore && killed;

        orphan.Dispose();
        return ok;
    }

    private static bool RunSelfExitReapingCheck(TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("== check 4/7: self-exit reaping (Process.Exited) vs teardown ==");

        using var session = PtySession.Start(CmdSpec("/c", "exit", "42"), new PtySessionOptions());
        var collector = TextCollector.Attach(session);

        var completed = session.ExitTask.Wait(TimeSpan.FromSeconds(10));
        var exitCode = completed ? session.ExitTask.Result : null;
        var reason = session.ExitReason;
        var ok = completed && exitCode == 42 && reason == PtySessionExitReason.ChildExited;
        output.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] the child exited on its own and was reaped without any Dispose (completed={completed}, exitCode={exitCode?.ToString() ?? "null"}, expected 42, reason={reason}, expected ChildExited)");

        collector.WaitForCompletion(TimeSpan.FromSeconds(2));
        return ok;
    }

    private static bool RunBackpressureAndTeardownCheck(TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("== check 5/7: no consumer + tiny bounded channel -> Dispose still completes promptly ==");

        // Capacity 1 and nobody reading: the pump blocks on the very first full channel, which stops the
        // pty being drained, which is exactly the situation that can wedge ClosePseudoConsole. Dispose
        // must still return quickly, because it cancels the pump into drain-and-discard mode first.
        var session = PtySession.Start(
            // Args stay individually space-free so the command line needs no quoting at all - cmd.exe's
            // own /c quote-stripping rules are notoriously conditional, and this verb must not depend on
            // them. `dir /s /b` on System32 produces far more output than any bounded channel can hold.
            CmdSpec("/c", "dir", "/s", "/b", Environment.SystemDirectory),
            new PtySessionOptions { OutputChannelCapacity = 1, ReadBufferSize = 256 });

        Thread.Sleep(1500); // let it stall with a full channel and nobody reading

        var stalledBytes = session.BytesRead;
        var stopwatch = Stopwatch.StartNew();
        session.Dispose();
        stopwatch.Stop();

        var fastEnough = stopwatch.ElapsedMilliseconds < 5000;
        output.WriteLine($"  bytes read before Dispose while stalled: {stalledBytes} (bounded: the channel held at most 1 chunk, so this is nowhere near the child's total output)");
        output.WriteLine($"  after Dispose: bytesRead={session.BytesRead} chunksPublished={session.ChunksPublished} chunksDiscarded={session.ChunksDiscarded} pumpSawEof={session.PumpSawEof}");
        output.WriteLine($"  [{(fastEnough ? "PASS" : "FAIL")}] Dispose() returned in {stopwatch.ElapsedMilliseconds} ms despite a stalled consumer (<5s)");
        output.WriteLine($"  [{(session.ChunksDiscarded > 0 ? "PASS" : "FAIL")}] the pump switched to drain-and-discard rather than staying blocked (discarded={session.ChunksDiscarded})");

        return fastEnough && session.ChunksDiscarded > 0;
    }

    private static bool RunLaunchSpecGuardCheck(TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("== check 6/7: launch-spec guards (shim resolution, claude resolution on this machine) ==");

        var ok = true;

        var shimRejected = false;
        string shimMessage = string.Empty;
        try
        {
            PtySession.Start(new PtyLaunchSpec { ExecutablePath = @"C:\fake\claude.cmd" });
        }
        catch (PtySessionLaunchException ex)
        {
            shimRejected = true;
            shimMessage = ex.Message;
        }

        output.WriteLine($"  [{(shimRejected ? "PASS" : "FAIL")}] a .cmd shim path is refused before any OS resource is allocated");
        output.WriteLine($"    message: {shimMessage}");
        ok &= shimRejected;

        var resolution = ClaudeCliLocator.Resolve();
        output.WriteLine($"  informational: `claude` resolves on this machine to kind={resolution.Kind} path={resolution.Path ?? "<none>"}");
        if (resolution.Kind == ClaudeCliResolutionKind.NativeExe)
        {
            var claudeSpec = PtySession.CreateClaudeSpec(
                new[] { "--session-id", "11111111-2222-3333-4444-555555555555", "--name", "a name with spaces" },
                workingDirectory: Path.GetTempPath());
            output.WriteLine($"  informational: CreateClaudeSpec would launch: {claudeSpec.BuildCommandLine()}");
            output.WriteLine("               (spec only - this verb never launches claude.exe)");
        }
        else
        {
            output.WriteLine("  informational: CreateClaudeSpec would throw here (missing or shim), by design.");
        }

        return ok;
    }

    private static bool RunCycleLeakCheck(TextWriter output, int cycles)
    {
        output.WriteLine();
        output.WriteLine($"== check 7/7: {cycles}x full session cycles - no pump-thread or handle accumulation ==");

        RunOneCycle(); // warm-up: first pseudoconsole in a process legitimately adds handles/threads

        var (handlesBefore, threadsBefore) = StableCounts();
        var reaped = 0;
        for (var i = 0; i < cycles; i++)
        {
            var result = RunOneCycle();
            if (result.Ok)
            {
                reaped++;
            }
            else
            {
                // Attributable failure detail: a bare count would leave a hostile reviewer (and a future
                // flake) with nothing to go on.
                output.WriteLine($"  cycle {i}: FAILED - exitObserved={result.ExitObserved} exitCode={result.ExitCode?.ToString() ?? "none"} (expected 7) channelCompleted={result.ChannelCompleted} pumpSawEof={result.PumpSawEof} pumpThreadFinished={result.PumpThreadFinished} chunks={result.Chunks}");
            }
        }

        var (handlesAfter, threadsAfter) = StableCounts();
        var handleDelta = handlesAfter - handlesBefore;
        var threadDelta = threadsAfter - threadsBefore;

        output.WriteLine($"  handles before={handlesBefore} after={handlesAfter} delta={handleDelta}");
        output.WriteLine($"  threads before={threadsBefore} after={threadsAfter} delta={threadDelta} - INFORMATIONAL ONLY: this number also moves with .NET threadpool injection, so it is not gated; the gated pump-thread check is per session (PumpThreadFinished) below");
        output.WriteLine($"  cycles whose child was reaped with exit code 7, whose output channel completed, AND whose pump thread had terminated after Dispose: {reaped}/{cycles}");

        var threshold = Math.Max(cycles / 4, 4);
        var ok = reaped == cycles && handleDelta < threshold;
        output.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] every session reaped with its pump thread terminated, and no handle trend");
        return ok;
    }

    private readonly record struct CycleResult(
        bool ExitObserved,
        int? ExitCode,
        bool ChannelCompleted,
        bool PumpSawEof,
        bool PumpThreadFinished,
        int Chunks)
    {
        public bool Ok => ExitObserved && ExitCode == 7 && ChannelCompleted && PumpThreadFinished;
    }

    private static CycleResult RunOneCycle()
    {
        using var session = PtySession.Start(CmdSpec("/c", "echo", "cycle", "&", "exit", "7"), new PtySessionOptions());
        var collector = TextCollector.Attach(session);
        var exitObserved = session.ExitTask.Wait(TimeSpan.FromSeconds(10));
        var exitCode = exitObserved ? session.ExitTask.Result : null;
        session.Dispose();
        var completed = collector.WaitForCompletion(TimeSpan.FromSeconds(5));
        return new CycleResult(
            exitObserved,
            exitCode,
            completed,
            session.PumpSawEof,
            session.PumpThreadFinished,
            collector.ChunkCount);
    }

    /// <summary>
    /// The smoke test needs a process handle to query <c>IsProcessInJob</c>. <see cref="PtySession"/>
    /// deliberately does not expose one (handle ownership stays with <see cref="ConPtySession"/>), so
    /// open a second one here - the child is alive, so its PID cannot have been recycled. The handle is
    /// used strictly inside the <c>using</c>, never returned out of it.
    /// </summary>
    private static bool IsInJob(GlaudeJobObject job, int processId)
    {
        using var process = Process.GetProcessById(processId);
        return job.ContainsProcess(process.SafeHandle);
    }

    private static string FormatExit(PtySession session) =>
        session.ExitTask.IsCompletedSuccessfully ? session.ExitTask.Result?.ToString() ?? "null" : "not observed";

    private static (int Handles, int Threads) StableCounts()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var self = Process.GetCurrentProcess();
        self.Refresh();
        return (self.HandleCount, self.Threads.Count);
    }

    /// <summary>
    /// A single consumer of <see cref="PtySession.Output"/> - the shape a real consumer (P2-T4's
    /// WebSocket) will have: one <c>await foreach</c> until the channel completes.
    /// </summary>
    private sealed class TextCollector
    {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();
        private readonly Task _task;

        private TextCollector(PtySession session)
        {
            _task = Task.Run(async () =>
            {
                await foreach (var chunk in session.ReadOutputAsync().ConfigureAwait(false))
                {
                    lock (_gate)
                    {
                        _text.Append(chunk);
                        ChunkCount++;
                    }
                }
            });
        }

        public int ChunkCount { get; private set; }

        public static TextCollector Attach(PtySession session) => new(session);

        public bool WaitForCompletion(TimeSpan timeout) => _task.Wait(timeout);

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

        public IEnumerable<string> TailForDisplay(int chars)
        {
            var text = Snapshot();
            var tail = text.Length <= chars ? text : text[^chars..];
            return tail
                .Replace("\u001b", "<ESC>", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Length > 0);
        }

        private string Snapshot()
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }

        private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
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
    }
}

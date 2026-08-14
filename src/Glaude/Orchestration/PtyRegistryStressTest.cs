namespace Glaude.Orchestration;

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Hidden dev-only diagnostic for <see cref="PtyRegistry"/> (P3-T2), reachable via the undocumented
/// <c>pty-registry-stress-test</c> verb - same pattern and rationale as <c>pty-session-smoke-test</c>
/// (<see cref="PtySessionSmokeTest"/>): the properties at stake here are concurrency and OS-resource
/// ownership under real load, and a registry bug of that kind passes a casual unit test and fails when N
/// real children are torn down at once from N threads. The unit suite covers the deterministic logic
/// (self-exit auto-removal, unknown-tab no-op, enumeration, the timeout → force-kill escalation via a fake
/// observer); this covers "does it actually hold up, and does it leak".
///
/// <para>Launches <c>cmd.exe</c>, never <c>claude.exe</c> - same reasoning as the other smoke tests: a
/// controllable child with known exit codes, no auth, no network, no effect on the user's real sessions.</para>
///
/// <para>Checks, in order:
/// <list type="number">
/// <item>self-exit auto-removal: a child that ends by itself leaves the registry on its own and surfaces
/// <see cref="PtyRegistry.SessionEnded"/> with <see cref="PtySessionExitReason.ChildExited"/>, with no
/// close ever requested;</item>
/// <item>the concurrent storm: N tabs, closed simultaneously from many tasks, with a third of the tabs
/// closed <i>twice</i> concurrently and a third of the children exiting on their own right before/during
/// the close - asserting no escaped exception, one teardown per tab (duplicate closers get the identical
/// result instance), exactly one <see cref="PtyRegistry.SessionEnded"/> per tab, and an empty registry;</item>
/// <item>leak accounting for that storm: process handle count and <c>cmd.exe</c> process count, before and
/// after;</item>
/// <item>the force-kill escalation on real processes (<c>CloseTimeout</c> = 0, so the wait never
/// succeeds and every close escalates to <c>Process.Kill(entireProcessTree: true)</c>);</item>
/// <item><see cref="PtyRegistry.CloseAllAsync"/> concurrency: N tabs must cost about one timeout, not N of
/// them;</item>
/// <item><see cref="PtyRegistry.Dispose"/>: closes everything synchronously and then refuses further
/// registrations.</item>
/// </list></para>
/// </summary>
public static class PtyRegistryStressTest
{
    /// <summary>Runs every check. Returns 0 if all passed, 1 otherwise.</summary>
    public static int Run(TextWriter output, int tabs = 30)
    {
        ArgumentNullException.ThrowIfNull(output);
        tabs = Math.Max(tabs, 4);

        var failures = 0;
        failures += RunSelfExitCheck(output) ? 0 : 1;
        failures += RunConcurrentStormCheck(output, tabs) ? 0 : 1;
        failures += RunForceKillCheck(output) ? 0 : 1;
        failures += RunCloseAllConcurrencyCheck(output, tabs) ? 0 : 1;
        failures += RunDisposeCheck(output) ? 0 : 1;

        output.WriteLine();
        output.WriteLine(failures == 0
            ? "pty-registry-stress-test: ALL CHECKS PASSED"
            : $"pty-registry-stress-test: {failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static string CmdPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    /// <summary>An interactive <c>cmd.exe</c>: it sits at its prompt forever, so it only ever goes away
    /// because the registry tore it down. That is the shape that can leak.</summary>
    private static PtySession StartIdleChild() => PtySession.Start(
        new PtyLaunchSpec { ExecutablePath = CmdPath(), WorkingDirectory = Path.GetTempPath() },
        new PtySessionOptions { OutputChannelCapacity = 8, ReadBufferSize = 512 });

    /// <summary>A <c>cmd.exe</c> that exits on its own almost immediately - the self-exit racer.</summary>
    private static PtySession StartSelfExitingChild(int exitCode) => PtySession.Start(
        new PtyLaunchSpec
        {
            ExecutablePath = CmdPath(),
            Arguments = new[] { "/c", "exit", exitCode.ToString() },
            WorkingDirectory = Path.GetTempPath(),
        },
        new PtySessionOptions { OutputChannelCapacity = 8, ReadBufferSize = 512 });

    /// <summary>A <c>cmd.exe</c> that is busy for a minute in a grandchild process (<c>ping -n 60</c>) - the
    /// shape that reaches the force-kill branch, and that gives <c>entireProcessTree: true</c> a descendant
    /// to kill.</summary>
    private static PtySession StartBusyChild() => PtySession.Start(
        new PtyLaunchSpec
        {
            ExecutablePath = CmdPath(),
            Arguments = new[] { "/c", "ping", "-n", "60", "127.0.0.1" },
            WorkingDirectory = Path.GetTempPath(),
        },
        new PtySessionOptions { OutputChannelCapacity = 8, ReadBufferSize = 512 });

    private static bool RunSelfExitCheck(TextWriter output)
    {
        output.WriteLine("== check 1/5: self-exit auto-removal (no close ever requested) ==");

        using var registry = new PtyRegistry();
        var ended = new BlockingCollection<PtySessionEndedEventArgs>();
        registry.SessionEnded += (_, e) => ended.Add(e);

        var tabId = Guid.NewGuid().ToString("N");
        registry.Register(tabId, StartSelfExitingChild(5));
        output.WriteLine($"  registered tabId={tabId} for a `cmd /c exit 5` child; nobody will call CloseAsync");

        var fired = ended.TryTake(out var args, TimeSpan.FromSeconds(15));
        var ok = fired
            && args!.TabId == tabId
            && args.Reason == PtySessionExitReason.ChildExited
            && args.ExitCode == 5;
        output.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] SessionEnded fired for the self-exit (fired={fired}, tabId={args?.TabId}, reason={args?.Reason} expected ChildExited, exitCode={args?.ExitCode} expected 5, outcome={args?.Outcome})");

        var emptied = WaitFor(() => registry.Count == 0, TimeSpan.FromSeconds(5));
        output.WriteLine($"  [{(emptied ? "PASS" : "FAIL")}] the dead session removed itself from the registry (Count={registry.Count}, TryGet={registry.TryGet(tabId, out _)})");

        // The registry still disposed it, so no pty handles/pump thread are left behind by a session whose
        // child died on its own - the reason self-exit runs the full close pipeline rather than just a
        // dictionary removal.
        var closedAgain = registry.CloseAsync(tabId).GetAwaiter().GetResult();
        var noop = closedAgain.Outcome == PtyCloseOutcome.NotFound;
        output.WriteLine($"  [{(noop ? "PASS" : "FAIL")}] closing that tabId afterwards is a NotFound no-op (outcome={closedAgain.Outcome})");

        return ok && emptied && noop;
    }

    /// <summary>
    /// The storm, run twice: once as a warm-up and once measured. Running it twice is what makes the handle
    /// number meaningful - a first storm legitimately grows the process (thread-pool worker injection for N
    /// concurrent blocking teardowns, the first pseudoconsole's plumbing, JIT), and those are one-time costs
    /// that never come back. A per-session <i>leak</i>, by contrast, is paid again on every round, so it
    /// shows up as a delta on the second round; one-time growth does not.
    /// </summary>
    private static bool RunConcurrentStormCheck(TextWriter output, int tabs)
    {
        output.WriteLine();
        output.WriteLine($"== check 2/5 + 3/5: {tabs} tabs closed concurrently from {tabs}+ tasks (duplicates + self-exit racers), with leak accounting ==");

        // Enough pool threads for N concurrent closes (each one's PtySession.Dispose blocks while it joins
        // its pump thread). Without this the pool injects threads at ~1/second and the storm serialises -
        // which would still pass, but would no longer be testing simultaneity.
        ThreadPool.GetMinThreads(out var minWorker, out var minIo);
        ThreadPool.SetMinThreads(Math.Max(minWorker, (tabs * 2) + 16), minIo);

        output.WriteLine("  --- warm-up round (not gated: absorbs one-time thread-pool/conhost/JIT growth) ---");
        var warmup = RunOneStorm(output, tabs, gated: false);

        // Force the thread pool to reach its full width BEFORE the baseline is taken. Measured: without
        // this the handle count grew ~0.4 per tab across the measured round and looked exactly like a
        // per-session leak, but the growth was thread-pool worker injection (each worker costs a couple of
        // handles) triggered by the storm itself - a sequential 40-session run of pty-session-smoke-test
        // leaks 0 handles, which is what ruled PtySession out. Pre-warming moves that cost to the left of
        // the baseline so the gated number is about sessions only.
        PrewarmThreadPool((tabs * 2) + 8);

        var (handlesBefore, cmdBefore, threadsBefore) = StableCounts();
        output.WriteLine($"  before measured round: handles={handlesBefore} cmd.exe processes={cmdBefore} threads={threadsBefore}");

        output.WriteLine("  --- measured round ---");
        var measured = RunOneStorm(output, tabs, gated: true);

        var (handlesAfter, cmdAfter, threadsAfter) = StableCounts();
        var handleDelta = handlesAfter - handlesBefore;
        var cmdDelta = cmdAfter - cmdBefore;
        output.WriteLine($"  after measured round: handles={handlesAfter} (delta={handleDelta}) cmd.exe processes={cmdAfter} (delta={cmdDelta}) threads={threadsAfter} (delta={threadsAfter - threadsBefore}, informational: thread-pool injection moves this for reasons unrelated to sessions)");
        output.WriteLine($"  (the warm-up round's own handle delta was {warmup.HandleDelta} - one-time growth, shown for contrast)");

        // Scaled like pty-session-smoke-test's own threshold: a per-session leak is proportional to `tabs`
        // (a pty session plus an observer handle is several handles each), which this catches, while
        // tolerating unrelated runtime churn.
        var handleThreshold = Math.Max(tabs / 4, 8);
        var noHandleLeak = handleDelta < handleThreshold;
        output.WriteLine($"  [{(noHandleLeak ? "PASS" : "FAIL")}] no handle trend across {tabs} more full register+close cycles (delta={handleDelta}, threshold<{handleThreshold})");

        // The load-bearing one: not a single cmd.exe may survive. Other cmd.exe processes on the machine
        // make the absolute count noisy, so the delta is what is gated, and it must be <= 0.
        var noProcessLeak = cmdDelta <= 0;
        output.WriteLine($"  [{(noProcessLeak ? "PASS" : "FAIL")}] no leaked cmd.exe process (delta={cmdDelta}, must be <= 0)");

        return measured.Ok && noHandleLeak && noProcessLeak;
    }

    private readonly record struct StormResult(bool Ok, int HandleDelta);

    private static StormResult RunOneStorm(TextWriter output, int tabs, bool gated)
    {
        var (handlesAtStart, _, _) = StableCounts();

        var registry = new PtyRegistry();
        var ended = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var endedArgs = new ConcurrentBag<PtySessionEndedEventArgs>();
        registry.SessionEnded += (_, e) =>
        {
            ended.AddOrUpdate(e.TabId, 1, (_, count) => count + 1);
            endedArgs.Add(e);
        };

        // A third of the tabs get a child that exits by itself at (roughly) the same moment as the close -
        // race (a) from PtyRegistry's class remarks. The rest are idle children that only ever die on
        // teardown, which is the shape that leaks if teardown is wrong.
        var tabIds = new string[tabs];
        var selfExiting = new bool[tabs];
        for (var i = 0; i < tabs; i++)
        {
            tabIds[i] = Guid.NewGuid().ToString("N");
            selfExiting[i] = i % 3 == 0;
            registry.Register(tabIds[i], selfExiting[i] ? StartSelfExitingChild(9) : StartIdleChild());
        }

        output.WriteLine($"  registered {tabs} sessions ({selfExiting.Count(x => x)} of them `cmd /c exit 9` self-exit racers); live Count now {registry.Count} - the racers that already died removed themselves");

        // Every close starts at the same instant, from its own task, and a third of the tabs are closed
        // twice concurrently - race (b). Enumeration of the whole registry runs concurrently too - race (c).
        var barrier = new ManualResetEventSlim(false);
        var escaped = new ConcurrentBag<Exception>();
        var results = new ConcurrentBag<(string TabId, PtyCloseResult Result)>();
        var enumerations = 0;
        var stopEnumerating = false;

        var closers = new List<Task>();
        for (var i = 0; i < tabs; i++)
        {
            var tabId = tabIds[i];
            var closerCount = i % 3 == 1 ? 2 : 1;
            for (var c = 0; c < closerCount; c++)
            {
                closers.Add(Task.Run(async () =>
                {
                    barrier.Wait();
                    try
                    {
                        var result = await registry.CloseAsync(tabId).ConfigureAwait(false);
                        results.Add((tabId, result));
                    }
                    catch (Exception ex)
                    {
                        escaped.Add(ex);
                    }
                }));
            }
        }

        // Dedicated threads, not pool work items: the pool is saturated by the closers above, and an
        // enumerator that only got scheduled after the storm finished would prove nothing about enumerating
        // *during* teardown.
        var enumerators = new List<Thread>();
        for (var e = 0; e < 4; e++)
        {
            var thread = new Thread(() =>
            {
                barrier.Wait();
                try
                {
                    while (!Volatile.Read(ref stopEnumerating))
                    {
                        foreach (var registration in registry.Snapshot())
                        {
                            _ = registration.Session.ProcessId;
                        }

                        Interlocked.Increment(ref enumerations);
                    }
                }
                catch (Exception ex)
                {
                    escaped.Add(ex);
                }
            })
            {
                IsBackground = true,
                Name = $"registry-stress-enumerator-{e}",
            };
            thread.Start();
            enumerators.Add(thread);
        }

        var stopwatch = Stopwatch.StartNew();
        barrier.Set();
        var finished = Task.WhenAll(closers).Wait(TimeSpan.FromMinutes(2));
        stopwatch.Stop();
        Volatile.Write(ref stopEnumerating, true);
        foreach (var thread in enumerators)
        {
            thread.Join(TimeSpan.FromSeconds(5));
        }

        var ok = finished;
        output.WriteLine($"  all closers finished={finished} in {stopwatch.ElapsedMilliseconds} ms; concurrent enumeration passes during teardown={Volatile.Read(ref enumerations)}");

        output.WriteLine($"  [{(escaped.IsEmpty ? "PASS" : "FAIL")}] no exception escaped any CloseAsync/Snapshot ({escaped.Count} escaped)");
        foreach (var ex in escaped.Take(3))
        {
            output.WriteLine($"    escaped: {ex}");
        }

        ok &= escaped.IsEmpty;
        ok &= registry.Count == 0;
        output.WriteLine($"  [{(registry.Count == 0 ? "PASS" : "FAIL")}] the registry is empty afterwards (Count={registry.Count})");

        // One teardown per tab: duplicate closers must have joined the same close, which is observable as
        // the identical result instance (or a NotFound if they arrived after it had finished).
        var duplicateJoins = 0;
        var duplicateDivergences = 0;
        foreach (var group in results.GroupBy(r => r.TabId, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            var distinct = group.Select(g => g.Result).Distinct().ToList();
            var notFounds = distinct.Count(r => r.Outcome == PtyCloseOutcome.NotFound);
            if (distinct.Count == 1 || (distinct.Count == 2 && notFounds == 1))
            {
                duplicateJoins++;
            }
            else
            {
                duplicateDivergences++;
                output.WriteLine($"    tab {group.Key}: {distinct.Count} distinct results -> {string.Join(", ", distinct.Select(d => d.Outcome))}");
            }
        }

        output.WriteLine($"  [{(duplicateDivergences == 0 ? "PASS" : "FAIL")}] every double-closed tab produced ONE teardown ({duplicateJoins} tabs where the second closer joined the first's result or got NotFound, {duplicateDivergences} divergent)");
        ok &= duplicateDivergences == 0;

        var endedOnce = ended.Count == tabs && ended.Values.All(count => count == 1);
        output.WriteLine($"  [{(endedOnce ? "PASS" : "FAIL")}] SessionEnded fired exactly once per tab (tabs with an event={ended.Count}/{tabs}, max fires for one tab={(ended.IsEmpty ? 0 : ended.Values.Max())})");
        ok &= endedOnce;

        var childExited = endedArgs.Count(a => a.Reason == PtySessionExitReason.ChildExited);
        var tornDown = endedArgs.Count(a => a.Reason == PtySessionExitReason.TornDown);
        var outcomes = string.Join(", ", endedArgs.GroupBy(a => a.Outcome).Select(g => $"{g.Key}={g.Count()}"));
        output.WriteLine($"  exit classification: ChildExited={childExited} (self-exit racers registered={selfExiting.Count(x => x)}) TornDown={tornDown}; outcomes: {outcomes}");

        var childrenGone = results.Where(r => r.Result.Outcome != PtyCloseOutcome.NotFound).All(r => r.Result.ChildIsGone);
        output.WriteLine($"  [{(childrenGone ? "PASS" : "FAIL")}] every non-NotFound close reported the child as gone (Closed or ForceKilled)");
        ok &= childrenGone;

        registry.Dispose();

        var (handlesAtEnd, _, _) = StableCounts();
        var delta = handlesAtEnd - handlesAtStart;
        if (!gated)
        {
            output.WriteLine($"  warm-up round handle delta={delta} (not gated)");
        }

        return new StormResult(ok, delta);
    }

    private static bool RunForceKillCheck(TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("== check 4/5: force-kill escalation against real children (CloseTimeout=0) ==");

        // CloseTimeout=0 means the "is it gone yet" wait cannot succeed, so every close escalates to
        // Process.Kill(entireProcessTree: true). This exercises the real kill path end to end - the fake
        // observer in the unit suite proves the decision logic, this proves the syscall works and leaves
        // nothing behind.
        using var registry = new PtyRegistry(new PtyRegistryOptions
        {
            CloseTimeout = TimeSpan.Zero,
            ForceKillGrace = TimeSpan.FromSeconds(5),
        });

        const int count = 4;
        var tabIds = new string[count];
        var pids = new int[count];
        for (var i = 0; i < count; i++)
        {
            // A *busy* child, not an idle prompt: `ping -n 60` keeps cmd.exe in a child process of its own
            // for a minute, which makes it far likelier to still be alive when the (zero) wait expires, so
            // the force-kill branch is actually reached rather than the child having already died inside
            // Dispose. It also gives entireProcessTree something to do - the ping is a grandchild.
            var session = StartBusyChild();
            tabIds[i] = Guid.NewGuid().ToString("N");
            pids[i] = session.ProcessId;
            registry.Register(tabIds[i], session);
        }

        var results = Task.WhenAll(tabIds.Select(t => registry.CloseAsync(t))).GetAwaiter().GetResult();
        foreach (var result in results)
        {
            output.WriteLine($"  tab={result.TabId[..8]} pid={result.ProcessId} outcome={result.Outcome} exitCode={result.ExitCode?.ToString() ?? "null"} reason={result.Reason} elapsed={result.Elapsed.TotalMilliseconds:0} ms");
        }

        var allGone = results.All(r => r.ChildIsGone);
        var forceKilled = results.Count(r => r.Outcome == PtyCloseOutcome.ForceKilled);
        output.WriteLine($"  [{(allGone ? "PASS" : "FAIL")}] every child is gone ({forceKilled}/{count} of them via the force-kill path; the rest raced to a normal exit first, which is also correct)");

        var stillAlive = pids.Count(IsAlive);
        output.WriteLine($"  [{(stillAlive == 0 ? "PASS" : "FAIL")}] none of the {count} PIDs is still alive (alive={stillAlive})");

        return allGone && stillAlive == 0;
    }

    private static bool RunCloseAllConcurrencyCheck(TextWriter output, int tabs)
    {
        output.WriteLine();
        output.WriteLine($"== check 5a/5: CloseAllAsync closes {tabs} tabs concurrently, not serially ==");

        using var registry = new PtyRegistry();
        for (var i = 0; i < tabs; i++)
        {
            registry.Register(Guid.NewGuid().ToString("N"), StartIdleChild());
        }

        var stopwatch = Stopwatch.StartNew();
        var results = registry.CloseAllAsync().GetAwaiter().GetResult();
        stopwatch.Stop();

        var serialBudget = TimeSpan.FromMilliseconds(500 * tabs);
        var fastEnough = stopwatch.Elapsed < serialBudget;
        output.WriteLine($"  closed {results.Count} tabs in {stopwatch.ElapsedMilliseconds} ms (a serial teardown of {tabs} children would be well over {serialBudget.TotalMilliseconds:0} ms)");
        output.WriteLine($"  [{(fastEnough ? "PASS" : "FAIL")}] CloseAllAsync ran the closes concurrently");
        output.WriteLine($"  [{(registry.Count == 0 ? "PASS" : "FAIL")}] registry empty afterwards (Count={registry.Count})");
        var allGone = results.All(r => r.ChildIsGone || r.Outcome == PtyCloseOutcome.NotFound);
        output.WriteLine($"  [{(allGone ? "PASS" : "FAIL")}] every result reports the child gone (or NotFound): {string.Join(", ", results.GroupBy(r => r.Outcome).Select(g => $"{g.Key}={g.Count()}"))}");

        return fastEnough && registry.Count == 0 && allGone;
    }

    private static bool RunDisposeCheck(TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("== check 5b/5: Dispose tears everything down synchronously and then refuses registrations ==");

        var registry = new PtyRegistry();
        var pids = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var session = StartIdleChild();
            pids.Add(session.ProcessId);
            registry.Register(Guid.NewGuid().ToString("N"), session);
        }

        var stopwatch = Stopwatch.StartNew();
        registry.Dispose();
        registry.Dispose(); // idempotency, on purpose
        stopwatch.Stop();

        var alive = pids.Count(IsAlive);
        output.WriteLine($"  [{(alive == 0 ? "PASS" : "FAIL")}] Dispose() (called twice) returned in {stopwatch.ElapsedMilliseconds} ms with all {pids.Count} children gone (alive={alive})");

        var refused = false;
        PtySession? extra = null;
        try
        {
            extra = StartIdleChild();
            registry.Register(Guid.NewGuid().ToString("N"), extra);
        }
        catch (ObjectDisposedException)
        {
            refused = true;
        }
        finally
        {
            extra?.Dispose();
        }

        output.WriteLine($"  [{(refused ? "PASS" : "FAIL")}] Register after Dispose throws ObjectDisposedException (so a late tab cannot be silently orphaned in a dead registry)");
        return alive == 0 && refused;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Occupies <paramref name="workers"/> thread-pool workers simultaneously so the pool has actually
    /// created them, then releases them. Purely a measurement-hygiene step - see the call site.
    /// </summary>
    private static void PrewarmThreadPool(int workers)
    {
        using var release = new ManualResetEventSlim(false);
        using var allBusy = new CountdownEvent(workers);
        var tasks = new Task[workers];
        for (var i = 0; i < workers; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                allBusy.Signal();
                release.Wait();
            });
        }

        allBusy.Wait(TimeSpan.FromSeconds(30));
        release.Set();
        Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(30));
    }

    private static (int Handles, int CmdProcesses, int Threads) StableCounts()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Give the OS a moment to actually reap children whose kill/exit was just observed - "the handle is
        // closed" and "the process object is gone from the process table" are not the same instant.
        Thread.Sleep(500);

        using var self = Process.GetCurrentProcess();
        self.Refresh();
        var cmdProcesses = Process.GetProcessesByName("cmd");
        try
        {
            return (self.HandleCount, cmdProcesses.Length, self.Threads.Count);
        }
        finally
        {
            foreach (var process in cmdProcesses)
            {
                process.Dispose();
            }
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

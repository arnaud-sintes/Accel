namespace Glaude.Orchestration;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Hidden dev-only diagnostic for P3-T4, reachable via the undocumented <c>pty-shutdown-orphan-test</c> verb -
/// same pattern, placement rules and rationale as <see cref="PtyRegistryStressTest"/>: what is at stake is real
/// OS process lifecycle across a real process boundary (does app exit actually reach
/// <see cref="PtyRegistry.CloseAllAsync"/> down each of the three exit paths, and does startup reconciliation
/// classify real live/dead PIDs correctly), and no unit test with fakes can establish any of it. Launches
/// <c>cmd.exe</c>, never <c>claude.exe</c>, and writes only to a temp-path <c>glaude-sessions.json</c> - never the
/// user's real profile.
///
/// <para><b>Integration scope decision (Half A).</b> This verb, not <c>RunCombinedAsync</c>, is where the shutdown
/// pattern is wired and proven, and that is deliberate. The real combined-start path runs the WinForms
/// <c>MonitorForm</c> and has no <see cref="PtyRegistry"/>, no <see cref="PtySession"/> and no tab concept at all -
/// the session/registry composition currently exists only in the <c>ui-preview</c>/<c>tabs-e2e-smoke-test</c> dev
/// verbs. Installing <see cref="PtyShutdownCoordinator"/> into <c>RunCombinedAsync</c> today would therefore
/// register process-wide console-control and <c>ProcessExit</c> handlers against a registry that is guaranteed to
/// be empty for the whole run: no benefit, and a real (if small) cost - it would put a new handler into the same
/// console-control chain as that path's existing Ctrl+C→close-the-window handler, which is a live user-facing
/// behaviour with its own tests. Swapping <c>MonitorForm</c> for the WPF <c>MainWindow</c> is explicitly a later,
/// unscoped task, and it is the natural place for the one-line install (see
/// <see cref="PtyShutdownCoordinator"/>'s remarks). So P3-T4 delivers: the shutdown mechanism, all three exit
/// paths proven against real children here, and unit coverage of the timeout/try-finally behaviour.</para>
///
/// <para>Scenarios, in order (the first two run as one check, so the printed labels are 1/5 .. 5/5):
/// <list type="number">
/// <item><b>crash-simulated orphans are adoptable, not stale</b> - register real sessions, persist them to a temp
/// <see cref="PtyPidRegistry"/>, then simulate an ungraceful exit by simply <i>not</i> tearing the registry down,
/// and reconcile the file as a fresh startup would. All still-alive children must classify as
/// <see cref="PtyOrphanKind.Adoptable"/>, and no entry may be pruned;</item>
/// <item><b>a child that died while Glaude was down is stale</b> - kill one of those children directly, bypassing
/// the registry, and reconcile again: that one must flip to <see cref="PtyOrphanKind.Stale"/> and be deleted from
/// the file, while the survivors stay adoptable;</item>
/// <item><b>the PID-reuse guard on a real live PID</b> (risk register item 5) - an entry whose PID is genuinely
/// alive but whose recorded start time is wrong must be stale, and <see cref="PtyOrphanReconciler.KillOrphan"/>
/// must <i>refuse</i> to kill it;</item>
/// <item><b>exit path 1/3 - explicit</b>: <see cref="PtyShutdownCoordinator.Shutdown"/> from the normal
/// <c>finally</c>/<c>Dispose</c> position closes every real child, bounded;</item>
/// <item><b>exit path 2/3 - console control</b>: the real <c>SetConsoleCtrlHandler</c> callback body
/// (<see cref="PtyShutdownCoordinator.HandleConsoleCtrlEvent"/>, invoked with <c>CTRL_C_EVENT</c> and with
/// <c>CTRL_CLOSE_EVENT</c>) closes every real child, bounded, and does not swallow the event;</item>
/// <item><b>exit path 3/3 - <c>AppDomain.ProcessExit</c></b>: a real child Glaude process
/// (<c>pty-shutdown-processexit-child</c>) starts real sessions, installs the coordinator and calls
/// <c>Environment.Exit</c>. The verdict is written from a <i>second</i> <c>ProcessExit</c> handler registered after
/// the coordinator's, so it observes the world after the coordinator ran but while the process is still alive -
/// which is what makes the result attributable to the handler rather than to
/// <see cref="GlaudeJobObject"/>'s kill-on-close, which only fires later when the process's handles close. A
/// control <c>cmd.exe</c> started outside the registry (and outside the job) must still be alive at that point,
/// proving the observation window is real.</item>
/// </list></para>
/// </summary>
public static class PtyShutdownReconcileSmokeTest
{
    /// <summary>The argv[0] of the re-executed child used by the <c>AppDomain.ProcessExit</c> check (5/5).</summary>
    public const string ProcessExitChildVerb = "pty-shutdown-processexit-child";

    /// <summary>Runs every check. Returns 0 if all passed, 1 otherwise.</summary>
    public static int Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var failures = 0;
        var workDir = Path.Combine(Path.GetTempPath(), $"glaude-p3t4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            failures += RunOrphanClassificationCheck(output, workDir) ? 0 : 1;
            failures += RunPidReuseGuardCheck(output, workDir) ? 0 : 1;
            failures += RunExplicitShutdownCheck(output) ? 0 : 1;
            failures += RunConsoleCtrlShutdownCheck(output) ? 0 : 1;
            failures += RunProcessExitShutdownCheck(output, workDir) ? 0 : 1;
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }

        output.WriteLine();
        output.WriteLine(failures == 0
            ? "pty-shutdown-orphan-test: ALL CHECKS PASSED"
            : $"pty-shutdown-orphan-test: {failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Half B: startup orphan reconciliation
    // ---------------------------------------------------------------------------------------------------------

    private static bool RunOrphanClassificationCheck(TextWriter output, string workDir)
    {
        output.WriteLine("== check 1/5: crash-simulated orphans classify as adoptable; a child that died while Glaude was down classifies as stale ==");

        var pidRegistryPath = Path.Combine(workDir, "glaude-sessions.json");
        var pidRegistry = new PtyPidRegistry(pidRegistryPath);

        // NOTE: deliberately not `using`. Simulating an ungraceful exit means the registry is never disposed and
        // its sessions are never closed - the children stay alive, orphaned from Glaude's bookkeeping. It is
        // disposed at the very end of this check purely as cleanup, after every assertion.
        var registry = new PtyRegistry();
        var ok = true;

        try
        {
            var pids = new List<int>();
            var sessionIds = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var session = StartIdleChild();
                var tabId = Guid.NewGuid().ToString("N");
                registry.Register(tabId, session);

                // What a real launch would persist (P2-T7's write-on-spawn): the PID paired with the start time
                // captured while the child was still suspended - the PID-reuse guard's other half.
                if (session.ProcessStartTimeUtc is not { } startTime)
                {
                    output.WriteLine($"  [FAIL] session {tabId[..8]} has no ProcessStartTimeUtc; the whole PID-reuse guard depends on it");
                    ok = false;
                    continue;
                }

                pidRegistry.Add(new PtyPidEntry(
                    SessionId: tabId,
                    Pid: session.ProcessId,
                    ProcessStartTimeUtc: startTime,
                    Cwd: Path.GetTempPath(),
                    LaunchedAtUtc: DateTime.UtcNow));

                pids.Add(session.ProcessId);
                sessionIds.Add(tabId);
            }

            output.WriteLine($"  registered + persisted {sessionIds.Count} real sessions (pids {string.Join(", ", pids)}) to {pidRegistryPath}");
            output.WriteLine("  simulating an ungraceful Glaude exit: NOT calling PtyRegistry.Dispose() - the children stay alive and orphaned");

            // --- first reconciliation: everything is alive, so everything is adoptable -------------------------
            var report = PtyOrphanReconciler.ReconcileAtStartup(pidRegistry);
            output.WriteLine($"  reconcile #1: {report.Summarize()}");
            foreach (var classification in report.Classifications)
            {
                output.WriteLine($"    pid={classification.Entry.Pid} -> {classification.Kind}: {classification.Reason}");
            }

            var allAdoptable = report.Classifications.Count == pids.Count
                && report.Classifications.All(c => c.Kind == PtyOrphanKind.Adoptable);
            output.WriteLine($"  [{(allAdoptable ? "PASS" : "FAIL")}] all {pids.Count} live orphans classified Adoptable (adoptable={report.Adoptable.Count}, stale={report.Stale.Count})");
            ok &= allAdoptable;

            var nothingPruned = pidRegistry.LoadAll().Count == pids.Count;
            output.WriteLine($"  [{(nothingPruned ? "PASS" : "FAIL")}] default policy left every live orphan in the registry file and running (entries on disk={pidRegistry.LoadAll().Count}, alive={pids.Count(PtyOrphanReconciler.IsProcessAlive)})");
            ok &= nothingPruned;

            // --- kill one child behind the registry's back, then reconcile again -------------------------------
            if (pids.Count == 0)
            {
                return false;
            }

            var doomedPid = pids[0];
            var doomedSessionId = sessionIds[0];
            KillDirectly(doomedPid);
            var reallyDead = WaitFor(() => !PtyOrphanReconciler.IsProcessAlive(doomedPid), TimeSpan.FromSeconds(10));
            output.WriteLine($"  killed pid {doomedPid} directly (bypassing the registry: 'the child died while Glaude was down'); alive now={!reallyDead}");

            var report2 = PtyOrphanReconciler.ReconcileAtStartup(pidRegistry);
            output.WriteLine($"  reconcile #2: {report2.Summarize()}");
            foreach (var classification in report2.Classifications)
            {
                output.WriteLine($"    pid={classification.Entry.Pid} -> {classification.Kind}: {classification.Reason}");
            }

            var deadIsStale = report2.Stale.Count == 1 && report2.Stale[0].Pid == doomedPid;
            output.WriteLine($"  [{(deadIsStale ? "PASS" : "FAIL")}] the dead child's entry is the only stale one (stale pids={string.Join(",", report2.Stale.Select(e => e.Pid))}, expected {doomedPid})");
            ok &= deadIsStale;

            var survivorsAdoptable = report2.Adoptable.Count == pids.Count - 1
                && report2.Adoptable.All(e => e.Pid != doomedPid);
            output.WriteLine($"  [{(survivorsAdoptable ? "PASS" : "FAIL")}] the {pids.Count - 1} still-alive children are still Adoptable (adoptable pids={string.Join(",", report2.Adoptable.Select(e => e.Pid))})");
            ok &= survivorsAdoptable;

            var onDisk = pidRegistry.LoadAll();
            var stalePruned = onDisk.Count == pids.Count - 1
                && onDisk.All(e => !string.Equals(e.SessionId, doomedSessionId, StringComparison.Ordinal));
            output.WriteLine($"  [{(stalePruned ? "PASS" : "FAIL")}] the stale entry was deleted from the registry file and the live ones kept (entries on disk={onDisk.Count}, expected {pids.Count - 1})");
            ok &= stalePruned;

            // --- the decision-point primitives, on a real orphan -----------------------------------------------
            var adopted = PtyOrphanReconciler.AdoptAsDetached(onDisk[0], pidRegistry);
            var stillAlive = PtyOrphanReconciler.IsProcessAlive(onDisk[0].Pid);
            var adoptOk = adopted.Outcome == PtyOrphanActionOutcome.Detached
                && stillAlive
                && pidRegistry.LoadAll().All(e => !string.Equals(e.SessionId, onDisk[0].SessionId, StringComparison.Ordinal));
            output.WriteLine($"  [{(adoptOk ? "PASS" : "FAIL")}] 'adopt as detached' on pid {onDisk[0].Pid}: left running (alive={stillAlive}) and dropped from the registry ({adopted.Outcome})");
            ok &= adoptOk;

            var last = pidRegistry.LoadAll().Single();
            var killed = PtyOrphanReconciler.KillOrphan(last, pidRegistry);
            var killGone = WaitFor(() => !PtyOrphanReconciler.IsProcessAlive(last.Pid), TimeSpan.FromSeconds(10));
            var killOk = killed.Outcome == PtyOrphanActionOutcome.Killed && killGone && pidRegistry.LoadAll().Count == 0;
            output.WriteLine($"  [{(killOk ? "PASS" : "FAIL")}] 'kill orphan' on pid {last.Pid}: {killed.Outcome} ({killed.Detail}); gone={killGone}, registry now empty={pidRegistry.LoadAll().Count == 0}");
            ok &= killOk;

            return ok;
        }
        finally
        {
            // Cleanup only - after every assertion. The one session still standing (the "adopted" one) is closed
            // here so this diagnostic does not leak a cmd.exe of its own.
            registry.Dispose();
        }
    }

    private static bool RunPidReuseGuardCheck(TextWriter output, string workDir)
    {
        output.WriteLine();
        output.WriteLine("== check 2/5: PID-reuse guard against a genuinely live PID (risk register item 5) ==");

        var pidRegistry = new PtyPidRegistry(Path.Combine(workDir, "glaude-sessions-reuse.json"));

        // A real, live, definitely-not-ours PID: this very process. Recorded with a start time that is wrong by an
        // hour, which is exactly the shape of a recycled PID - alive, matching number, different process.
        using var self = Process.GetCurrentProcess();
        var entry = new PtyPidEntry(
            SessionId: "recycled-pid",
            Pid: self.Id,
            ProcessStartTimeUtc: self.StartTime.ToUniversalTime().AddHours(-1),
            Cwd: Path.GetTempPath(),
            LaunchedAtUtc: DateTime.UtcNow.AddHours(-1));
        pidRegistry.Add(entry);

        var report = PtyOrphanReconciler.ReconcileAtStartup(pidRegistry);
        var classification = report.Classifications.SingleOrDefault();
        var isStale = classification is { Kind: PtyOrphanKind.Stale };
        output.WriteLine($"  entry pid={entry.Pid} (this process, alive) recorded with a start time 1h off -> {classification?.Kind}: {classification?.Reason}");
        output.WriteLine($"  [{(isStale ? "PASS" : "FAIL")}] a live-but-recycled PID is Stale, never Adoptable, so it is never offered up for killing");
        output.WriteLine($"  [{(pidRegistry.LoadAll().Count == 0 ? "PASS" : "FAIL")}] and its junk entry was pruned from the file");

        // The action-time re-check: even if a UI acted on a stale-by-now entry, the kill must be refused. Killing
        // this process's own PID is exactly what must NOT happen - a real proof, since a bug here kills the test.
        var killAttempt = PtyOrphanReconciler.KillOrphan(entry, registry: null);
        var refused = killAttempt.Outcome == PtyOrphanActionOutcome.RefusedIdentityMismatch;
        output.WriteLine($"  [{(refused ? "PASS" : "FAIL")}] KillOrphan on it refused rather than killing an unrelated live process ({killAttempt.Outcome}: {killAttempt.Detail})");

        return isStale && pidRegistry.LoadAll().Count == 0 && refused;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Half A: the three exit paths
    // ---------------------------------------------------------------------------------------------------------

    private static bool RunExplicitShutdownCheck(TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("== check 3/5: exit path 1/3 - explicit Shutdown() (the try/finally + Dispose position) ==");
        return RunShutdownPathCheck(
            output,
            "explicit",
            (coordinator, _) => coordinator.Shutdown(),
            PtyShutdownTrigger.Explicit);
    }

    private static bool RunConsoleCtrlShutdownCheck(TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("== check 4/5: exit path 2/3 - the real SetConsoleCtrlHandler callback body (Ctrl+C, then console-close) ==");

        // CTRL_C_EVENT = 0. Invoking the callback body directly is the same code the OS calls; sending a genuine
        // Ctrl+C to our own process group would also hit whatever ran this verb.
        var ctrlC = RunShutdownPathCheck(
            output,
            "CTRL_C_EVENT",
            (coordinator, results) =>
            {
                var handled = coordinator.HandleConsoleCtrlEvent(0);
                results["swallowed"] = handled;
                return coordinator.LastResult!;
            },
            PtyShutdownTrigger.ConsoleCtrl,
            extraAssert: (output2, results) =>
            {
                var swallowed = results.TryGetValue("swallowed", out var value) && value is true;
                output2.WriteLine($"  [{(!swallowed ? "PASS" : "FAIL")}] the handler returned false (did not swallow the event), so an existing Ctrl+C handler still runs");
                return !swallowed;
            });

        // CTRL_CLOSE_EVENT = 2: the case Console.CancelKeyPress never sees at all, i.e. the reason the native
        // handler exists rather than just the managed event.
        var ctrlClose = RunShutdownPathCheck(
            output,
            "CTRL_CLOSE_EVENT",
            (coordinator, _) =>
            {
                coordinator.HandleConsoleCtrlEvent(2);
                return coordinator.LastResult!;
            },
            PtyShutdownTrigger.ConsoleCtrl);

        return ctrlC && ctrlClose;
    }

    private static bool RunShutdownPathCheck(
        TextWriter output,
        string label,
        Func<PtyShutdownCoordinator, Dictionary<string, object>, PtyShutdownResult> trigger,
        PtyShutdownTrigger expectedTrigger,
        Func<TextWriter, Dictionary<string, object>, bool>? extraAssert = null)
    {
        var registry = new PtyRegistry();
        var pids = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var session = StartIdleChild();
            pids.Add(session.ProcessId);
            registry.Register(Guid.NewGuid().ToString("N"), session);
        }

        // Handlers are NOT installed here: this check drives the handler bodies directly, and a diagnostic verb
        // must not leave process-wide console handlers behind for whatever runs after it. Installation itself is
        // covered by the unit tests and, end to end, by check 5/5's real child process.
        using var coordinator = new PtyShutdownCoordinator(
            new PtyRegistryShutdownTarget(registry),
            new PtyShutdownOptions { InstallConsoleCtrlHandler = false, InstallProcessExit = false, InstallCancelKeyPress = false });

        var extras = new Dictionary<string, object>(StringComparer.Ordinal);
        var stopwatch = Stopwatch.StartNew();
        var result = trigger(coordinator, extras);
        stopwatch.Stop();

        output.WriteLine($"  {label}: {result} (wall clock {stopwatch.ElapsedMilliseconds} ms for {pids.Count} children: pids {string.Join(", ", pids)})");

        var completed = result.Outcome == PtyShutdownOutcome.Completed && result.Trigger == expectedTrigger;
        output.WriteLine($"  [{(completed ? "PASS" : "FAIL")}] CloseAllAsync actually ran to completion via this path (outcome={result.Outcome}, trigger={result.Trigger} expected {expectedTrigger}, sessions closed={result.SessionsClosed})");

        var gone = WaitFor(() => pids.All(p => !PtyOrphanReconciler.IsProcessAlive(p)), TimeSpan.FromSeconds(10));
        var aliveCount = pids.Count(PtyOrphanReconciler.IsProcessAlive);
        output.WriteLine($"  [{(gone ? "PASS" : "FAIL")}] every child process is gone (still alive={aliveCount})");

        var bounded = stopwatch.Elapsed < TimeSpan.FromSeconds(30);
        output.WriteLine($"  [{(bounded ? "PASS" : "FAIL")}] the teardown was bounded, not hanging ({stopwatch.ElapsedMilliseconds} ms)");

        var emptied = registry.Count == 0;
        output.WriteLine($"  [{(emptied ? "PASS" : "FAIL")}] the registry is empty afterwards (Count={registry.Count})");

        // Second trigger of the same path: the three exit paths deliberately overlap, so a repeat must be a no-op,
        // not a second teardown. Asserted as "LastResult is still the very same instance" rather than on the
        // returned value, because the handler-body paths (HandleConsoleCtrlEvent/OnProcessExit) return void/bool -
        // and result identity is the stronger claim anyway: the one teardown never ran twice.
        var resultBefore = coordinator.LastResult;
        trigger(coordinator, new Dictionary<string, object>(StringComparer.Ordinal));
        var repeatIsNoop = ReferenceEquals(resultBefore, coordinator.LastResult);
        output.WriteLine($"  [{(repeatIsNoop ? "PASS" : "FAIL")}] triggering the same path again is a no-op - the single teardown's result is unchanged (still {coordinator.LastResult?.Outcome} from {coordinator.LastResult?.Trigger})");

        var extraOk = extraAssert?.Invoke(output, extras) ?? true;

        registry.Dispose();
        return completed && gone && bounded && emptied && repeatIsNoop && extraOk;
    }

    private static bool RunProcessExitShutdownCheck(TextWriter output, string workDir)
    {
        output.WriteLine();
        output.WriteLine("== check 5/5: exit path 3/3 - a real AppDomain.ProcessExit in a real child Glaude process ==");

        var verdictPath = Path.Combine(workDir, "processexit-verdict.txt");
        if (!TryBuildSelfInvocation(verdictPath, out var fileName, out var arguments))
        {
            output.WriteLine("  [FAIL] could not work out how to re-invoke this executable");
            return false;
        }

        output.WriteLine($"  launching: {fileName} {arguments}");
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDir,
            },
        };

        child.Start();
        var childOut = child.StandardOutput.ReadToEnd();
        var childErr = child.StandardError.ReadToEnd();
        var exited = child.WaitForExit(60_000);
        foreach (var line in childOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            output.WriteLine($"    child> {line.TrimEnd()}");
        }

        if (!string.IsNullOrWhiteSpace(childErr))
        {
            output.WriteLine($"    child stderr> {childErr.Trim()}");
        }

        output.WriteLine($"  child exited={exited} code={(exited ? child.ExitCode.ToString(CultureInfo.InvariantCulture) : "n/a")}");

        if (!File.Exists(verdictPath))
        {
            output.WriteLine("  [FAIL] the child's post-coordinator ProcessExit handler never wrote its verdict");
            return false;
        }

        var verdict = ParseVerdict(File.ReadAllLines(verdictPath));
        foreach (var pair in verdict)
        {
            output.WriteLine($"    verdict: {pair.Key}={pair.Value}");
        }

        var sessionsGone = Get(verdict, "sessionsAliveAfterCoordinator") == "0";
        output.WriteLine($"  [{(sessionsGone ? "PASS" : "FAIL")}] inside the child's ProcessExit, AFTER the coordinator's handler ran, every registry session was already gone (alive={Get(verdict, "sessionsAliveAfterCoordinator")} of {Get(verdict, "sessionCount")})");

        // The control process proves the observation window is real: it was started by the child but never
        // registered and never assigned to the job, so if it is still alive at this point, the sessions being gone
        // is attributable to the coordinator - not to process teardown or the job object's kill-on-close, neither
        // of which had happened yet.
        var controlAlive = Get(verdict, "controlAlive") == "True";
        output.WriteLine($"  [{(controlAlive ? "PASS" : "FAIL")}] the control cmd.exe (started outside the registry and the job) was still alive at that moment, so the observation window is real, not post-mortem");

        var triggerOk = Get(verdict, "trigger") == nameof(PtyShutdownTrigger.ProcessExit);
        var outcomeOk = Get(verdict, "outcome") == nameof(PtyShutdownOutcome.Completed);
        output.WriteLine($"  [{(triggerOk && outcomeOk ? "PASS" : "FAIL")}] the teardown was attributed to the ProcessExit path and completed (trigger={Get(verdict, "trigger")}, outcome={Get(verdict, "outcome")}, elapsed={Get(verdict, "elapsedMs")} ms)");

        // Belt and braces from out here too: the PIDs the child reported must be gone now that it has exited.
        var reportedPids = Get(verdict, "sessionPids")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(text => int.TryParse(text, out var pid) ? pid : -1)
            .Where(pid => pid > 0)
            .ToArray();
        var goneFromHere = reportedPids.Length > 0 && reportedPids.All(pid => !PtyOrphanReconciler.IsProcessAlive(pid));
        output.WriteLine($"  [{(goneFromHere ? "PASS" : "FAIL")}] and from this process, none of the child's {reportedPids.Length} session pids is alive");

        // Clean up the control process, which by design survived its parent.
        if (int.TryParse(Get(verdict, "controlPid"), out var controlPid))
        {
            KillDirectly(controlPid);
            output.WriteLine($"  cleaned up the control cmd.exe (pid {controlPid})");
        }

        return exited && sessionsGone && controlAlive && triggerOk && outcomeOk && goneFromHere;
    }

    /// <summary>
    /// The <c>pty-shutdown-processexit-child</c> body: starts real sessions, installs a real
    /// <see cref="PtyShutdownCoordinator"/>, and calls <c>Environment.Exit</c> so a genuine
    /// <c>AppDomain.ProcessExit</c> fires. The verdict is written by a second <c>ProcessExit</c> handler,
    /// registered after the coordinator's so it runs after it (multicast handlers run in subscription order) while
    /// the process is still alive - see the class remarks for why that ordering is the whole point.
    /// </summary>
    public static int RunProcessExitChild(TextWriter output, string verdictPath)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrEmpty(verdictPath);

        var registry = new PtyRegistry();
        var pids = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var session = StartIdleChild();
            pids.Add(session.ProcessId);
            registry.Register(Guid.NewGuid().ToString("N"), session);
        }

        // The control: a cmd.exe this process starts directly, so it is neither in the registry nor in
        // GlaudeJobObject. It must outlive us.
        var control = Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            "/c ping -n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;

        output.WriteLine($"session pids: {string.Join(",", pids)}");
        output.WriteLine($"control pid: {control.Id}");

        var coordinator = new PtyShutdownCoordinator(new PtyRegistryShutdownTarget(registry)).Install();
        output.WriteLine($"coordinator installed: consoleCtrl={coordinator.ConsoleCtrlHandlerInstalled} processExit={coordinator.ProcessExitHandlerInstalled} cancelKeyPress={coordinator.CancelKeyPressHandlerInstalled}");

        // Registered AFTER the coordinator's, so it observes the world once the coordinator's handler has returned
        // but before the process is actually gone.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            var lines = new List<string>
            {
                $"sessionCount={pids.Count}",
                $"sessionPids={string.Join(",", pids)}",
                $"sessionsAliveAfterCoordinator={pids.Count(PtyOrphanReconciler.IsProcessAlive)}",
                $"controlPid={control.Id}",
                $"controlAlive={PtyOrphanReconciler.IsProcessAlive(control.Id)}",
                $"trigger={coordinator.LastResult?.Trigger}",
                $"outcome={coordinator.LastResult?.Outcome}",
                $"elapsedMs={coordinator.LastResult?.Elapsed.TotalMilliseconds:0}",
                $"sessionsClosed={coordinator.LastResult?.SessionsClosed}",
                $"registryCount={registry.Count}",
            };

            try
            {
                File.WriteAllLines(verdictPath, lines);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"verdict write failed: {ex}");
            }
        };

        output.WriteLine("calling Environment.Exit(0) - nothing below this line runs, and no finally/Dispose closes the registry");
        output.Flush();
        Environment.Exit(0);
        return 0;
    }

    // ---------------------------------------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>An interactive <c>cmd.exe</c>: it sits at its prompt forever, so it only ever goes away because
    /// something tore it down - the shape that can leak. Same child as <see cref="PtyRegistryStressTest"/> uses,
    /// and never <c>claude.exe</c>.</summary>
    private static PtySession StartIdleChild() => PtySession.Start(
        new PtyLaunchSpec
        {
            ExecutablePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            WorkingDirectory = Path.GetTempPath(),
        },
        new PtySessionOptions { OutputChannelCapacity = 8, ReadBufferSize = 512 });

    /// <summary>Re-invokes this same build: directly for an apphost (<c>Glaude.exe</c>), via the muxer when the
    /// process was launched as <c>dotnet Glaude.dll</c>.</summary>
    private static bool TryBuildSelfInvocation(string verdictPath, out string fileName, out string arguments)
    {
        fileName = string.Empty;
        arguments = string.Empty;

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            return false;
        }

        var quotedVerdict = $"\"{verdictPath}\"";
        var isMuxer = string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);

        if (isMuxer)
        {
            // AppContext.BaseDirectory rather than Assembly.Location: the latter is empty in a single-file
            // publish (IL3000), which is exactly how this app ships (see publish.ps1).
            var assemblyName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
            var assemblyPath = assemblyName is null
                ? null
                : Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
            if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
            {
                return false;
            }

            fileName = processPath;
            arguments = $"\"{assemblyPath}\" {ProcessExitChildVerb} {quotedVerdict}";
            return true;
        }

        fileName = processPath;
        arguments = $"{ProcessExitChildVerb} {quotedVerdict}";
        return true;
    }

    private static Dictionary<string, string> ParseVerdict(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                result[line[..separator]] = line[(separator + 1)..].Trim();
            }
        }

        return result;
    }

    private static string Get(Dictionary<string, string> verdict, string key) =>
        verdict.TryGetValue(key, out var value) ? value : "<missing>";

    private static void KillDirectly(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // Already gone, or not killable - the caller re-probes liveness rather than trusting this.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // Temp litter is not worth failing a diagnostic over.
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

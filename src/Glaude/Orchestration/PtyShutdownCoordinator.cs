namespace Glaude.Orchestration;

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Which exit path reached <see cref="PtyShutdownCoordinator.Shutdown"/>.</summary>
public enum PtyShutdownTrigger
{
    /// <summary>The normal path: a <c>finally</c> around the UI loop, or <see cref="PtyShutdownCoordinator.Dispose"/>.</summary>
    Explicit,

    /// <summary>A console control event (Ctrl+C, Ctrl+Break, console window closed, logoff, shutdown).</summary>
    ConsoleCtrl,

    /// <summary><c>AppDomain.CurrentDomain.ProcessExit</c> - the last-chance path.</summary>
    ProcessExit,
}

/// <summary>How a shutdown attempt ended.</summary>
public enum PtyShutdownOutcome
{
    /// <summary>Every session was closed within the budget.</summary>
    Completed,

    /// <summary>The budget expired first. The remaining children are left to
    /// <see cref="GlaudeJobObject"/>'s kill-on-close, which the OS applies when this process's handles close.</summary>
    TimedOut,

    /// <summary>The teardown threw. Recorded, swallowed, and cleanup still finished.</summary>
    Faulted,

    /// <summary>Another trigger got there first; this call did nothing. Not an error - the three exit paths
    /// deliberately overlap.</summary>
    AlreadyShutDown,
}

/// <summary>The result of one shutdown attempt. Never thrown.</summary>
/// <param name="Trigger">Which exit path ran it.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Elapsed">Wall clock spent in the teardown.</param>
/// <param name="SessionsClosed">How many sessions the target reported closing (0 for a timeout/fault).</param>
/// <param name="Failure">The exception for <see cref="PtyShutdownOutcome.Faulted"/>.</param>
public sealed record PtyShutdownResult(
    PtyShutdownTrigger Trigger,
    PtyShutdownOutcome Outcome,
    TimeSpan Elapsed,
    int SessionsClosed,
    Exception? Failure)
{
    /// <summary>One-line summary for a log/console line.</summary>
    public override string ToString() =>
        $"{Trigger} -> {Outcome} in {Elapsed.TotalMilliseconds:0} ms, {SessionsClosed} session(s) closed" +
        (Failure is null ? string.Empty : $", failure: {Failure.GetType().Name}: {Failure.Message}");
}

/// <summary>
/// What the coordinator tears down. An interface rather than a direct <see cref="PtyRegistry"/> dependency purely
/// so the timeout/try-finally behaviour is unit-testable against a slow or throwing double - a genuinely hung
/// registry is not something a test can conjure. <see cref="PtyRegistryShutdownTarget"/> is the production
/// implementation.
/// </summary>
public interface IPtyShutdownTarget
{
    /// <summary>Closes every live session and returns how many were closed. Should honour
    /// <paramref name="cancellationToken"/> by bringing its force-kill forward, never by abandoning children.</summary>
    Task<int> CloseAllAsync(CancellationToken cancellationToken);
}

/// <summary>Adapts <see cref="PtyRegistry.CloseAllAsync"/> to <see cref="IPtyShutdownTarget"/>. Does not own the
/// registry and never disposes it - the coordinator's contract is "close the sessions", and refusing further
/// registrations is <see cref="PtyRegistry.Dispose"/>'s separate job, still owned by whoever created it.</summary>
public sealed class PtyRegistryShutdownTarget : IPtyShutdownTarget
{
    private readonly PtyRegistry _registry;

    public PtyRegistryShutdownTarget(PtyRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async Task<int> CloseAllAsync(CancellationToken cancellationToken)
    {
        var results = await _registry.CloseAllAsync(cancellationToken).ConfigureAwait(false);
        return results.Count;
    }
}

/// <summary>Tunables for one <see cref="PtyShutdownCoordinator"/>.</summary>
public sealed class PtyShutdownOptions
{
    /// <summary>
    /// Budget for the <see cref="PtyShutdownTrigger.Explicit"/> and <see cref="PtyShutdownTrigger.ProcessExit"/>
    /// paths. Must comfortably exceed <see cref="PtyRegistryOptions.CloseTimeout"/> +
    /// <see cref="PtyRegistryOptions.ForceKillGrace"/> (closes run concurrently, so it does not need to scale with
    /// the tab count) so that the ordinary graceful sequence - stdin EOF, <c>claude</c> flushes its transcript and
    /// fires its session-end signal, <c>ClosePseudoConsole</c> drains conhost, then force-kill only if needed -
    /// gets to run to completion. Cutting this too fine converts normal exits into kills and silently degrades
    /// telemetry, which is the failure this budget is tuned against; it exists only so a wedged child cannot hold
    /// the whole process open forever.
    /// </summary>
    public TimeSpan GracefulTimeout { get; init; } = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Budget for the <see cref="PtyShutdownTrigger.ConsoleCtrl"/> path, deliberately shorter: Windows gives a
    /// console control handler a bounded window (a few seconds, per
    /// <c>HKCU\Control Panel\Desktop\WaitToKillAppTimeout</c> and the console host's own policy) for
    /// <c>CTRL_CLOSE_EVENT</c>/<c>CTRL_LOGOFF_EVENT</c>/<c>CTRL_SHUTDOWN_EVENT</c> before killing the process
    /// outright. Overrunning it does not buy more time, it just means the teardown is cut off at an arbitrary
    /// point instead of a chosen one - so this path aims to finish inside the window, and whatever is left falls
    /// to the job object's kill-on-close.
    /// </summary>
    public TimeSpan ConsoleCtrlTimeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Whether <see cref="PtyShutdownCoordinator.Install"/> registers the native
    /// <c>SetConsoleCtrlHandler</c> callback. Off is useful for a windowed process with no console.</summary>
    public bool InstallConsoleCtrlHandler { get; init; } = true;

    /// <summary>Whether <see cref="PtyShutdownCoordinator.Install"/> subscribes <c>AppDomain.ProcessExit</c>.</summary>
    public bool InstallProcessExit { get; init; } = true;

    /// <summary>
    /// Whether <see cref="PtyShutdownCoordinator.Install"/> also subscribes <c>Console.CancelKeyPress</c>.
    /// Redundant with the native handler for Ctrl+C/Ctrl+Break and included only as the managed half of
    /// belt-and-braces (it still fires if the native registration failed). The handler never sets
    /// <c>e.Cancel</c>: whether Ctrl+C should abort the app is the host's policy, not this class's - see the class
    /// remarks on non-interference.
    /// </summary>
    public bool InstallCancelKeyPress { get; init; } = true;

    /// <summary>Test seam for the native registration, so the install/uninstall bookkeeping can be asserted
    /// without touching the real process's console handlers. Returns false to simulate a failed registration.</summary>
    public Func<Delegate, bool, bool>? ConsoleCtrlHandlerInstaller { get; init; }
}

/// <summary>
/// P3-T4, first half: makes app exit actually reach <see cref="PtyRegistry.CloseAllAsync"/> down <b>all three</b>
/// exit paths rather than just the tidy one -
/// <list type="number">
/// <item><b>normal exit</b> - a <c>try/finally</c> around the UI loop calling <see cref="Dispose"/> (or
/// <see cref="Shutdown"/>) directly;</item>
/// <item><b>console control events</b> - Ctrl+C, Ctrl+Break, <i>and</i> the console window's close button /
/// logoff / shutdown, via a real <c>SetConsoleCtrlHandler</c> callback. The native handler is used rather than
/// only <c>Console.CancelKeyPress</c> because the managed event covers Ctrl+C/Ctrl+Break <i>only</i>: closing the
/// console window raises <c>CTRL_CLOSE_EVENT</c>, which never surfaces as <c>CancelKeyPress</c>, so a
/// CancelKeyPress-only app leaks every session when the user clicks the X;</item>
/// <item><b><c>AppDomain.ProcessExit</c></b> - the last chance, and the one that catches
/// <c>Environment.Exit</c>/a normal return from <c>Main</c> that skipped the <c>finally</c>.</item>
/// </list>
/// The paths overlap on purpose and converge on one teardown: an interlocked gate means the first trigger runs it
/// and every later trigger returns <see cref="PtyShutdownOutcome.AlreadyShutDown"/> (a late caller waits, briefly
/// and boundedly, for the in-flight one rather than racing it).
///
/// <para><b>Graceful, not eager.</b> This class adds no killing of its own; it calls
/// <see cref="PtyRegistry.CloseAllAsync"/>, which is already graceful-then-force per session (P3-T2): close
/// stdin, let the child exit on its own, and only escalate to <c>Kill(entireProcessTree: true)</c> after
/// <see cref="PtyRegistryOptions.CloseTimeout"/>. That ordering is what lets <c>claude</c> flush its transcript
/// and fire its session-end signal into the existing <c>EventServer</c> - a shutdown that killed first would
/// silently degrade telemetry, which is the point of doing this at all rather than just letting the job object's
/// kill-on-close reap everything.</para>
///
/// <para><b>Bounded, and never able to wedge the exit.</b> Every path is capped
/// (<see cref="PtyShutdownOptions.GracefulTimeout"/>, or the shorter
/// <see cref="PtyShutdownOptions.ConsoleCtrlTimeout"/>) and every handler body is wrapped so that a throw or a
/// timeout still records a result, still releases anyone waiting, and still lets the remaining handlers run: a
/// shutdown handler that hangs or throws blocks or aborts the whole process exit, which is worse than the leak it
/// was trying to prevent. Whatever the budget did not manage falls through to
/// <see cref="GlaudeJobObject"/>'s kill-on-close, so "timed out" still is not "orphaned".</para>
///
/// <para><b>Non-interference.</b> The native handler always returns <c>false</c> ("not handled") and the
/// <c>CancelKeyPress</c> handler never sets <c>e.Cancel</c>. So installing this changes <i>no</i> control-flow
/// policy: an existing Ctrl+C handler that closes a window (as <c>RunCombinedAsync</c> already has) keeps working
/// exactly as before, and this class only adds the session teardown alongside it. It is a cleanup hook, not a
/// signal handler that takes over.</para>
///
/// <para><b>Why this is not installed in <c>RunCombinedAsync</c> yet.</b> See
/// <see cref="PtyShutdownReconcileSmokeTest"/>'s remarks: the real combined-start path runs
/// <c>MonitorForm</c> and has no <see cref="PtyRegistry"/>/session concept at all, so installing this there today
/// would register process-wide console handlers for a registry that is guaranteed empty - all risk, no effect.
/// Wiring it up belongs to the task that actually swaps <c>MonitorForm</c> for the WPF <c>MainWindow</c>; the
/// call site is one line (<c>using var shutdown = new PtyShutdownCoordinator(new
/// PtyRegistryShutdownTarget(registry)).Install();</c> before the UI loop).</para>
///
/// <para><b>Threading.</b> Every member is safe from any thread. <see cref="Shutdown"/> blocks (there is no later
/// opportunity at process exit) but never on the calling thread's own work: the teardown runs on the thread pool,
/// and <see cref="PtyRegistry"/>'s close path takes no UI thread, so calling it from the WPF dispatcher or a
/// WinForms UI thread cannot deadlock.</para>
/// </summary>
public sealed class PtyShutdownCoordinator : IDisposable
{
    private const uint CtrlCEvent = 0;
    private const uint CtrlBreakEvent = 1;
    private const uint CtrlCloseEvent = 2;
    private const uint CtrlLogoffEvent = 5;
    private const uint CtrlShutdownEvent = 6;

    private readonly IPtyShutdownTarget _target;
    private readonly PtyShutdownOptions _options;
    private readonly ManualResetEventSlim _completed = new(false);
    private readonly object _installGate = new();

    /// <summary>
    /// Kept in a field on purpose, and this is not stylistic: <c>SetConsoleCtrlHandler</c> stores a raw function
    /// pointer to the marshalled stub, and the CLR is free to collect that stub the moment the delegate becomes
    /// unreachable - after which the next Ctrl+C calls freed code and crashes the process. The field is what keeps
    /// it alive for as long as the handler is registered. (Same class of "must stay rooted" hazard as
    /// <see cref="GlaudeJobObject"/>'s handle.)
    /// </summary>
    private ConsoleCtrlDelegate? _nativeHandler;

    private ConsoleCancelEventHandler? _cancelKeyPressHandler;
    private EventHandler? _processExitHandler;
    private int _shutdownStarted;
    private int _disposed;
    private PtyShutdownResult? _lastResult;

    public PtyShutdownCoordinator(IPtyShutdownTarget target, PtyShutdownOptions? options = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _options = options ?? new PtyShutdownOptions();
    }

    private delegate bool ConsoleCtrlDelegate(uint ctrlType);

    /// <summary>Raised once, after the single teardown finishes, whatever its outcome. Raised on whichever thread
    /// ran the teardown; a throwing subscriber is swallowed (it must not be able to break an exit path).</summary>
    public event EventHandler<PtyShutdownResult>? ShutdownCompleted;

    /// <summary>Whether the one teardown has started (or finished).</summary>
    public bool HasShutDown => Volatile.Read(ref _shutdownStarted) != 0;

    /// <summary>The single teardown's result, or null until it has finished.</summary>
    public PtyShutdownResult? LastResult => Volatile.Read(ref _lastResult);

    /// <summary>Whether the native <c>SetConsoleCtrlHandler</c> registration succeeded (false when it was
    /// disabled, not attempted, or rejected - e.g. a process with no console).</summary>
    public bool ConsoleCtrlHandlerInstalled { get; private set; }

    /// <summary>Whether <c>AppDomain.ProcessExit</c> is currently subscribed.</summary>
    public bool ProcessExitHandlerInstalled => _processExitHandler is not null;

    /// <summary>Whether <c>Console.CancelKeyPress</c> is currently subscribed.</summary>
    public bool CancelKeyPressHandlerInstalled => _cancelKeyPressHandler is not null;

    /// <summary>
    /// Registers the belt-and-braces handlers. Idempotent, never throws (a rejected native registration is
    /// recorded in <see cref="ConsoleCtrlHandlerInstalled"/>, not raised - a process with no console must still be
    /// able to install the other two), and returns <c>this</c> so it can be used as
    /// <c>using var shutdown = new PtyShutdownCoordinator(target).Install();</c>.
    /// </summary>
    public PtyShutdownCoordinator Install()
    {
        lock (_installGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return this;
            }

            if (_options.InstallConsoleCtrlHandler && _nativeHandler is null)
            {
                // Assign the field BEFORE handing the delegate to native code, so it is rooted from the instant
                // the function pointer becomes reachable by the OS.
                var handler = new ConsoleCtrlDelegate(HandleConsoleCtrlEvent);
                _nativeHandler = handler;
                try
                {
                    ConsoleCtrlHandlerInstalled = AddOrRemoveConsoleCtrlHandler(handler, add: true);
                }
                catch (Exception)
                {
                    ConsoleCtrlHandlerInstalled = false;
                }

                if (!ConsoleCtrlHandlerInstalled)
                {
                    _nativeHandler = null;
                }
            }

            if (_options.InstallProcessExit && _processExitHandler is null)
            {
                _processExitHandler = (_, _) => OnProcessExit();
                AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
            }

            if (_options.InstallCancelKeyPress && _cancelKeyPressHandler is null)
            {
                // Deliberately does NOT set e.Cancel - see the class remarks on non-interference.
                _cancelKeyPressHandler = (_, _) => Shutdown(PtyShutdownTrigger.ConsoleCtrl);
                try
                {
                    Console.CancelKeyPress += _cancelKeyPressHandler;
                }
                catch (Exception)
                {
                    _cancelKeyPressHandler = null;
                }
            }

            return this;
        }
    }

    /// <summary>
    /// Unregisters the handlers. Idempotent, never throws. Called by <see cref="Dispose"/> before the final
    /// teardown so the teardown cannot re-enter through one of its own handlers.
    /// </summary>
    public void Uninstall()
    {
        lock (_installGate)
        {
            if (_nativeHandler is { } native)
            {
                try
                {
                    AddOrRemoveConsoleCtrlHandler(native, add: false);
                }
                catch (Exception)
                {
                    // Nothing useful to do; the delegate stays rooted by this field until the object dies, which
                    // is the safe direction (a stale-but-live callback beats a freed one).
                }

                _nativeHandler = null;
                ConsoleCtrlHandlerInstalled = false;
            }

            if (_processExitHandler is { } processExit)
            {
                try
                {
                    AppDomain.CurrentDomain.ProcessExit -= processExit;
                }
                catch (Exception)
                {
                    // Best effort.
                }

                _processExitHandler = null;
            }

            if (_cancelKeyPressHandler is { } cancelKey)
            {
                try
                {
                    Console.CancelKeyPress -= cancelKey;
                }
                catch (Exception)
                {
                    // Best effort.
                }

                _cancelKeyPressHandler = null;
            }
        }
    }

    /// <summary>
    /// The one teardown, from whichever path got here first. Blocking, bounded, and never throws.
    ///
    /// <para>A second caller does not run a second teardown: it waits (bounded by the same budget) for the
    /// in-flight one and gets <see cref="PtyShutdownOutcome.AlreadyShutDown"/>. That waiting matters for the real
    /// overlap this class is built for - <c>CTRL_CLOSE_EVENT</c> arriving while the <c>finally</c> path is already
    /// tearing down, where returning immediately would let the OS kill the process mid-teardown.</para>
    /// </summary>
    /// <param name="trigger">Which exit path this is; selects the budget.</param>
    public PtyShutdownResult Shutdown(PtyShutdownTrigger trigger = PtyShutdownTrigger.Explicit)
    {
        var budget = trigger == PtyShutdownTrigger.ConsoleCtrl
            ? _options.ConsoleCtrlTimeout
            : _options.GracefulTimeout;

        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            // Somebody else owns the teardown. Give them the budget to finish, then report.
            try
            {
                _completed.Wait(Clamp(budget));
            }
            catch (Exception)
            {
                // A disposed wait handle (racing Dispose) is not worth failing an exit path over.
            }

            return new PtyShutdownResult(trigger, PtyShutdownOutcome.AlreadyShutDown, TimeSpan.Zero, 0, null);
        }

        var stopwatch = Stopwatch.StartNew();
        var outcome = PtyShutdownOutcome.Faulted;
        var closed = 0;
        Exception? failure = null;

        var cts = new CancellationTokenSource();
        var teardown = Task.CompletedTask;
        try
        {
            // Task.Run so that a target which throws synchronously, or blocks before its first await, still lands
            // in the bounded wait below instead of blocking here forever.
            var work = Task.Run(() => _target.CloseAllAsync(cts.Token));
            teardown = work;

            // The budget is enforced by this Wait, NOT by cts.CancelAfter. That matters: disposing a
            // CancellationTokenSource cancels its pending timer *without* signalling the token, so a CancelAfter
            // whose deadline coincides with the end of a `using` block can silently never fire - which would
            // leave the registry waiting out its own full CloseTimeout instead of being told to escalate.
            // Cancelling explicitly on the timeout path below is what makes the signal deterministic.
            if (work.Wait(Clamp(budget)))
            {
                closed = work.Result;
                outcome = PtyShutdownOutcome.Completed;
            }
            else
            {
                outcome = PtyShutdownOutcome.TimedOut;

                // Abandoned, not forgotten. Cancelling asks PtyRegistry to bring its force-kill forward (its
                // token cancels only the *waiting*, never the kill), so the children still get killed rather
                // than being left to the job object - which remains the backstop for whatever does not finish
                // before the process dies.
                try
                {
                    cts.Cancel();
                }
                catch (Exception)
                {
                    // A racing dispose is not worth failing an exit path over.
                }
            }
        }
        catch (Exception ex)
        {
            failure = Unwrap(ex);
            outcome = PtyShutdownOutcome.Faulted;
        }
        finally
        {
            // The token source outlives this method on the timeout path (the abandoned teardown still holds its
            // token), so it is disposed from a continuation instead of here - and that continuation also observes
            // the abandoned task's exception, which would otherwise surface as an unobserved-task fault.
            teardown.ContinueWith(
                static (task, state) =>
                {
                    _ = task.Exception;
                    ((CancellationTokenSource)state!).Dispose();
                },
                cts,
                CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            // Always: record the result and release every waiter, even if the teardown threw. A handler that
            // skipped this would leave a concurrent Ctrl+C blocked on _completed for its whole budget.
            stopwatch.Stop();
            var result = new PtyShutdownResult(trigger, outcome, stopwatch.Elapsed, closed, failure);
            Volatile.Write(ref _lastResult, result);
            try
            {
                _completed.Set();
            }
            catch (Exception)
            {
                // Disposed under us; waiters are already unblocked by that.
            }

            try
            {
                ShutdownCompleted?.Invoke(this, result);
            }
            catch (Exception)
            {
                // A throwing subscriber must not turn into a failed process exit.
            }
        }

        return LastResult!;
    }

    /// <summary>
    /// The native <c>SetConsoleCtrlHandler</c> callback body, public so the console-control exit path can be
    /// exercised for real in tests and in <c>pty-shutdown-orphan-test</c> - sending a genuine Ctrl+C to one's own
    /// process group is not something a test process can do without also killing the test runner.
    ///
    /// <para>Always returns <c>false</c> ("not handled"), so the next handler in the chain and the default
    /// behaviour both still run - see the class remarks on non-interference. It only ever <i>adds</i> the session
    /// teardown.</para>
    /// </summary>
    /// <param name="ctrlType">One of <c>CTRL_C_EVENT</c> (0), <c>CTRL_BREAK_EVENT</c> (1),
    /// <c>CTRL_CLOSE_EVENT</c> (2), <c>CTRL_LOGOFF_EVENT</c> (5), <c>CTRL_SHUTDOWN_EVENT</c> (6).</param>
    public bool HandleConsoleCtrlEvent(uint ctrlType)
    {
        try
        {
            if (ctrlType is CtrlCEvent or CtrlBreakEvent or CtrlCloseEvent or CtrlLogoffEvent or CtrlShutdownEvent)
            {
                Shutdown(PtyShutdownTrigger.ConsoleCtrl);
            }
        }
        catch (Exception)
        {
            // Shutdown does not throw; this is the belt on the braces. An exception escaping a native callback
            // is undefined behaviour at best.
        }

        return false;
    }

    /// <summary>The <c>AppDomain.ProcessExit</c> handler body, public for the same reason as
    /// <see cref="HandleConsoleCtrlEvent"/>: it makes the third exit path directly assertable.</summary>
    public void OnProcessExit()
    {
        try
        {
            Shutdown(PtyShutdownTrigger.ProcessExit);
        }
        catch (Exception)
        {
            // Never let an exit handler throw.
        }
    }

    /// <summary>
    /// Uninstalls the handlers and then runs the teardown (a no-op if a trigger already did). Idempotent, never
    /// throws - safe from a <c>finally</c>. Uninstalling first is what makes this path not re-enter itself
    /// through <c>ProcessExit</c> later in the same exit.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            Uninstall();
        }
        finally
        {
            // In a finally: a failure to unhook must not be allowed to skip the actual session cleanup, which is
            // the only irreversible part.
            try
            {
                Shutdown(PtyShutdownTrigger.Explicit);
            }
            catch (Exception)
            {
                // Shutdown does not throw.
            }

            _completed.Dispose();
        }
    }

    private static TimeSpan Clamp(TimeSpan budget) =>
        budget <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : budget;

    private static Exception Unwrap(Exception ex) =>
        ex is AggregateException aggregate && aggregate.InnerExceptions.Count == 1
            ? aggregate.InnerExceptions[0]
            : ex;

    private bool AddOrRemoveConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add) =>
        _options.ConsoleCtrlHandlerInstaller is { } installer
            ? installer(handler, add)
            : NativeMethods.SetConsoleCtrlHandler(handler, add);

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetConsoleCtrlHandler(
            ConsoleCtrlDelegate handlerRoutine,
            [MarshalAs(UnmanagedType.Bool)] bool add);
    }
}

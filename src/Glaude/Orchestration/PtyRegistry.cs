namespace Glaude.Orchestration;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>What a <see cref="PtyRegistry.CloseAsync"/> actually managed to do.</summary>
public enum PtyCloseOutcome
{
    /// <summary>Nothing was registered under that tabId (and no close was in flight for it). A safe no-op -
    /// never an error: closing an unknown tab is what a double-close, a stale UI command, or an
    /// app-shutdown sweep racing a self-exit all look like from here.</summary>
    NotFound,

    /// <summary>The session was disposed and the child process was independently observed to be gone.</summary>
    Closed,

    /// <summary>The child was still alive after <see cref="PtySession.Dispose"/> plus the close timeout, so
    /// <c>Process.Kill(entireProcessTree: true)</c> was used, and the child is now gone.</summary>
    ForceKilled,

    /// <summary>Force-kill was attempted and the child was <i>still</i> observable afterwards (or the kill
    /// itself failed). The job object's kill-on-close remains the last backstop; see
    /// <see cref="PtyCloseResult.Failure"/>.</summary>
    ForceKillFailed,

    /// <summary>The session was disposed and reported its exit, but no trusted OS handle for the child was
    /// held, so "the process is really gone" could not be verified and force-kill was deliberately
    /// <b>not</b> attempted - killing by a PID we cannot prove identity for could kill an unrelated
    /// process. See <see cref="PtyRegistry"/>'s "PID-reuse" remarks.</summary>
    ExitUnverified,

    /// <summary>Teardown threw somewhere unexpected; <see cref="PtyCloseResult.Failure"/> carries it. The
    /// entry is still removed from the registry (it is never resurrected), and
    /// <see cref="PtyRegistry.SessionEnded"/> still fires.</summary>
    Faulted,
}

/// <summary>The result of one close attempt. Never thrown - <see cref="PtyRegistry.CloseAsync"/> reports
/// failures as data so a bulk teardown cannot be derailed by one bad session.</summary>
/// <param name="TabId">The tab that was closed.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="ProcessId">The child's PID, or null for <see cref="PtyCloseOutcome.NotFound"/>.</param>
/// <param name="ExitCode">The child's exit code if it was observed, else null.</param>
/// <param name="Reason">The session's own exit classification (<see cref="PtySession.ExitReason"/>), or
/// null for <see cref="PtyCloseOutcome.NotFound"/>.</param>
/// <param name="Elapsed">Wall-clock duration of the close.</param>
/// <param name="Failure">The exception, for <see cref="PtyCloseOutcome.Faulted"/>/<see cref="PtyCloseOutcome.ForceKillFailed"/>.</param>
public sealed record PtyCloseResult(
    string TabId,
    PtyCloseOutcome Outcome,
    int? ProcessId,
    int? ExitCode,
    PtySessionExitReason? Reason,
    TimeSpan Elapsed,
    Exception? Failure)
{
    internal static PtyCloseResult NotFound(string tabId) =>
        new(tabId, PtyCloseOutcome.NotFound, null, null, null, TimeSpan.Zero, null);

    /// <summary>Whether the child is known to be gone (either it exited or it was killed).</summary>
    public bool ChildIsGone => Outcome is PtyCloseOutcome.Closed or PtyCloseOutcome.ForceKilled;
}

/// <summary>One live registration, as handed out by <see cref="PtyRegistry.Snapshot"/>.</summary>
/// <param name="TabId">The tab id the session is registered under.</param>
/// <param name="Session">The session. <b>Do not dispose it</b> - see <see cref="PtyRegistry"/>.</param>
public sealed record PtyRegistration(string TabId, PtySession Session);

/// <summary>A session left the registry. Fires exactly once per registration, whether it exited on its own
/// or was closed by Glaude.</summary>
public sealed class PtySessionEndedEventArgs : EventArgs
{
    public PtySessionEndedEventArgs(string tabId, PtySessionExitReason reason, int? exitCode, PtyCloseOutcome outcome)
    {
        TabId = tabId;
        Reason = reason;
        ExitCode = exitCode;
        Outcome = outcome;
    }

    /// <summary>The tab whose session ended. Already removed from the registry when this fires.</summary>
    public string TabId { get; }

    /// <summary>
    /// <see cref="PtySessionExitReason.ChildExited"/> = the child ended by itself (`claude` finished, the
    /// user typed <c>exit</c>, it crashed) - this is the case a future <c>TabsViewModel</c> (P3-T1) should
    /// surface as "session ended" UI, keeping the tab open with frozen scrollback (P4-T5).
    /// <see cref="PtySessionExitReason.TornDown"/> = Glaude closed it, so the UI already knows.
    /// </summary>
    public PtySessionExitReason Reason { get; }

    /// <summary>The child's exit code if it was observed, else null.</summary>
    public int? ExitCode { get; }

    /// <summary>How the teardown went - <see cref="PtyCloseOutcome.ForceKilled"/> and
    /// <see cref="PtyCloseOutcome.ForceKillFailed"/> are worth logging.</summary>
    public PtyCloseOutcome Outcome { get; }
}

/// <summary>Tunables for one <see cref="PtyRegistry"/>.</summary>
public sealed class PtyRegistryOptions
{
    /// <summary>
    /// How long a close waits, after <see cref="PtySession.Dispose"/> has returned, for the child process
    /// to actually disappear before escalating to <c>Process.Kill(entireProcessTree: true)</c>.
    ///
    /// <para>Disposal is not instantaneous by design: <see cref="ConPtySession"/>'s documented close order
    /// closes the child's stdin <i>first</i> precisely so a well-behaved child gets a window to exit on its
    /// own, and <c>ClosePseudoConsole</c> then blocks until conhost has flushed. A cooperative
    /// <c>claude</c>/<c>cmd.exe</c> is gone well inside a second; this timeout only ever expires for a child
    /// that is ignoring EOF, wedged, or blocked in a driver.</para>
    ///
    /// <para>It is wall-clock, which includes thread-pool scheduling latency - measured in
    /// <c>pty-registry-stress-test</c>, tearing 20 children down simultaneously on a saturated pool
    /// occasionally pushes one close past this timeout and into a force-kill even though its child was
    /// exiting normally. That is harmless (the session was already torn down, the child was already dying)
    /// and is the deliberately safe direction to err in: an unnecessary kill of a dying child costs nothing,
    /// an unnoticed orphan is the failure this class exists to prevent.</para>
    /// </summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait after a force-kill before declaring <see cref="PtyCloseOutcome.ForceKillFailed"/>.</summary>
    public TimeSpan ForceKillGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long <see cref="PtyRegistry.Dispose"/> (the synchronous app-exit path) waits for
    /// <see cref="PtyRegistry.CloseAllAsync"/> overall. Deliberately larger than
    /// <see cref="CloseTimeout"/> + <see cref="ForceKillGrace"/> but not N times larger, because closes run
    /// concurrently.
    /// </summary>
    public TimeSpan DisposeTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Test seam: opens an independent observer for a child PID, or returns null if it cannot be trusted.
    /// Production default is <see cref="PtyRegistry.OpenProcessObserver"/>. Overridden by tests to make the
    /// timeout/force-kill path deterministic without needing a genuinely unkillable child.
    /// </summary>
    public Func<PtySession, IPtyProcessObserver?>? ProcessObserverFactory { get; init; }
}

/// <summary>
/// The registry's view of a child process: exactly the three things a close needs. Exists as an interface
/// only so the timeout → force-kill escalation is unit-testable (a fake that never exits) instead of
/// depending on producing a real wedged child.
/// </summary>
public interface IPtyProcessObserver : IDisposable
{
    /// <summary>The PID being observed.</summary>
    int ProcessId { get; }

    /// <summary>Whether the process has exited.</summary>
    bool HasExited { get; }

    /// <summary>Completes when the process exits, or throws <see cref="OperationCanceledException"/>.</summary>
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Terminates the process and its descendants (<c>Process.Kill(entireProcessTree: true)</c>).</summary>
    void KillTree();
}

/// <summary>
/// P3-T2: the app-lifetime <c>tabId → <see cref="PtySession"/></c> map, and <b>the single owner of
/// <see cref="PtySession.Dispose"/></b>.
///
/// <para><b>Ownership rule for future consumers (P3-T1, P3-T4, P4-T5) - read this first.</b> Nothing
/// outside this class may call <see cref="PtySession.Dispose"/> on a registered session. Tab ViewModels
/// close a tab by calling <see cref="CloseAsync"/>; the app-exit path calls
/// <see cref="CloseAllAsync"/>/<see cref="Dispose"/>. The reason is not tidiness: disposal here is a
/// multi-step escalation (dispose → verify the process is gone → force-kill the tree) that has to be
/// serialized per session and paired with removal from this map, and a ViewModel that disposes a session
/// directly bypasses the force-kill backstop, leaves a dead session registered, and makes
/// <see cref="SessionEnded"/> fire with a teardown reason nobody requested. Enumeration
/// (<see cref="Snapshot"/>) deliberately hands out live <see cref="PtySession"/> references for
/// reading/writing (input, resize, output) - just never for disposing. This is also what the P2-T6
/// stopgap in <c>MainWindow.CreateSession_Click</c>/<c>MainWindow.Closed</c> (a plain
/// <c>List&lt;(tabId, session)&gt;</c> that disposes sessions itself) is meant to be replaced by.</para>
///
/// <para><b>tabIds</b> are generated by the caller and opaque here, compared with
/// <see cref="StringComparer.Ordinal"/>. The convention in this codebase is a GUID
/// (<c>Guid.NewGuid().ToString("N")</c>, as <c>MainWindow</c> already does, matching the session GUID
/// <see cref="PtySession.CreateClaudeSpec"/> passes as <c>--session-id</c>) - which also matters for
/// <c>/pty/{tabId}</c>'s security posture, where an unguessable id is part of the route's defence.</para>
///
/// <para><b>Removal happens before disposal, not after.</b> The close pipeline is: win a per-entry
/// interlocked gate → publish the entry into the "closing" map → remove it from the live map → dispose →
/// verify/kill. Removing first is what makes a concurrent second close a no-op <i>by construction</i>
/// rather than by luck: while a session is being torn down it is unreachable through
/// <see cref="TryGet"/>/<see cref="Snapshot"/>, so no other caller can find it, hand it to a ViewModel, or
/// route new input to it. The reverse order (dispose, then remove) leaves a half-destroyed session
/// discoverable for the entire duration of the teardown - which is milliseconds to seconds, not
/// microseconds, since <c>ClosePseudoConsole</c> blocks until conhost flushes. The removal itself uses the
/// identity-checked <c>TryRemove(KeyValuePair)</c> overload, so it can only ever remove <i>this</i> entry
/// and never a different session that has since been registered under the same tabId.</para>
///
/// <para><b>The three races this class exists to get right.</b>
/// <list type="number">
/// <item><i>A close racing a session that is already exiting on its own.</i> Both paths funnel into the
/// same per-entry pipeline: every registration gets a continuation on
/// <see cref="PtySession.ExitTask"/> that starts a close, and a user-initiated
/// <see cref="CloseAsync"/> starts the same one. Whichever arrives first wins the interlocked gate and
/// runs teardown exactly once; the loser awaits the winner's result. So a child dying at the same instant
/// the user closes the tab cannot double-dispose, and <see cref="PtySession.ExitReason"/> still reports
/// <see cref="PtySessionExitReason.ChildExited"/> because <see cref="PtySession.Dispose"/> observes an
/// already-completed exit before it declares teardown (that classification is deliberately not
/// re-derived here - see <see cref="PtySession.ExitReason"/>'s own remarks).</item>
/// <item><i>Two concurrent closes of the same tabId.</i> The second returns the first's
/// <see cref="Task{TResult}"/> - the same <see cref="PtyCloseResult"/>, one dispose, at most one
/// force-kill. It is never an exception and never a second kill attempt. If it arrives after the whole
/// close finished, it degrades to <see cref="PtyCloseOutcome.NotFound"/>, which is also a no-op. The
/// window where it would find neither the live entry nor the in-flight one is closed by publishing into
/// the closing map <i>before</i> removing from the live map.</item>
/// <item><i>Dictionary mutation during teardown / enumeration.</i> A
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> holds the state and <see cref="Snapshot"/> materialises
/// a list, so an app-shutdown sweep enumerating every session while a <see cref="CloseAsync"/> for one of
/// them is in flight is well-defined: the sweep either does not see that tab, or sees it and joins the
/// in-flight close.</item>
/// </list></para>
///
/// <para><b>Force-kill and PID reuse.</b> <see cref="PtySession"/> deliberately does not expose the
/// child's process handle (handle ownership stays inside <see cref="ConPtySession"/>, which closes it
/// during <c>Dispose</c>), so this class opens its own <c>Process</c> at registration time - while the
/// child is alive - and holds it for the whole registration. Holding an open handle keeps the kernel
/// process object alive, which is what makes the PID <i>stable</i>: it cannot be recycled underneath us,
/// so the eventual <c>Kill(entireProcessTree: true)</c> provably targets this session's child and not some
/// unrelated process that inherited its number. Two extra guards on top: the handle is only opened if the
/// session has not already exited, and it is discarded unless its start time matches
/// <see cref="PtySession.ProcessStartTimeUtc"/> (captured while the child was suspended). If no trusted
/// handle exists the close reports <see cref="PtyCloseOutcome.ExitUnverified"/> and <b>does not kill
/// anything</b>; <see cref="GlaudeJobObject.Shared"/>'s kill-on-close is the remaining backstop.</para>
///
/// <para><b>Threading.</b> Every public member is safe from any thread. <see cref="CloseAsync"/> and
/// <see cref="CloseAllAsync"/> never block the caller: the blocking parts (<see cref="PtySession.Dispose"/>
/// joins the pump thread) run on the thread pool, and every internal await is
/// <c>ConfigureAwait(false)</c>, so awaiting them from the WPF dispatcher cannot deadlock.
/// <see cref="SessionEnded"/> is raised on a thread-pool thread - marshal to the UI yourself. A throwing
/// subscriber is swallowed rather than allowed to abort a teardown.</para>
/// </summary>
public sealed class PtyRegistry : IPtySessionHost, IDisposable
{
    /// <summary>
    /// The process-wide registry, created on first use - same convention (and same rationale) as
    /// <see cref="GlaudeJobObject.Shared"/>: a <c>static readonly</c> <see cref="Lazy{T}"/> field is a GC
    /// root for the life of the AppDomain, which is exactly the "app-lifetime singleton" lifetime the plan
    /// asks for. Unlike <see cref="GlaudeJobObject.Shared"/> this one <i>is</i> meant to be disposed
    /// exactly once, on the app-exit path (P3-T4's <c>try/finally</c> around <c>Application.Run</c>);
    /// disposing it closes every session and refuses further registrations.
    ///
    /// <para>Construct an instance directly instead (the constructor is public) wherever a scoped, own-able
    /// registry is better - tests, diagnostics, and any future DI composition root. Nothing in this class
    /// depends on being the singleton.</para>
    /// </summary>
    public static PtyRegistry Shared => SharedLazy.Value;

    private static readonly Lazy<PtyRegistry> SharedLazy =
        new(() => new PtyRegistry(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Entries whose teardown has started but not finished. Published <i>before</i> the entry leaves
    /// <see cref="_sessions"/> so a concurrent <see cref="CloseAsync"/> can always find one of the two -
    /// see race 2 in the class remarks. Never a source of disposal: callers can only await the in-flight
    /// result through it.
    /// </summary>
    private readonly ConcurrentDictionary<string, Entry> _closing = new(StringComparer.Ordinal);

    private readonly PtyRegistryOptions _options;
    private readonly Func<PtySession, IPtyProcessObserver?> _observerFactory;
    private int _disposed;

    public PtyRegistry(PtyRegistryOptions? options = null)
    {
        _options = options ?? new PtyRegistryOptions();
        _observerFactory = _options.ProcessObserverFactory ?? OpenProcessObserver;
    }

    /// <summary>
    /// A registered session has ended and has already been removed from the registry. Fires exactly once
    /// per registration, from both the self-exit and the explicit-close path, on a thread-pool thread.
    ///
    /// <para>This is the notification primitive P3-T1's <c>TabsViewModel</c> needs in order to show
    /// "session ended" for a child that died on its own (<see cref="PtySessionExitReason.ChildExited"/>)
    /// without polling. No UI is built here.</para>
    /// </summary>
    public event EventHandler<PtySessionEndedEventArgs>? SessionEnded;

    /// <summary>How many sessions are currently registered (excludes any mid-teardown).</summary>
    public int Count => _sessions.Count;

    /// <summary>Whether <see cref="Dispose"/> has run - after which <see cref="Register"/> throws.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Production <see cref="IPtyProcessObserver"/> factory: an independently owned <c>Process</c> for the
    /// session's child, or null when it cannot be trusted - see the class remarks on PID reuse for why
    /// "cannot be trusted" is a first-class outcome rather than a best-effort guess.
    /// </summary>
    public static IPtyProcessObserver? OpenProcessObserver(PtySession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        // Guard 1: if the child is already gone, its PID may already have been recycled, so opening it
        // could hand us an unrelated process. Not opening one costs only the ability to *verify* the exit
        // (the session's own ExitTask still reports it) and deliberately gives up the ability to kill.
        if (session.ExitTask.IsCompleted)
        {
            return null;
        }

        Process? process = null;
        try
        {
            process = Process.GetProcessById(session.ProcessId);

            // Guard 2: PID+start-time pairing, the same guard PtyPidRegistry applies across restarts. If
            // the session could not record a start time we accept the handle (guard 1 plus the handle
            // itself already make recycling essentially impossible); if it could, it must match.
            if (session.ProcessStartTimeUtc is { } expected)
            {
                var actual = process.StartTime.ToUniversalTime();
                if ((actual - expected).Duration() > TimeSpan.FromSeconds(2))
                {
                    process.Dispose();
                    return null;
                }
            }

            return new ProcessObserver(process);
        }
        catch (Exception)
        {
            // Gone between the check and the open, access denied, or an unreadable start time: no trusted
            // handle. Never fail the registration over this.
            process?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Takes ownership of <paramref name="session"/> under <paramref name="tabId"/>, and from this moment
    /// on is the only thing allowed to dispose it.
    ///
    /// <para>Registering also arms the self-exit path: a continuation on
    /// <see cref="PtySession.ExitTask"/> runs the same close pipeline <see cref="CloseAsync"/> does, so a
    /// child that ends on its own is removed from the map and surfaced through
    /// <see cref="SessionEnded"/> without anybody polling. A session that has <i>already</i> exited may be
    /// registered - it will simply be closed again almost immediately by that continuation.</para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="tabId"/> is null/empty, or already registered.
    /// Duplicate tabIds are rejected rather than silently replaced: replacing would drop the only reference
    /// to a live session that this class is responsible for disposing, i.e. leak a `claude` process.</exception>
    /// <exception cref="ObjectDisposedException">The registry has been disposed (app is shutting down). If
    /// the disposal was detected only after the entry had been added - i.e. <see cref="Dispose"/> ran
    /// concurrently with this call - the registry has already started closing that session before throwing,
    /// so the caller may safely dispose <paramref name="session"/> as well: <see cref="PtySession.Dispose"/>
    /// is idempotent. Either way the session will not be left running and unreferenced.</exception>
    public void Register(string tabId, PtySession session)
    {
        ArgumentException.ThrowIfNullOrEmpty(tabId);
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var entry = new Entry(this, tabId, session);
        if (!_sessions.TryAdd(tabId, entry))
        {
            throw new ArgumentException($"A session is already registered under tabId '{tabId}'.", nameof(tabId));
        }

        // Only after the entry is reachable: a factory that touches the process must not be able to throw
        // out of Register and leave a session that is neither registered nor owned by the caller.
        entry.Observer = SafeOpenObserver(session);

        // Re-check disposal: Dispose may have swept the map between the TryAdd and here, in which case this
        // entry would sit in a disposed registry forever. Closing it is the honest outcome.
        if (IsDisposed && _sessions.TryGetValue(tabId, out var stillThere) && ReferenceEquals(stillThere, entry))
        {
            _ = BeginOrJoinClose(entry);
            throw new ObjectDisposedException(nameof(PtyRegistry));
        }

        // Self-exit arming. A continuation rather than the Exited event on purpose: it fires even if the
        // exit was already observed before this line (no lost-notification window), it fires exactly once,
        // it needs no unsubscription, and it cannot re-enter a Dispose that is in progress - the body only
        // hands work to BeginOrJoinClose, whose per-entry gate the in-progress teardown already holds.
        session.ExitTask.ContinueWith(
            static (_, state) =>
            {
                var self = (Entry)state!;
                _ = self.Owner.BeginOrJoinClose(self);
            },
            entry,
            CancellationToken.None,
            TaskContinuationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    /// <summary>Looks up a live registration. Returns false for an unknown tabId <i>and</i> for one whose
    /// teardown has already begun - a session being torn down is deliberately unreachable.</summary>
    public bool TryGet(string tabId, out PtySession? session)
    {
        session = null;
        if (string.IsNullOrEmpty(tabId))
        {
            return false;
        }

        if (_sessions.TryGetValue(tabId, out var entry))
        {
            session = entry.Session;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A point-in-time list of every live registration. Materialised (not a lazy view over the dictionary),
    /// so a caller can iterate it while closing entries - which is exactly what the app-exit sweep
    /// (P3-T4) and a future startup-orphan reconciliation do.
    ///
    /// <para>The returned <see cref="PtySession"/> objects are live and usable; they must not be
    /// disposed - see the class remarks.</para>
    /// </summary>
    public IReadOnlyList<PtyRegistration> Snapshot() =>
        _sessions.Select(pair => new PtyRegistration(pair.Key, pair.Value.Session)).ToArray();

    /// <summary>Just the tab ids, in no particular order.</summary>
    public IReadOnlyList<string> TabIds() => _sessions.Keys.ToArray();

    /// <summary>
    /// The one and only path to <see cref="PtySession.Dispose"/>: removes the session from the registry
    /// first, disposes it, waits <see cref="PtyRegistryOptions.CloseTimeout"/> for the child process to
    /// actually be gone, and force-kills the whole process tree if it is not.
    ///
    /// <para>Never throws for an unknown/already-closing tab and never double-disposes - see the class
    /// remarks for the exact orderings. Failures come back as <see cref="PtyCloseResult"/> data.</para>
    /// </summary>
    /// <param name="tabId">The tab to close.</param>
    /// <param name="cancellationToken">Cancels only the <i>waiting</i>. It never cancels the dispose (which
    /// has already happened by then) and never skips the force-kill - abandoning a half-torn-down child is
    /// precisely the orphan this class exists to prevent. Cancelling therefore just brings the force-kill
    /// forward.</param>
    public Task<PtyCloseResult> CloseAsync(string tabId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(tabId))
        {
            return Task.FromResult(PtyCloseResult.NotFound(tabId ?? string.Empty));
        }

        if (_sessions.TryGetValue(tabId, out var entry))
        {
            return BeginOrJoinClose(entry, cancellationToken);
        }

        // Not live. Either it never existed, or a close is in flight - and because the closing map is
        // published before the live-map removal, an in-flight close is guaranteed to be visible here.
        if (_closing.TryGetValue(tabId, out var closingEntry))
        {
            return closingEntry.CloseCompletion.Task;
        }

        return Task.FromResult(PtyCloseResult.NotFound(tabId));
    }

    /// <summary>
    /// Closes every registered session. Used by the app-exit path (P3-T4).
    ///
    /// <para>Closes run <b>concurrently</b>, so N tabs cost roughly one
    /// <see cref="PtyRegistryOptions.CloseTimeout"/> rather than N of them, and each one's failure is
    /// isolated: a session that throws or hangs produces its own <see cref="PtyCloseResult"/> and cannot
    /// stop the others (there is no shared lock on the teardown path and no exception escapes a single
    /// close). Repeats until the registry is empty or no progress is made, so a session registered while
    /// the sweep was running is still caught.</para>
    /// </summary>
    /// <returns>One result per tab closed by this call - including <see cref="PtyCloseOutcome.NotFound"/>
    /// for tabs that another caller finished closing first.</returns>
    public async Task<IReadOnlyList<PtyCloseResult>> CloseAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<PtyCloseResult>();

        // Bounded, but generous: each pass only re-runs for tabs registered *during* the previous pass.
        for (var pass = 0; pass < 8; pass++)
        {
            var tabIds = TabIds();
            if (tabIds.Count == 0)
            {
                break;
            }

            var closes = tabIds.Select(tabId => CloseOneNeverThrowsAsync(tabId, cancellationToken)).ToArray();
            results.AddRange(await Task.WhenAll(closes).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// Synchronous app-exit teardown: closes everything (bounded by
    /// <see cref="PtyRegistryOptions.DisposeTimeout"/>) and refuses further registrations. Idempotent and
    /// never throws, so it is safe from a <c>finally</c>, an <c>AppDomain.ProcessExit</c> handler, or a
    /// console Ctrl+C handler - the belt-and-braces paths P3-T4 will wire up.
    ///
    /// <para>Blocking, on purpose: at process exit there is no later opportunity to finish. It is safe to
    /// call from the UI thread because nothing on the close path needs that thread (all internal awaits are
    /// <c>ConfigureAwait(false)</c> and the blocking dispose runs on the thread pool).</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (!CloseAllAsync().Wait(_options.DisposeTimeout))
            {
                // Nothing further to do here: every child still standing is in GlaudeJobObject.Shared, and
                // the OS closing that job handle at process exit kills it (kill-on-close).
            }
        }
        catch
        {
            // Teardown on the way out is best-effort by contract; a throw from here could take down a
            // ProcessExit/Ctrl+C path.
        }
    }

    private async Task<PtyCloseResult> CloseOneNeverThrowsAsync(string tabId, CancellationToken cancellationToken)
    {
        try
        {
            return await CloseAsync(tabId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Defensive: CloseAsync is written not to throw, and this makes "one bad session cannot break
            // the bulk teardown" true structurally rather than by inspection.
            return new PtyCloseResult(tabId, PtyCloseOutcome.Faulted, null, null, null, TimeSpan.Zero, ex);
        }
    }

    /// <summary>
    /// Starts the teardown for <paramref name="entry"/>, or joins the one already running.
    ///
    /// <para>The ordering here is the whole point of the class - see the class remarks. Winning the gate is
    /// what confers the right to dispose, and only one caller can win it, ever.</para>
    /// </summary>
    private Task<PtyCloseResult> BeginOrJoinClose(Entry entry, CancellationToken cancellationToken = default)
    {
        if (!entry.TryBeginClose())
        {
            // Race 2 (and race 1): somebody else owns this teardown. Join their result - do not dispose,
            // do not kill, do not throw.
            return entry.CloseCompletion.Task;
        }

        // Published before the removal below, so CloseAsync's "not live -> is it closing?" lookup can never
        // fall into a gap and wrongly report NotFound while a teardown is in flight.
        _closing[entry.TabId] = entry;

        // Removal BEFORE disposal, identity-checked so it cannot remove a different session that was
        // registered under the same tabId in the meantime.
        _sessions.TryRemove(new KeyValuePair<string, Entry>(entry.TabId, entry));

        // Off the caller's thread: PtySession.Dispose blocks (it joins the pump thread), and callers include
        // the WPF dispatcher.
        return Task.Run(() => RunCloseAsync(entry, cancellationToken));
    }

    private async Task<PtyCloseResult> RunCloseAsync(Entry entry, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = PtyCloseOutcome.Faulted;
        Exception? failure = null;

        try
        {
            // Step 1: the session's own idempotent teardown (input pipe -> ClosePseudoConsole -> handles,
            // pump joined, ExitTask completed). This is the ONLY call to it in the whole app.
            try
            {
                entry.Session.Dispose();
            }
            catch (Exception ex)
            {
                // PtySession.Dispose is documented not to throw (except when called from its own pump
                // thread, which cannot happen here - we are on a thread-pool thread). Record it and still
                // proceed to the verify/kill steps: a failed dispose makes the force-kill *more* necessary,
                // not less.
                failure = ex;
            }

            // Step 2: verify the child is actually gone, independently of the session's own bookkeeping.
            if (entry.Observer is not { } observer)
            {
                // No trusted handle: wait for the session's own exit signal (Dispose always completes it),
                // then report honestly rather than killing a PID we cannot vouch for.
                await WaitForSessionExitAsync(entry.Session, _options.CloseTimeout, cancellationToken).ConfigureAwait(false);
                outcome = failure is null ? PtyCloseOutcome.ExitUnverified : PtyCloseOutcome.Faulted;
            }
            else if (await WaitForProcessGoneAsync(observer, _options.CloseTimeout, cancellationToken).ConfigureAwait(false))
            {
                outcome = failure is null ? PtyCloseOutcome.Closed : PtyCloseOutcome.Faulted;
            }
            else
            {
                // Step 3: last line of defence against an orphaned `claude` tree. entireProcessTree because
                // the child spawns its own children (node, git, shells) and killing only the root would
                // leave those reparented and running.
                var (killOutcome, killFailure) = await ForceKillAsync(observer).ConfigureAwait(false);
                outcome = killOutcome;
                failure ??= killFailure;
            }
        }
        catch (Exception ex)
        {
            failure ??= ex;
            outcome = PtyCloseOutcome.Faulted;
        }
        finally
        {
            entry.Observer?.Dispose();
            entry.Observer = null;
        }

        stopwatch.Stop();

        // Everything from here on is bookkeeping and notification, and it runs inside a try/finally whose
        // finally *always* completes CloseCompletion. That is not decoration: this task's result is what
        // every joining caller (race 1 and race 2) and CloseAllAsync await, so an exception escaping this
        // tail would hang them forever rather than merely losing a result.
        var result = PtyCloseResult.NotFound(entry.TabId);
        try
        {
            var exitCode = entry.Session.ExitTask.IsCompletedSuccessfully ? entry.Session.ExitTask.Result : null;
            var reason = entry.Session.ExitReason;
            result = new PtyCloseResult(
                entry.TabId,
                outcome,
                entry.ProcessId,
                exitCode,
                reason,
                stopwatch.Elapsed,
                failure);

            // Leave the closing map before anything user-visible runs, so a subscriber that immediately
            // re-registers the same tabId (or closes it again) sees a fully settled registry.
            _closing.TryRemove(new KeyValuePair<string, Entry>(entry.TabId, entry));

            if (entry.TryMarkEnded())
            {
                try
                {
                    SessionEnded?.Invoke(this, new PtySessionEndedEventArgs(entry.TabId, reason, exitCode, outcome));
                }
                catch
                {
                    // A throwing subscriber must not turn into a failed close (or a lost result for whoever
                    // is awaiting one).
                }
            }
        }
        finally
        {
            _closing.TryRemove(new KeyValuePair<string, Entry>(entry.TabId, entry));
            entry.CloseCompletion.TrySetResult(result);
        }

        return result;
    }

    private static async Task<bool> WaitForProcessGoneAsync(
        IPtyProcessObserver observer,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (observer.HasExited)
        {
            return true;
        }

        if (timeout <= TimeSpan.Zero)
        {
            return false;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await observer.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Timed out or the caller cancelled: re-read rather than assuming, since the process may have
            // exited in the same instant.
            return observer.HasExited;
        }
        catch (Exception)
        {
            // A broken observer is treated as "cannot confirm", which escalates to force-kill - the safe
            // direction for a class whose job is to not leak processes.
            return false;
        }
    }

    private static async Task WaitForSessionExitAsync(PtySession session, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : timeout);
            await session.ExitTask.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // ExitTask never faults; this only ever swallows the timeout/cancellation.
        }
    }

    private async Task<(PtyCloseOutcome Outcome, Exception? Failure)> ForceKillAsync(IPtyProcessObserver observer)
    {
        try
        {
            observer.KillTree();
        }
        catch (Exception) when (observer.HasExited)
        {
            // Classic benign race: the child exited between the timeout check and the kill, so Kill threw
            // "process has exited". That is a successful close, not a failure.
            return (PtyCloseOutcome.Closed, null);
        }
        catch (Exception ex)
        {
            return (PtyCloseOutcome.ForceKillFailed, ex);
        }

        var gone = await WaitForProcessGoneAsync(observer, _options.ForceKillGrace, CancellationToken.None)
            .ConfigureAwait(false);
        return (gone ? PtyCloseOutcome.ForceKilled : PtyCloseOutcome.ForceKillFailed, null);
    }

    private IPtyProcessObserver? SafeOpenObserver(PtySession session)
    {
        try
        {
            return _observerFactory(session);
        }
        catch (Exception)
        {
            // A factory (including a test double) throwing must not fail a registration; it just means no
            // force-kill capability for that tab.
            return null;
        }
    }

    /// <summary>
    /// One registration plus its teardown state. The two interlocked gates are the whole concurrency
    /// story: <see cref="TryBeginClose"/> decides who disposes, <see cref="TryMarkEnded"/> decides who
    /// raises <see cref="PtyRegistry.SessionEnded"/> - each exactly once, for the life of the entry.
    /// </summary>
    private sealed class Entry
    {
        private int _closeStarted;
        private int _endedRaised;

        internal Entry(PtyRegistry owner, string tabId, PtySession session)
        {
            Owner = owner;
            TabId = tabId;
            Session = session;
            ProcessId = TryReadProcessId(session);
        }

        /// <summary>The registry that owns this entry, and the only thing allowed to tear it down.</summary>
        internal PtyRegistry Owner { get; }

        internal string TabId { get; }

        internal PtySession Session { get; }

        /// <summary>Captured at registration: <see cref="PtySession.ProcessId"/> is cheap and stable, but
        /// reading it after teardown is not something to depend on.</summary>
        internal int? ProcessId { get; }

        /// <summary>Set once, immediately after construction, and cleared when the close finishes.</summary>
        internal IPtyProcessObserver? Observer { get; set; }

        /// <summary>Completed exactly once, by whoever won <see cref="TryBeginClose"/>. Continuations run
        /// asynchronously so a close cannot be resumed on the teardown thread.</summary>
        internal TaskCompletionSource<PtyCloseResult> CloseCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool TryBeginClose() => Interlocked.Exchange(ref _closeStarted, 1) == 0;

        internal bool TryMarkEnded() => Interlocked.Exchange(ref _endedRaised, 1) == 0;

        private static int? TryReadProcessId(PtySession session)
        {
            try
            {
                return session.ProcessId;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Production <see cref="IPtyProcessObserver"/>: a <see cref="Process"/> handle this class
    /// owns for the whole registration (which is also what pins the PID - see the class remarks).</summary>
    private sealed class ProcessObserver : IPtyProcessObserver
    {
        private readonly Process _process;

        internal ProcessObserver(Process process)
        {
            _process = process;
            ProcessId = process.Id;
        }

        public int ProcessId { get; }

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (Exception)
                {
                    // Unreadable state (handle closed under us): report "not known to be gone" so the
                    // caller escalates rather than assuming success.
                    return false;
                }
            }
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _process.WaitForExitAsync(cancellationToken);

        public void KillTree() => _process.Kill(entireProcessTree: true);

        public void Dispose() => _process.Dispose();
    }
}

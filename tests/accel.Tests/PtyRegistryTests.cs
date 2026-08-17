namespace Accel.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Accel.Orchestration;
using Xunit;

/// <summary>
/// P3-T2 unit tests for <see cref="PtyRegistry"/>.
///
/// <para><b>What is tested here vs in the dev verb.</b> Everything deterministic lives here: the lookup /
/// enumeration / ownership rules, the unknown-tab no-op, self-exit auto-removal, and - via the
/// <see cref="IPtyProcessObserver"/> seam - the whole timeout → force-kill escalation, including that
/// <c>KillTree</c> happens at most once no matter how many callers close the same tab. What genuinely
/// cannot be established with fakes (N real children torn down simultaneously from N threads, handle and
/// process-leak accounting) lives in the <c>pty-registry-stress-test</c> verb
/// (<see cref="PtyRegistryStressTest"/>), following the same split as <c>pty-session-smoke-test</c>.</para>
///
/// <para>Real sessions here are <c>cmd.exe</c>, never <c>claude.exe</c> - same precedent as
/// <c>ConPtyTests</c>/<c>CreateSessionDialogViewModelTests</c>: a controllable child with known exit codes
/// and no side effects on the user's real sessions.</para>
/// </summary>
public class PtyRegistryTests
{
    private static string CmdPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    /// <summary>An interactive <c>cmd.exe</c>: only ever goes away because the registry tore it down.</summary>
    private static PtySession StartIdleSession() => PtySession.Start(
        new PtyLaunchSpec { ExecutablePath = CmdPath(), WorkingDirectory = Path.GetTempPath() },
        new PtySessionOptions { OutputChannelCapacity = 8, ReadBufferSize = 512 });

    private static PtySession StartSelfExitingSession(int exitCode) => PtySession.Start(
        new PtyLaunchSpec
        {
            ExecutablePath = CmdPath(),
            Arguments = new[] { "/c", "exit", exitCode.ToString() },
            WorkingDirectory = Path.GetTempPath(),
        },
        new PtySessionOptions { OutputChannelCapacity = 8, ReadBufferSize = 512 });

    private static string NewTabId() => Guid.NewGuid().ToString("N");

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return condition();
    }

    // ---------------------------------------------------------------- lookup / enumeration / registration

    [Fact]
    public async Task CloseAsync_UnknownTabId_IsASafeNoOp()
    {
        using var registry = new PtyRegistry();

        var result = await registry.CloseAsync(NewTabId());

        Assert.Equal(PtyCloseOutcome.NotFound, result.Outcome);
        Assert.False(result.ChildIsGone);
        Assert.Null(result.Failure);

        // Null/empty are no-ops too, not argument exceptions: an app-shutdown sweep or a stale UI command
        // must never be able to throw out of a close.
        Assert.Equal(PtyCloseOutcome.NotFound, (await registry.CloseAsync(string.Empty)).Outcome);
        Assert.Equal(PtyCloseOutcome.NotFound, (await registry.CloseAsync(null!)).Outcome);
    }

    [Fact]
    public async Task CloseAllAsync_OnAnEmptyRegistry_ReturnsNothingAndDoesNotThrow()
    {
        using var registry = new PtyRegistry();
        var results = await registry.CloseAllAsync();
        Assert.Empty(results);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Register_RejectsNullsEmptyTabIdsAndDuplicates()
    {
        using var registry = new PtyRegistry();
        var session = StartIdleSession();

        Assert.Throws<ArgumentException>(() => registry.Register(string.Empty, session));
        Assert.Throws<ArgumentNullException>(() => registry.Register(NewTabId(), null!));

        var tabId = NewTabId();
        registry.Register(tabId, session); // from here the registry owns `session`, not this test

        // A duplicate must NOT silently replace: replacing would drop the only reference to a live session
        // the registry is responsible for disposing, i.e. leak a process.
        var second = StartIdleSession();
        try
        {
            Assert.Throws<ArgumentException>(() => registry.Register(tabId, second));
            Assert.Equal(1, registry.Count);
        }
        finally
        {
            // `second` was never accepted, so this test still owns it - the one legitimate case for
            // disposing a PtySession outside the registry.
            second.Dispose();
        }
    }

    [Fact]
    public async Task Snapshot_TabIds_TryGet_AndCount_ReflectRegistrations()
    {
        using var registry = new PtyRegistry();
        var tabA = NewTabId();
        var tabB = NewTabId();
        var sessionA = StartIdleSession();
        var sessionB = StartIdleSession();
        registry.Register(tabA, sessionA);
        registry.Register(tabB, sessionB);

        Assert.Equal(2, registry.Count);
        Assert.True(registry.TryGet(tabA, out var found));
        Assert.Same(sessionA, found);
        Assert.False(registry.TryGet(NewTabId(), out var missing));
        Assert.Null(missing);
        Assert.False(registry.TryGet(string.Empty, out _));

        var snapshot = registry.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(new[] { tabA, tabB }.OrderBy(x => x, StringComparer.Ordinal), snapshot.Select(s => s.TabId).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Contains(snapshot, s => ReferenceEquals(s.Session, sessionB));
        Assert.Equal(2, registry.TabIds().Count);

        // The snapshot is a materialised copy: closing a tab afterwards must not mutate it (that is what
        // makes it safe for an app-exit sweep to iterate while closing).
        await registry.CloseAsync(tabA);
        Assert.Equal(2, snapshot.Count);
        Assert.Single(registry.Snapshot());
        Assert.False(registry.TryGet(tabA, out _));
    }

    // ---------------------------------------------------------------------------- close = the only dispose

    [Fact]
    public async Task CloseAsync_DisposesTheSession_RemovesIt_AndRaisesSessionEndedOnceAsTornDown()
    {
        using var registry = new PtyRegistry();
        var ended = new List<PtySessionEndedEventArgs>();
        registry.SessionEnded += (_, e) =>
        {
            lock (ended)
            {
                ended.Add(e);
            }
        };

        var tabId = NewTabId();
        var session = StartIdleSession();
        registry.Register(tabId, session);

        var result = await registry.CloseAsync(tabId);

        Assert.True(result.ChildIsGone, $"outcome was {result.Outcome} ({result.Failure})");
        Assert.Equal(tabId, result.TabId);
        Assert.Equal(session.ProcessId, result.ProcessId);
        Assert.Equal(PtySessionExitReason.TornDown, result.Reason);
        Assert.False(registry.TryGet(tabId, out _));
        Assert.Equal(0, registry.Count);

        // The session really was disposed - PumpThreadFinished is PtySession's own per-session proof.
        Assert.True(session.PumpThreadFinished);
        Assert.True(session.ExitTask.IsCompleted);

        Assert.True(WaitFor(() => ended.Count > 0, TimeSpan.FromSeconds(5)));
        Assert.Single(ended);
        Assert.Equal(tabId, ended[0].TabId);
        Assert.Equal(PtySessionExitReason.TornDown, ended[0].Reason);
    }

    [Fact]
    public async Task CloseAsync_Twice_Sequentially_SecondIsNotFound()
    {
        using var registry = new PtyRegistry();
        var tabId = NewTabId();
        registry.Register(tabId, StartIdleSession());

        var first = await registry.CloseAsync(tabId);
        var second = await registry.CloseAsync(tabId);

        Assert.True(first.ChildIsGone);
        Assert.Equal(PtyCloseOutcome.NotFound, second.Outcome);
    }

    /// <summary>
    /// Race (b): two closes of the same tab, concurrently. The second must join the first - one dispose, one
    /// result - not crash and not run a second teardown. The fake observer makes the teardown long enough
    /// (its wait never completes until the kill) for the two calls to genuinely overlap.
    /// </summary>
    [Fact]
    public async Task TwoConcurrentClosesOfTheSameTab_ShareOneTeardownAndOneKill()
    {
        var options = new PtyRegistryOptions
        {
            CloseTimeout = TimeSpan.FromMilliseconds(250),
            ForceKillGrace = TimeSpan.FromSeconds(2),
            ProcessObserverFactory = _ => new FakeObserver(diesOnKill: true),
        };

        using var registry = new PtyRegistry(options);
        var endedCount = 0;
        registry.SessionEnded += (_, _) => Interlocked.Increment(ref endedCount);

        var tabId = NewTabId();
        var session = StartIdleSession();
        registry.Register(tabId, session);
        var observer = FakeObserver.Last!;

        var barrier = new ManualResetEventSlim(false);
        var closes = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            barrier.Wait();
            return await registry.CloseAsync(tabId);
        })).ToArray();
        barrier.Set();

        var results = await Task.WhenAll(closes);

        // Exactly one teardown: every caller either got the identical result instance or a NotFound (which
        // is what a caller that arrived after the close had already finished sees).
        var distinct = results.Distinct().ToList();
        var realResults = distinct.Where(r => r.Outcome != PtyCloseOutcome.NotFound).ToList();
        Assert.Single(realResults);
        Assert.Equal(PtyCloseOutcome.ForceKilled, realResults[0].Outcome);

        // The load-bearing assertion of this test: one kill, not six.
        Assert.Equal(1, observer.KillCount);
        Assert.Equal(1, observer.DisposeCount);
        Assert.Equal(0, registry.Count);
        Assert.True(WaitFor(() => Volatile.Read(ref endedCount) == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref endedCount));
        Assert.True(session.PumpThreadFinished);
    }

    // ------------------------------------------------------------------------------- self-exit auto-removal

    /// <summary>
    /// Requirement 4: a child that ends on its own leaves the registry by itself and is surfaced through
    /// <see cref="PtyRegistry.SessionEnded"/> with <see cref="PtySessionExitReason.ChildExited"/> - the
    /// classification coming from <see cref="PtySession.ExitReason"/> rather than being re-derived here.
    /// </summary>
    [Fact]
    public void SelfExitingChild_IsAutoRemovedAndReportedAsChildExited()
    {
        using var registry = new PtyRegistry();
        var ended = new BlockingCollection<PtySessionEndedEventArgs>();
        registry.SessionEnded += (_, e) => ended.Add(e);

        var tabId = NewTabId();
        registry.Register(tabId, StartSelfExitingSession(4));

        Assert.True(ended.TryTake(out var args, TimeSpan.FromSeconds(30)), "SessionEnded never fired for a self-exiting child");
        Assert.Equal(tabId, args!.TabId);
        Assert.Equal(PtySessionExitReason.ChildExited, args.Reason);
        Assert.Equal(4, args.ExitCode);

        Assert.True(WaitFor(() => registry.Count == 0, TimeSpan.FromSeconds(5)));
        Assert.False(registry.TryGet(tabId, out _));
    }

    /// <summary>
    /// Race (a): a session that has <i>already</i> exited when it is registered. The exit continuation must
    /// still fire (no lost-notification window), and a close arriving at the same time must not
    /// double-dispose or throw.
    /// </summary>
    [Fact]
    public async Task RegisteringAnAlreadyExitedSession_StillEndsExactlyOnce_EvenWithAConcurrentClose()
    {
        using var registry = new PtyRegistry();
        var endedCount = 0;
        registry.SessionEnded += (_, _) => Interlocked.Increment(ref endedCount);

        var session = StartSelfExitingSession(3);
        await session.ExitTask.WaitAsync(TimeSpan.FromSeconds(30));

        var tabId = NewTabId();
        registry.Register(tabId, session);

        // Race the self-exit continuation with an explicit close.
        var result = await registry.CloseAsync(tabId);
        Assert.True(result.Outcome is PtyCloseOutcome.Closed or PtyCloseOutcome.ExitUnverified or PtyCloseOutcome.NotFound, $"unexpected outcome {result.Outcome}");

        Assert.True(WaitFor(() => Volatile.Read(ref endedCount) == 1, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, Volatile.Read(ref endedCount));
        Assert.Equal(0, registry.Count);
        Assert.True(session.PumpThreadFinished);
    }

    // -------------------------------------------------------------- timeout / force-kill escalation (fakes)

    [Fact]
    public async Task WhenTheChildOutlivesTheCloseTimeout_TheProcessTreeIsForceKilled()
    {
        using var registry = new PtyRegistry(NeverExitsOptions(out var observers));
        var tabId = NewTabId();
        registry.Register(tabId, StartIdleSession());

        var result = await registry.CloseAsync(tabId);

        var observer = Assert.Single(observers);
        Assert.Equal(PtyCloseOutcome.ForceKilled, result.Outcome);
        Assert.True(result.ChildIsGone);
        Assert.Equal(1, observer.KillCount);
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task WhenTheForceKillItselfFails_TheOutcomeIsForceKillFailedWithTheException()
    {
        var boom = new InvalidOperationException("kill refused");
        using var registry = new PtyRegistry(new PtyRegistryOptions
        {
            CloseTimeout = TimeSpan.FromMilliseconds(100),
            ForceKillGrace = TimeSpan.FromMilliseconds(100),
            ProcessObserverFactory = _ => new FakeObserver(diesOnKill: false) { KillFailure = boom },
        });

        var tabId = NewTabId();
        registry.Register(tabId, StartIdleSession());

        var result = await registry.CloseAsync(tabId);

        Assert.Equal(PtyCloseOutcome.ForceKillFailed, result.Outcome);
        Assert.False(result.ChildIsGone);
        Assert.Same(boom, result.Failure);
        Assert.Equal(0, registry.Count); // still removed - never resurrected
    }

    [Fact]
    public async Task WhenTheKillSucceedsButTheProcessSurvivesTheGrace_TheOutcomeIsForceKillFailed()
    {
        using var registry = new PtyRegistry(new PtyRegistryOptions
        {
            CloseTimeout = TimeSpan.FromMilliseconds(100),
            ForceKillGrace = TimeSpan.FromMilliseconds(100),
            ProcessObserverFactory = _ => new FakeObserver(diesOnKill: false),
        });

        var tabId = NewTabId();
        registry.Register(tabId, StartIdleSession());

        var result = await registry.CloseAsync(tabId);

        Assert.Equal(PtyCloseOutcome.ForceKillFailed, result.Outcome);
        Assert.Equal(1, FakeObserver.Last!.KillCount);
    }

    /// <summary>
    /// No trusted handle for the child (the production factory's own answer whenever PID identity cannot be
    /// proven) must never turn into "kill whatever is at that PID". The close reports
    /// <see cref="PtyCloseOutcome.ExitUnverified"/> instead.
    /// </summary>
    [Fact]
    public async Task WithoutATrustedProcessHandle_NothingIsKilledAndTheOutcomeIsExitUnverified()
    {
        using var registry = new PtyRegistry(new PtyRegistryOptions
        {
            CloseTimeout = TimeSpan.FromSeconds(2),
            ProcessObserverFactory = _ => null,
        });

        var tabId = NewTabId();
        var session = StartIdleSession();
        registry.Register(tabId, session);

        var result = await registry.CloseAsync(tabId);

        Assert.Equal(PtyCloseOutcome.ExitUnverified, result.Outcome);
        Assert.Null(result.Failure);
        Assert.Equal(0, registry.Count);
        Assert.True(session.PumpThreadFinished); // the session was still disposed
    }

    [Fact]
    public async Task AnObserverFactoryThatThrows_DoesNotFailTheRegistration()
    {
        using var registry = new PtyRegistry(new PtyRegistryOptions
        {
            CloseTimeout = TimeSpan.FromSeconds(2),
            ProcessObserverFactory = _ => throw new InvalidOperationException("no handle for you"),
        });

        var tabId = NewTabId();
        registry.Register(tabId, StartIdleSession());
        Assert.Equal(1, registry.Count);

        var result = await registry.CloseAsync(tabId);
        Assert.Equal(PtyCloseOutcome.ExitUnverified, result.Outcome);
    }

    // ------------------------------------------------------------------------------------- bulk / lifecycle

    [Fact]
    public async Task CloseAllAsync_ClosesEverySession_AndOneFailureDoesNotStopTheOthers()
    {
        // The first session gets an observer whose kill throws (a "failing" session); the rest are normal.
        var created = 0;
        using var registry = new PtyRegistry(new PtyRegistryOptions
        {
            CloseTimeout = TimeSpan.FromMilliseconds(100),
            ForceKillGrace = TimeSpan.FromMilliseconds(100),
            ProcessObserverFactory = session => Interlocked.Increment(ref created) == 1
                ? new FakeObserver(diesOnKill: false) { KillFailure = new InvalidOperationException("boom") }
                : PtyRegistry.OpenProcessObserver(session),
        });

        var tabIds = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var tabId = NewTabId();
            tabIds.Add(tabId);
            registry.Register(tabId, StartIdleSession());
        }

        var results = await registry.CloseAllAsync();

        Assert.Equal(4, results.Count);
        Assert.Equal(0, registry.Count);
        Assert.Single(results, r => r.Outcome == PtyCloseOutcome.ForceKillFailed);
        Assert.Equal(3, results.Count(r => r.Outcome != PtyCloseOutcome.ForceKillFailed));
        Assert.All(results, r => Assert.Contains(r.TabId, tabIds));
    }

    [Fact]
    public async Task CloseAllAsync_RunsClosesConcurrently_NotSerially()
    {
        // Four sessions whose observers never report an exit: serially this would cost 4 x CloseTimeout.
        using var registry = new PtyRegistry(new PtyRegistryOptions
        {
            CloseTimeout = TimeSpan.FromMilliseconds(700),
            ForceKillGrace = TimeSpan.FromSeconds(2),
            ProcessObserverFactory = _ => new FakeObserver(diesOnKill: true),
        });

        for (var i = 0; i < 4; i++)
        {
            registry.Register(NewTabId(), StartIdleSession());
        }

        var stopwatch = Stopwatch.StartNew();
        var results = await registry.CloseAllAsync();
        stopwatch.Stop();

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Equal(PtyCloseOutcome.ForceKilled, r.Outcome));
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(700 * 3),
            $"CloseAllAsync took {stopwatch.ElapsedMilliseconds} ms for 4 x 700 ms timeouts, which looks serial");
    }

    [Fact]
    public void Dispose_ClosesEverything_IsIdempotent_AndThenRefusesRegistrations()
    {
        var registry = new PtyRegistry();
        var sessions = new List<PtySession>();
        for (var i = 0; i < 3; i++)
        {
            var session = StartIdleSession();
            sessions.Add(session);
            registry.Register(NewTabId(), session);
        }

        registry.Dispose();
        registry.Dispose(); // idempotent, on purpose

        Assert.Equal(0, registry.Count);
        Assert.True(registry.IsDisposed);
        Assert.All(sessions, s => Assert.True(s.PumpThreadFinished));
        Assert.All(sessions, s => Assert.True(s.ExitTask.IsCompleted));

        using var late = StartIdleSession();
        Assert.Throws<ObjectDisposedException>(() => registry.Register(NewTabId(), late));
    }

    /// <summary>
    /// The PID-reuse guard's input: <see cref="PtySession.ProcessStartTimeUtc"/> must actually be populated
    /// for a real session, otherwise the guard silently degrades to "accept any handle".
    /// </summary>
    [Fact]
    public void PtySession_RecordsTheChildProcessStartTime()
    {
        using var session = StartIdleSession();
        var startTime = session.ProcessStartTimeUtc;

        Assert.NotNull(startTime);
        using var independent = Process.GetProcessById(session.ProcessId);
        Assert.True(
            (independent.StartTime.ToUniversalTime() - startTime!.Value).Duration() < TimeSpan.FromSeconds(2),
            $"recorded {startTime} but the OS reports {independent.StartTime.ToUniversalTime()}");

        // Survives disposal (it is a captured value, which is what makes it usable during teardown).
        session.Dispose();
        Assert.Equal(startTime, session.ProcessStartTimeUtc);
    }

    private static PtyRegistryOptions NeverExitsOptions(out IReadOnlyList<FakeObserver> observers)
    {
        var created = new List<FakeObserver>();
        observers = created;
        return new PtyRegistryOptions
        {
            CloseTimeout = TimeSpan.FromMilliseconds(150),
            ForceKillGrace = TimeSpan.FromSeconds(2),
            ProcessObserverFactory = _ =>
            {
                var observer = new FakeObserver(diesOnKill: true);
                lock (created)
                {
                    created.Add(observer);
                }

                return observer;
            },
        };
    }

    /// <summary>
    /// A child process that never exits until it is killed - the shape that forces the escalation branch.
    /// Substituting this for the real <c>Process</c> is what makes the force-kill path deterministic
    /// instead of dependent on producing a genuinely wedged <c>cmd.exe</c>.
    /// </summary>
    private sealed class FakeObserver : IPtyProcessObserver
    {
        private readonly bool _diesOnKill;
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _killCount;
        private int _disposeCount;

        internal FakeObserver(bool diesOnKill)
        {
            _diesOnKill = diesOnKill;
            Last = this;
        }

        /// <summary>The most recently constructed instance, for tests that build the factory inline.</summary>
        internal static FakeObserver? Last { get; private set; }

        internal Exception? KillFailure { get; init; }

        internal int KillCount => Volatile.Read(ref _killCount);

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public int ProcessId => 4242;

        public bool HasExited => _exited.Task.IsCompleted;

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _exited.Task.WaitAsync(cancellationToken);

        public void KillTree()
        {
            Interlocked.Increment(ref _killCount);
            if (KillFailure is not null)
            {
                throw KillFailure;
            }

            if (_diesOnKill)
            {
                _exited.TrySetResult();
            }
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}

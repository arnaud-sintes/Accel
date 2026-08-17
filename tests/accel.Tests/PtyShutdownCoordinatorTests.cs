using Accel.Orchestration;
using Xunit;

namespace Accel.Tests;

/// <summary>
/// P3-T4 (Half A): unit coverage for <see cref="PtyShutdownCoordinator"/> - the parts that are testable without a
/// process actually exiting: the bounded-timeout behaviour, the try/finally guarantees (a throwing target or a
/// throwing subscriber must not skip cleanup or lose a result), the single-teardown convergence of the three exit
/// paths, and the install/uninstall bookkeeping.
///
/// <para>The seam is <see cref="IPtyShutdownTarget"/>, which exists precisely so a slow/hanging/throwing registry
/// can be simulated - a genuinely wedged <see cref="PtyRegistry"/> is not something a test can conjure. The real
/// end-to-end proof (real children, real console-control callback, a real
/// <c>AppDomain.ProcessExit</c> in a re-invoked process) is the <c>pty-shutdown-orphan-test</c> diagnostic verb.</para>
///
/// <para>Every coordinator here is constructed with all three handler installations <b>off</b> unless the test is
/// specifically about installation, and those tests use the <see cref="PtyShutdownOptions.ConsoleCtrlHandlerInstaller"/>
/// seam: a unit test must not leave process-wide console-control handlers behind in the test runner.</para>
/// </summary>
public class PtyShutdownCoordinatorTests
{
    private static PtyShutdownOptions NoHandlers(TimeSpan? graceful = null, TimeSpan? consoleCtrl = null) => new()
    {
        InstallConsoleCtrlHandler = false,
        InstallProcessExit = false,
        InstallCancelKeyPress = false,
        GracefulTimeout = graceful ?? TimeSpan.FromSeconds(5),
        ConsoleCtrlTimeout = consoleCtrl ?? TimeSpan.FromSeconds(5),
    };

    /// <summary>A target that records its calls and can be made slow, hanging, or throwing.</summary>
    private sealed class FakeTarget : IPtyShutdownTarget
    {
        private readonly TimeSpan _delay;
        private readonly Exception? _throw;
        private readonly int _closed;

        public FakeTarget(int closed = 3, TimeSpan? delay = null, Exception? throws = null)
        {
            _closed = closed;
            _delay = delay ?? TimeSpan.Zero;
            _throw = throws;
        }

        public int Calls;

        public bool ObservedCancellation;

        public async Task<int> CloseAllAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);

            if (_throw is not null)
            {
                throw _throw;
            }

            if (_delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    ObservedCancellation = true;
                    throw;
                }
            }

            return _closed;
        }
    }

    // --- the happy path ------------------------------------------------------------------------------------

    [Fact]
    public void Shutdown_ClosesEverything_AndReportsCompleted()
    {
        var target = new FakeTarget(closed: 4);
        using var coordinator = new PtyShutdownCoordinator(target, NoHandlers());

        var result = coordinator.Shutdown();

        Assert.Equal(PtyShutdownOutcome.Completed, result.Outcome);
        Assert.Equal(PtyShutdownTrigger.Explicit, result.Trigger);
        Assert.Equal(4, result.SessionsClosed);
        Assert.Null(result.Failure);
        Assert.Equal(1, target.Calls);
        Assert.True(coordinator.HasShutDown);
        Assert.Same(result, coordinator.LastResult);
    }

    [Fact]
    public void Shutdown_NoSessions_IsStillACompletedShutdown()
    {
        var target = new FakeTarget(closed: 0);
        using var coordinator = new PtyShutdownCoordinator(target, NoHandlers());

        var result = coordinator.Shutdown();

        Assert.Equal(PtyShutdownOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.SessionsClosed);
    }

    // --- bounded timeout ----------------------------------------------------------------------------------

    [Fact]
    public void Shutdown_HangingTarget_TimesOutWithinTheBudget_RatherThanBlockingTheExit()
    {
        // The property that matters: a hung shutdown handler must not be able to hold the process open.
        var target = new FakeTarget(delay: TimeSpan.FromMinutes(5));
        using var coordinator = new PtyShutdownCoordinator(
            target,
            NoHandlers(graceful: TimeSpan.FromMilliseconds(250)));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = coordinator.Shutdown();
        stopwatch.Stop();

        Assert.Equal(PtyShutdownOutcome.TimedOut, result.Outcome);
        Assert.Equal(0, result.SessionsClosed);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void Shutdown_Timeout_CancelsTheTarget_RatherThanSilentlyAbandoningIt()
    {
        // Cancellation is how PtyRegistry is told to bring its force-kill forward, so the token must actually be
        // signalled on timeout - abandoning a half-torn-down child is the orphan this whole task prevents.
        var target = new FakeTarget(delay: TimeSpan.FromSeconds(30));
        using var coordinator = new PtyShutdownCoordinator(
            target,
            NoHandlers(graceful: TimeSpan.FromMilliseconds(200)));

        coordinator.Shutdown();

        var observed = SpinUntil(() => target.ObservedCancellation, TimeSpan.FromSeconds(10));
        Assert.True(observed, "the target never observed cancellation");
    }

    [Fact]
    public void Shutdown_ConsoleCtrlPath_UsesTheShorterConsoleCtrlBudget()
    {
        // Windows kills the process a few seconds into a CTRL_CLOSE_EVENT handler, so this path deliberately gets
        // a tighter budget than the explicit one.
        var target = new FakeTarget(delay: TimeSpan.FromMinutes(5));
        using var coordinator = new PtyShutdownCoordinator(
            target,
            NoHandlers(graceful: TimeSpan.FromMinutes(5), consoleCtrl: TimeSpan.FromMilliseconds(250)));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = coordinator.Shutdown(PtyShutdownTrigger.ConsoleCtrl);
        stopwatch.Stop();

        Assert.Equal(PtyShutdownOutcome.TimedOut, result.Outcome);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void Shutdown_ZeroBudget_StillTerminates()
    {
        var target = new FakeTarget(delay: TimeSpan.FromSeconds(30));
        using var coordinator = new PtyShutdownCoordinator(target, NoHandlers(graceful: TimeSpan.Zero));

        var result = coordinator.Shutdown();

        Assert.Equal(PtyShutdownOutcome.TimedOut, result.Outcome);
    }

    // --- try/finally guarantees ---------------------------------------------------------------------------

    [Fact]
    public void Shutdown_ThrowingTarget_ReportsFaulted_WithoutThrowing_AndStillCompletesTheAttempt()
    {
        var failure = new InvalidOperationException("teardown exploded");
        var target = new FakeTarget(throws: failure);
        using var coordinator = new PtyShutdownCoordinator(target, NoHandlers());

        var result = coordinator.Shutdown();

        Assert.Equal(PtyShutdownOutcome.Faulted, result.Outcome);
        Assert.Same(failure, result.Failure);
        Assert.True(coordinator.HasShutDown);
        Assert.NotNull(coordinator.LastResult);
    }

    [Fact]
    public void Shutdown_ThrowingTarget_StillReleasesASecondCaller_RatherThanLeavingItBlocked()
    {
        // The finally is what guarantees this: if the failure path skipped completing the gate, a concurrent
        // Ctrl+C would block for its whole budget on a teardown that had already given up.
        var target = new FakeTarget(throws: new InvalidOperationException("boom"));
        using var coordinator = new PtyShutdownCoordinator(
            target,
            NoHandlers(graceful: TimeSpan.FromMinutes(5), consoleCtrl: TimeSpan.FromMinutes(5)));

        coordinator.Shutdown();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var second = coordinator.Shutdown(PtyShutdownTrigger.ConsoleCtrl);
        stopwatch.Stop();

        Assert.Equal(PtyShutdownOutcome.AlreadyShutDown, second.Outcome);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"the second caller waited {stopwatch.Elapsed}");
    }

    [Fact]
    public void Shutdown_ThrowingSubscriber_DoesNotLoseTheResult()
    {
        var target = new FakeTarget(closed: 2);
        using var coordinator = new PtyShutdownCoordinator(target, NoHandlers());
        coordinator.ShutdownCompleted += (_, _) => throw new InvalidOperationException("bad subscriber");

        var result = coordinator.Shutdown();

        Assert.Equal(PtyShutdownOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.SessionsClosed);
    }

    [Fact]
    public void Shutdown_RaisesShutdownCompletedExactlyOnce()
    {
        var target = new FakeTarget();
        using var coordinator = new PtyShutdownCoordinator(target, NoHandlers());
        var raised = new List<PtyShutdownResult>();
        coordinator.ShutdownCompleted += (_, result) => raised.Add(result);

        coordinator.Shutdown();
        coordinator.Shutdown(PtyShutdownTrigger.ProcessExit);
        coordinator.HandleConsoleCtrlEvent(0);

        Assert.Single(raised);
        Assert.Equal(PtyShutdownTrigger.Explicit, raised[0].Trigger);
    }

    // --- the three exit paths converge on ONE teardown ----------------------------------------------------

    [Theory]
    [InlineData(PtyShutdownTrigger.Explicit)]
    [InlineData(PtyShutdownTrigger.ConsoleCtrl)]
    [InlineData(PtyShutdownTrigger.ProcessExit)]
    public void EachExitPath_OnItsOwn_RunsTheTeardown(PtyShutdownTrigger trigger)
    {
        var target = new FakeTarget();
        using var coordinator = new PtyShutdownCoordinator(target, NoHandlers());

        var result = coordinator.Shutdown(trigger);

        Assert.Equal(PtyShutdownOutcome.Completed, result.Outcome);
        Assert.Equal(trigger, result.Trigger);
        Assert.Equal(1, target.Calls);
    }

    [Fact]
    public void AllThreeExitPaths_TogetherStillProduceExactlyOneTeardown()
    {
        var target = new FakeTarget();
        using var coordinator = new PtyShutdownCoordinator(target, NoHandlers());

        coordinator.HandleConsoleCtrlEvent(2); // CTRL_CLOSE_EVENT
        coordinator.OnProcessExit();
        var explicitResult = coordinator.Shutdown();

        Assert.Equal(1, target.Calls);
        Assert.Equal(PtyShutdownOutcome.AlreadyShutDown, explicitResult.Outcome);
        Assert.Equal(PtyShutdownTrigger.ConsoleCtrl, coordinator.LastResult!.Trigger);
    }

    [Fact]
    public async Task ConcurrentTriggers_RunTheTeardownOnce()
    {
        var target = new FakeTarget(delay: TimeSpan.FromMilliseconds(100));
        using var coordinator = new PtyShutdownCoordinator(target, NoHandlers());
        using var gate = new ManualResetEventSlim(false);

        var racers = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            gate.Wait();
            return (i % 3) switch
            {
                0 => coordinator.Shutdown(),
                1 => coordinator.Shutdown(PtyShutdownTrigger.ProcessExit),
                _ => coordinator.Shutdown(PtyShutdownTrigger.ConsoleCtrl),
            };
        })).ToArray();

        gate.Set();
        var all = Task.WhenAll(racers);
        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(all, finished);

        var outcomes = (await all).Select(r => r.Outcome).ToArray();
        Assert.Equal(1, target.Calls);
        Assert.Equal(7, outcomes.Count(o => o == PtyShutdownOutcome.AlreadyShutDown));
        Assert.Equal(1, outcomes.Count(o => o == PtyShutdownOutcome.Completed));
    }

    // --- the native console-control callback body ---------------------------------------------------------

    [Theory]
    [InlineData(0u)] // CTRL_C_EVENT
    [InlineData(1u)] // CTRL_BREAK_EVENT
    [InlineData(2u)] // CTRL_CLOSE_EVENT - never surfaces as Console.CancelKeyPress, which is why the native
                     // handler exists at all
    [InlineData(5u)] // CTRL_LOGOFF_EVENT
    [InlineData(6u)] // CTRL_SHUTDOWN_EVENT
    public void HandleConsoleCtrlEvent_EveryHandledEvent_RunsTheTeardown_AndDoesNotSwallowIt(uint ctrlType)
    {
        var target = new FakeTarget();
        using var coordinator = new PtyShutdownCoordinator(target, NoHandlers());

        var handled = coordinator.HandleConsoleCtrlEvent(ctrlType);

        // False = "not handled", so the rest of the chain (e.g. RunCombinedAsync's existing Ctrl+C-closes-the-
        // window handler) still runs. This class adds cleanup; it never takes over the app's exit policy.
        Assert.False(handled);
        Assert.Equal(1, target.Calls);
        Assert.Equal(PtyShutdownTrigger.ConsoleCtrl, coordinator.LastResult!.Trigger);
    }

    [Fact]
    public void HandleConsoleCtrlEvent_UnknownEvent_IsIgnored()
    {
        var target = new FakeTarget();
        using var coordinator = new PtyShutdownCoordinator(target, NoHandlers());

        var handled = coordinator.HandleConsoleCtrlEvent(99);

        Assert.False(handled);
        Assert.Equal(0, target.Calls);
        Assert.False(coordinator.HasShutDown);
    }

    // --- install / uninstall bookkeeping ------------------------------------------------------------------

    [Fact]
    public void Install_RegistersTheNativeHandlerOnce_AndUninstallRemovesIt()
    {
        var calls = new List<bool>();
        var options = new PtyShutdownOptions
        {
            InstallProcessExit = false,
            InstallCancelKeyPress = false,
            ConsoleCtrlHandlerInstaller = (_, add) =>
            {
                calls.Add(add);
                return true;
            },
        };
        var coordinator = new PtyShutdownCoordinator(new FakeTarget(), options);

        coordinator.Install();
        coordinator.Install(); // idempotent
        Assert.True(coordinator.ConsoleCtrlHandlerInstalled);
        Assert.Equal(new[] { true }, calls);

        coordinator.Uninstall();
        coordinator.Uninstall(); // idempotent
        Assert.False(coordinator.ConsoleCtrlHandlerInstalled);
        Assert.Equal(new[] { true, false }, calls);

        coordinator.Dispose();
    }

    [Fact]
    public void Install_NativeRegistrationRejected_IsRecordedNotThrown()
    {
        // A process with no console legitimately fails this registration; the other two handlers must still work.
        var options = new PtyShutdownOptions
        {
            InstallProcessExit = false,
            InstallCancelKeyPress = false,
            ConsoleCtrlHandlerInstaller = (_, _) => false,
        };
        using var coordinator = new PtyShutdownCoordinator(new FakeTarget(), options);

        coordinator.Install();

        Assert.False(coordinator.ConsoleCtrlHandlerInstalled);
    }

    [Fact]
    public void Install_NativeRegistrationThrows_IsSwallowed()
    {
        var options = new PtyShutdownOptions
        {
            InstallProcessExit = false,
            InstallCancelKeyPress = false,
            ConsoleCtrlHandlerInstaller = (_, _) => throw new InvalidOperationException("no console"),
        };
        using var coordinator = new PtyShutdownCoordinator(new FakeTarget(), options);

        var ex = Record.Exception(() => coordinator.Install());

        Assert.Null(ex);
        Assert.False(coordinator.ConsoleCtrlHandlerInstalled);
    }

    [Fact]
    public void Install_ReturnsItself_SoItComposesAsAUsingStatement()
    {
        using var coordinator = new PtyShutdownCoordinator(new FakeTarget(), NoHandlers());

        Assert.Same(coordinator, coordinator.Install());
    }

    [Fact]
    public void Install_ProcessExitAndCancelKeyPress_AreSubscribedAndUnsubscribed()
    {
        var options = new PtyShutdownOptions
        {
            InstallConsoleCtrlHandler = false,
            InstallProcessExit = true,
            InstallCancelKeyPress = true,
        };
        var coordinator = new PtyShutdownCoordinator(new FakeTarget(), options);
        try
        {
            coordinator.Install();
            Assert.True(coordinator.ProcessExitHandlerInstalled);
            Assert.True(coordinator.CancelKeyPressHandlerInstalled);
        }
        finally
        {
            // Must unhook: a leaked ProcessExit handler would run this fake teardown when the test host exits.
            coordinator.Dispose();
        }

        Assert.False(coordinator.ProcessExitHandlerInstalled);
        Assert.False(coordinator.CancelKeyPressHandlerInstalled);
    }

    [Fact]
    public void Install_AfterDispose_DoesNothing()
    {
        var options = new PtyShutdownOptions
        {
            InstallConsoleCtrlHandler = false,
            InstallProcessExit = true,
            InstallCancelKeyPress = false,
        };
        var coordinator = new PtyShutdownCoordinator(new FakeTarget(), options);
        coordinator.Dispose();

        coordinator.Install();

        Assert.False(coordinator.ProcessExitHandlerInstalled);
    }

    // --- Dispose (the normal exit path) -------------------------------------------------------------------

    [Fact]
    public void Dispose_RunsTheTeardown_AndIsIdempotent()
    {
        var target = new FakeTarget(closed: 5);
        var coordinator = new PtyShutdownCoordinator(target, NoHandlers());

        coordinator.Dispose();
        coordinator.Dispose();

        Assert.Equal(1, target.Calls);
        Assert.Equal(PtyShutdownOutcome.Completed, coordinator.LastResult!.Outcome);
        Assert.Equal(PtyShutdownTrigger.Explicit, coordinator.LastResult.Trigger);
    }

    [Fact]
    public void Dispose_AfterAnEarlierTrigger_DoesNotRunASecondTeardown()
    {
        var target = new FakeTarget();
        var coordinator = new PtyShutdownCoordinator(target, NoHandlers());

        coordinator.HandleConsoleCtrlEvent(0);
        coordinator.Dispose();

        Assert.Equal(1, target.Calls);
        Assert.Equal(PtyShutdownTrigger.ConsoleCtrl, coordinator.LastResult!.Trigger);
    }

    [Fact]
    public void Dispose_ThrowingTarget_DoesNotThrowOutOfTheFinally()
    {
        var coordinator = new PtyShutdownCoordinator(
            new FakeTarget(throws: new InvalidOperationException("boom")),
            NoHandlers());

        var ex = Record.Exception(() => coordinator.Dispose());

        Assert.Null(ex);
        Assert.Equal(PtyShutdownOutcome.Faulted, coordinator.LastResult!.Outcome);
    }

    [Fact]
    public void Dispose_UninstallsBeforeTearingDown_SoTheTeardownCannotReEnterItsOwnHandlers()
    {
        var installerCalls = new List<bool>();
        var options = new PtyShutdownOptions
        {
            InstallProcessExit = true,
            InstallCancelKeyPress = false,
            ConsoleCtrlHandlerInstaller = (_, add) =>
            {
                installerCalls.Add(add);
                return true;
            },
        };
        var coordinator = new PtyShutdownCoordinator(new FakeTarget(), options).Install();

        coordinator.Dispose();

        Assert.Equal(new[] { true, false }, installerCalls);
        Assert.False(coordinator.ProcessExitHandlerInstalled);
        Assert.True(coordinator.HasShutDown);
    }

    // --- the production adapter ---------------------------------------------------------------------------

    [Fact]
    public void PtyRegistryShutdownTarget_OnAnEmptyRegistry_ClosesNothingAndCompletes()
    {
        using var registry = new PtyRegistry();
        var target = new PtyRegistryShutdownTarget(registry);
        using var coordinator = new PtyShutdownCoordinator(target, NoHandlers());

        var result = coordinator.Shutdown();

        Assert.Equal(PtyShutdownOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.SessionsClosed);
    }

    [Fact]
    public void PtyRegistryShutdownTarget_DoesNotDisposeTheRegistry()
    {
        // Ownership stays with whoever created the registry; the coordinator's contract is "close the sessions".
        using var registry = new PtyRegistry();
        using var coordinator = new PtyShutdownCoordinator(new PtyRegistryShutdownTarget(registry), NoHandlers());

        coordinator.Shutdown();

        Assert.False(registry.IsDisposed);
    }

    [Fact]
    public void PtyRegistryShutdownTarget_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new PtyRegistryShutdownTarget(null!));
    }

    [Fact]
    public void Constructor_RejectsNullTarget()
    {
        Assert.Throws<ArgumentNullException>(() => new PtyShutdownCoordinator(null!));
    }

    private static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }
}

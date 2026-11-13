namespace Accel.Tests;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Accel.App.Services;
using Accel.Cli;
using Accel.Metrics;
using Xunit;

/// <summary>
/// With several live Claude sessions, signals (status-line and hook posts, transcript writes) arrive faster than
/// a snapshot build takes. The single-flight path used to publish again the instant a build finished whenever a
/// signal had arrived meanwhile, which bypassed the 250 ms debounce and turned the feed into back-to-back builds,
/// each followed by a full UI-thread update. These tests hold a build open with a gate and check the debounce is
/// respected.
/// </summary>
public class TelemetryFeedLoopTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>A UI-thread stand-in the test drains explicitly, so "the UI thread" is the test thread.</summary>
    private sealed class QueueDispatcher : IUiThreadDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new();

        public bool IsOnUiThread => false;

        public int Pending => _queue.Count;

        public void Post(Action action) => _queue.Enqueue(action);

        public void Drain()
        {
            while (_queue.TryDequeue(out var action))
            {
                action();
            }
        }

        public void WaitForPending()
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (_queue.IsEmpty)
            {
                Assert.True(DateTime.UtcNow < deadline, "nothing was posted back to the UI thread");
                Thread.Sleep(5);
            }
        }
    }

    private sealed class GatedSource : ITelemetrySource
    {
        private int _builds;

        public event Action? Changed;

        public string? ProjectsDirectory => null;

        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Gate { get; } = new(false);

        public int Builds => Volatile.Read(ref _builds);

        public RootsTreeDto BuildSnapshot()
        {
            Interlocked.Increment(ref _builds);
            Entered.Set();
            Gate.Wait(Timeout);
            return TelemetryFixtures.EmptyTree();
        }

        public void RaiseChanged() => Changed?.Invoke();
    }

    [Fact]
    public void ASignalDuringABuild_DoesNotStartTheNextBuildUntilTheDebounceFires()
    {
        var source = new GatedSource();
        var dispatcher = new QueueDispatcher();
        var timer = new FakeDebounceTimer();
        using var feed = new TelemetryFeed(source, dispatcher, timer, buildOffUiThread: true);

        feed.Start();
        dispatcher.Drain();                       // Start's first Publish -> build 1 begins on a pool thread
        Assert.True(source.Entered.Wait(Timeout));

        // A signal arrives while build 1 is still running; the debounce window elapses.
        source.RaiseChanged();
        dispatcher.Drain();
        timer.Fire();                             // -> Publish -> single-flight: marks one trailing rebuild
        Assert.Equal(1, source.Builds);

        // Build 1 finishes and is applied.
        source.Gate.Set();
        dispatcher.WaitForPending();
        int restartsBeforeApply = timer.Restarts;
        dispatcher.Drain();

        // The trailing rebuild re-arms the debounce instead of starting at once...
        Assert.True(timer.Restarts > restartsBeforeApply, "the trailing rebuild must go through the debounce");
        Thread.Sleep(150);
        Assert.Equal(1, source.Builds);

        // ...and only the window elapsing starts it.
        timer.Fire();
        var deadline = DateTime.UtcNow + Timeout;
        while (source.Builds < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "the second build never started after the debounce fired");
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void ManySignalsDuringOneBuild_ProduceOneTrailingBuild_NotOnePerSignal()
    {
        var source = new GatedSource();
        var dispatcher = new QueueDispatcher();
        var timer = new FakeDebounceTimer();
        using var feed = new TelemetryFeed(source, dispatcher, timer, buildOffUiThread: true);

        feed.Start();
        dispatcher.Drain();
        Assert.True(source.Entered.Wait(Timeout));

        for (var i = 0; i < 10; i++)
        {
            source.RaiseChanged();
            dispatcher.Drain();
            timer.Fire();
        }

        source.Gate.Set();
        dispatcher.WaitForPending();
        dispatcher.Drain();
        timer.Fire();

        var deadline = DateTime.UtcNow + Timeout;
        while (source.Builds < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "the trailing build never started");
            Thread.Sleep(5);
        }

        Thread.Sleep(150);
        Assert.Equal(2, source.Builds);
    }

    [Fact]
    public void TheBackgroundBuild_ShipsTheTreeAlreadyBuilt_SoThePanelsDoNotBuildItOnTheUiThread()
    {
        var snapshot = TelemetryFixtures.EmptyTree();

        // Same snapshot instance -> same tree instance: what the feed warms in the background is what the panels
        // then get, instead of each building its own on every tick.
        Assert.Same(MonitorTreeBuilder.BuildCached(snapshot), MonitorTreeBuilder.BuildCached(snapshot));
        Assert.NotSame(MonitorTreeBuilder.BuildCached(snapshot), MonitorTreeBuilder.BuildCached(TelemetryFixtures.EmptyTree()));
    }

    [Fact]
    public void ANullSnapshot_StillBuildsAnEmptyTree()
    {
        var tree = MonitorTreeBuilder.BuildCached(null);

        Assert.Empty(tree.Roots);
        Assert.Null(tree.Unattributed);
    }
}

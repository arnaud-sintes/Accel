namespace Glaude.Tests;

using System;
using System.Collections.Generic;
using Glaude.App.Services;
using Glaude.Metrics;
using Xunit;

/// <summary>
/// Unit tests for P1-T2's <see cref="TelemetryFeed"/> - the single push feed wrapping
/// <c>SessionState.Changed</c> + <c>FileSystemWatcher</c> + the existing
/// <see cref="Glaude.Cli.DebounceCoalescer"/>.
///
/// Everything here runs headlessly: the UI-thread marshalling and the 250 ms debounce timer are
/// injected (<see cref="RecordingUiThreadDispatcher"/>, <see cref="FakeDebounceTimer"/>), and the
/// telemetry source is an in-memory double whose <c>ProjectsDirectory</c> is null, so no real
/// <see cref="System.IO.FileSystemWatcher"/> is ever created - see
/// <see cref="Start_WithoutProjectsDirectory_RunsWithoutAFileSystemWatcher"/>.
/// </summary>
public class TelemetryFeedTests
{
    private static (TelemetryFeed Feed, FakeTelemetrySource Source, RecordingUiThreadDispatcher Dispatcher, FakeDebounceTimer Timer, List<RootsTreeDto> Published, List<string> Failures) Build()
    {
        var source = new FakeTelemetrySource();
        var dispatcher = new RecordingUiThreadDispatcher();
        var timer = new FakeDebounceTimer();
        var feed = new TelemetryFeed(source, dispatcher, timer);

        var published = new List<RootsTreeDto>();
        var failures = new List<string>();
        feed.SnapshotAvailable += published.Add;
        feed.SnapshotFailed += failures.Add;

        return (feed, source, dispatcher, timer, published, failures);
    }

    [Fact]
    public void Start_PublishesOneImmediateSnapshot()
    {
        var (feed, source, _, _, published, _) = Build();

        feed.Start();

        // Mirrors MonitorForm.OnLoad's direct RefreshAndRender() - the panel is never blank while
        // waiting for the first change signal.
        Assert.Equal(1, source.BuildCount);
        Assert.Single(published);
        Assert.Same(source.Snapshot, published[0]);
        Assert.Same(source.Snapshot, feed.Latest);
    }

    [Fact]
    public void Start_IsIdempotent()
    {
        var (feed, source, _, _, published, _) = Build();

        feed.Start();
        feed.Start();

        Assert.Equal(1, source.BuildCount);
        Assert.Single(published);
    }

    [Fact]
    public void ChangedSignal_DoesNotPublishUntilTheDebounceWindowElapses()
    {
        var (feed, source, _, timer, published, _) = Build();
        feed.Start();
        published.Clear();

        source.RaiseChanged();

        Assert.Equal(1, timer.Restarts);       // the window was (re)started...
        Assert.Empty(published);               // ...but nothing rebuilt yet.
    }

    [Fact]
    public void BurstOfSignals_CoalescesIntoASinglePublish()
    {
        var (feed, source, _, timer, published, _) = Build();
        feed.Start();
        published.Clear();
        int buildsAfterStart = source.BuildCount;

        source.RaiseChanged();
        source.RaiseChanged();
        source.RaiseChanged();
        source.RaiseChanged();
        timer.Fire();

        // Same DebounceCoalescer contract as MonitorForm: every signal restarts the window, and the
        // whole burst produces exactly one rebuild.
        Assert.Equal(4, timer.Restarts);
        Assert.Single(published);
        Assert.Equal(buildsAfterStart + 1, source.BuildCount);
    }

    [Fact]
    public void TimerTickWithNoPendingSignal_PublishesNothing()
    {
        var (feed, source, _, timer, published, _) = Build();
        feed.Start();
        published.Clear();
        int buildsAfterStart = source.BuildCount;

        timer.Fire();
        timer.Fire();

        // The debounce timer is not a poll loop: with no signal pending, a tick rebuilds nothing.
        Assert.Empty(published);
        Assert.Equal(buildsAfterStart, source.BuildCount);
    }

    [Fact]
    public void NoSignals_MeansNoWorkAtAll()
    {
        var (feed, source, _, timer, published, _) = Build();

        feed.Start();
        published.Clear();

        // Nothing fires on a schedule of its own - no polling of /roots/tree, no periodic rebuild.
        Assert.Equal(0, timer.Restarts);
        Assert.Equal(1, source.BuildCount);
        Assert.Empty(published);
    }

    [Fact]
    public void SignalsAreMarshalledThroughTheDispatcherBeforeTouchingTheCoalescer()
    {
        var (feed, source, dispatcher, timer, published, _) = Build();
        feed.Start();
        published.Clear();

        // Simulate the real thing: SessionState.Changed fires on a Kestrel request thread, so the
        // signal must be queued onto the UI thread rather than mutating coalescer state in place.
        dispatcher.RunInline = false;
        source.RaiseChanged();

        Assert.Equal(0, timer.Restarts);
        Assert.Equal(1, dispatcher.PendingCount);

        dispatcher.Drain();

        Assert.Equal(1, timer.Restarts);
    }

    [Fact]
    public void RequestRefresh_GoesThroughTheSameDebounceWindow()
    {
        var (feed, source, _, timer, published, _) = Build();
        feed.Start();
        published.Clear();
        int buildsAfterStart = source.BuildCount;

        feed.RequestRefresh();
        feed.RequestRefresh();

        Assert.Equal(2, timer.Restarts);
        Assert.Empty(published);

        timer.Fire();

        Assert.Single(published);
        Assert.Equal(buildsAfterStart + 1, source.BuildCount);
    }

    [Fact]
    public void BuildFailure_RaisesSnapshotFailedAndKeepsTheLastGoodSnapshot()
    {
        var (feed, source, _, timer, published, failures) = Build();
        var good = TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(@"C:\projects") });
        source.Snapshot = good;
        feed.Start();

        source.ThrowOnBuild = new InvalidOperationException("disk exploded");
        source.RaiseChanged();
        timer.Fire();

        Assert.Single(published);                     // still only the good one
        Assert.Same(good, feed.Latest);               // last good snapshot preserved
        Assert.Equal(new[] { "disk exploded" }, failures);
    }

    [Fact]
    public void Start_WithoutProjectsDirectory_RunsWithoutAFileSystemWatcher()
    {
        var (feed, source, _, _, _, _) = Build();
        source.ProjectsDirectory = null;

        feed.Start();

        Assert.False(feed.IsWatchingFileSystem);
    }

    [Fact]
    public void Start_WithMissingProjectsDirectory_StillRunsWithoutAWatcher()
    {
        var (feed, source, _, timer, published, _) = Build();
        source.ProjectsDirectory = Path.Combine(Path.GetTempPath(), $"glaude-not-there-{Guid.NewGuid():N}");

        feed.Start();

        // Best-effort watcher, exactly as MonitorForm.TryCreateProjectsWatcher: a missing directory
        // degrades to "no watcher", and the Changed signal still works.
        Assert.False(feed.IsWatchingFileSystem);

        source.RaiseChanged();
        timer.Fire();
        Assert.Equal(2, published.Count);
    }

    [Fact]
    public void Start_WithRealProjectsDirectory_CreatesTheWatcherAndDisposeTearsItDown()
    {
        var (feed, source, _, _, _, _) = Build();
        string dir = Path.Combine(Path.GetTempPath(), $"glaude-feed-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            source.ProjectsDirectory = dir;
            feed.Start();

            Assert.True(feed.IsWatchingFileSystem);

            feed.Dispose();
            Assert.False(feed.IsWatchingFileSystem);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Fixture cleanup only.
            }
        }
    }

    [Fact]
    public void Dispose_UnsubscribesFromTheSourceAndStopsTheTimer()
    {
        var (feed, source, _, timer, published, _) = Build();
        feed.Start();
        published.Clear();

        feed.Dispose();

        Assert.False(source.HasSubscribers);
        Assert.True(timer.Disposed);

        source.RaiseChanged();
        timer.Fire();

        Assert.Empty(published);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var (feed, _, _, _, _, _) = Build();
        feed.Start();

        feed.Dispose();
        feed.Dispose();
    }
}

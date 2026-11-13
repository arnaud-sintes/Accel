namespace Accel.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using Accel.App.ViewModels;
using Accel.Metrics;
using Xunit;

/// <summary>
/// Panel A used to clear and recreate every row (and every WPF container) on every telemetry tick. A running
/// session changes its transcript many times a second, so that was the panel's whole recurring cost - and it
/// destroyed the row under a right-click. A tick that changes nothing the panel shows is now a no-op.
/// </summary>
public class RootsPanelSkipUnchangedTickTests
{
    private const string Root = @"C:\projects\a";

    private static (RootsPanelViewModel Vm, FakeTelemetryFeed Feed, List<string> Events) Build()
    {
        var feed = new FakeTelemetryFeed();
        var vm = new RootsPanelViewModel(feed, new RecordingUiThreadDispatcher());
        var events = new List<string>();
        vm.TreeRebuilding += () => events.Add("rebuilding");
        vm.TreeRebuilt += _ => events.Add("rebuilt");
        return (vm, feed, events);
    }

    private static RootsTreeDto Tree(SessionTreeDto session, DateTime? generatedAtUtc = null) =>
        TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(Root, session) }, generatedAtUtc: generatedAtUtc);

    [Fact]
    public void AnIdenticalTick_DoesNotRebuild_AndKeepsTheSameRowObjects()
    {
        var (vm, feed, events) = Build();
        feed.Publish(Tree(TelemetryFixtures.Session("s-1", isLive: true)));
        var rowBefore = vm.Roots.Single().Children.Single();
        events.Clear();

        feed.Publish(Tree(TelemetryFixtures.Session("s-1", isLive: true)));

        Assert.Empty(events);
        Assert.Same(rowBefore, vm.Roots.Single().Children.Single());
    }

    [Fact]
    public void ARunningSessionsDurationAndTokens_AreNotVisibleChanges()
    {
        var (vm, feed, events) = Build();
        feed.Publish(Tree(TelemetryFixtures.Session("s-1", isLive: true, durationMs: 1_000, consumedTokens: 10)));
        events.Clear();

        feed.Publish(Tree(TelemetryFixtures.Session("s-1", isLive: true, durationMs: 61_000, consumedTokens: 9_999)));

        Assert.Empty(events);
    }

    [Fact]
    public void ASkippedTick_StillUpdatesTheCountersAndTheRefreshedAtText()
    {
        var (vm, feed, _) = Build();
        feed.Publish(Tree(TelemetryFixtures.Session("s-1", isLive: true), new DateTime(2026, 8, 14, 10, 0, 5, DateTimeKind.Utc)));
        string before = vm.RefreshedAtText;

        feed.Publish(Tree(TelemetryFixtures.Session("s-1", isLive: true), new DateTime(2026, 8, 14, 15, 30, 0, DateTimeKind.Utc)));

        Assert.NotEqual(before, vm.RefreshedAtText);
        Assert.Equal(1, vm.LiveSessionCount);
    }

    public static IEnumerable<object[]> VisibleChanges() => new[]
    {
        new object[] { "a new name", (Func<SessionTreeDto>)(() => TelemetryFixtures.Session("s-1", isLive: true, name: "renamed")) },
        new object[] { "the session ended", (Func<SessionTreeDto>)(() => TelemetryFixtures.Session("s-1", isLive: false)) },
        new object[] { "a Stop (waiting) timestamp", (Func<SessionTreeDto>)(() => TelemetryFixtures.Session("s-1", isLive: true, waitingSinceUtc: new DateTime(2026, 8, 14, 10, 1, 0, DateTimeKind.Utc))) },
        new object[] { "a new sub-agent", (Func<SessionTreeDto>)(() => TelemetryFixtures.Session("s-1", isLive: true, agents: new[] { TelemetryFixtures.Agent("ag-1") })) },
        new object[] { "another working directory", (Func<SessionTreeDto>)(() => TelemetryFixtures.Session("s-1", isLive: true, cwd: @"C:\projects\elsewhere")) },
    };

    [Theory]
    [MemberData(nameof(VisibleChanges))]
    public void AVisibleChange_Rebuilds(string _, Func<SessionTreeDto> changed)
    {
        var (vm, feed, events) = Build();
        feed.Publish(Tree(TelemetryFixtures.Session("s-1", isLive: true)));
        events.Clear();

        feed.Publish(Tree(changed()));

        Assert.Equal(new[] { "rebuilding", "rebuilt" }, events);
    }

    [Fact]
    public void AddingARoot_Rebuilds()
    {
        var (vm, feed, events) = Build();
        feed.Publish(Tree(TelemetryFixtures.Session("s-1", isLive: true)));
        events.Clear();

        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(Root, TelemetryFixtures.Session("s-1", isLive: true)),
            TelemetryFixtures.Root(@"C:\projects\b"),
        }));

        Assert.Equal(new[] { "rebuilding", "rebuilt" }, events);
        Assert.Equal(2, vm.Roots.Count);
    }

    [Fact]
    public void ARowsExpansionAndSelection_SurviveAnySkippedTicks()
    {
        var (vm, feed, _) = Build();
        feed.Publish(Tree(TelemetryFixtures.Session("s-1", isLive: true)));
        var root = vm.Roots.Single();
        root.IsExpanded = false;
        root.Children.Single().IsSelected = true;

        for (var i = 0; i < 5; i++)
        {
            feed.Publish(Tree(TelemetryFixtures.Session("s-1", isLive: true, durationMs: i * 1000)));
        }

        Assert.False(vm.Roots.Single().IsExpanded);
        Assert.Equal("s-1", vm.SelectedKey);
    }
}

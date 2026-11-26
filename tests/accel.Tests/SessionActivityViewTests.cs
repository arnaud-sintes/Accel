namespace Accel.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using Accel.App.Controls;
using Accel.App.ViewModels;
using Accel.Metrics;
using Xunit;

/// <summary>The working / idle state as panel A's rows, panel C's tabs and the indicator control show it.</summary>
public class SessionActivityViewTests
{
    private const string Root = @"C:\projects\a";

    private static (RootsPanelViewModel Vm, FakeTelemetryFeed Feed, List<string> Events) Build()
    {
        var feed = new FakeTelemetryFeed();
        var vm = new RootsPanelViewModel(feed, new RecordingUiThreadDispatcher());
        var events = new List<string>();
        vm.TreeRebuilding += () => events.Add("rebuilding");
        return (vm, feed, events);
    }

    private static SessionTreeDto Session(string id, string? activity, bool live = true, long? durationMs = null) =>
        TelemetryFixtures.Session(id, isLive: live, durationMs: durationMs) with { Activity = activity };

    private static RootsTreeDto Tree(params SessionTreeDto[] sessions) =>
        TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(Root, sessions) });

    private static RootsPanelNodeViewModel Node(RootsPanelViewModel vm, string key) =>
        vm.Roots.SelectMany(Flatten).Single(n => n.Key == key);

    private static IEnumerable<RootsPanelNodeViewModel> Flatten(RootsPanelNodeViewModel n) =>
        new[] { n }.Concat(n.Children.SelectMany(Flatten));

    // --- panel A -----------------------------------------------------------------------------------------

    [Fact]
    public void ARunningSession_ShowsItsActivity_OnItsRow()
    {
        var (vm, feed, _) = Build();
        feed.Publish(Tree(Session("busy", "working"), Session("waiting", "idle")));

        Assert.Equal(SessionActivity.Working, Node(vm, "busy").Activity);
        Assert.True(Node(vm, "busy").IsWorking);
        Assert.Equal(SessionActivity.Idle, Node(vm, "waiting").Activity);
        Assert.True(Node(vm, "waiting").IsIdle);
        Assert.True(Node(vm, "waiting").HasActivity);
    }

    [Fact]
    public void ASleepingSession_AndTheRootRow_ShowNoActivity()
    {
        var (vm, feed, _) = Build();
        feed.Publish(Tree(Session("asleep", activity: null, live: false)));

        Assert.False(Node(vm, "asleep").HasActivity);
        Assert.False(Node(vm, Root).HasActivity);
    }

    [Fact]
    public void ALiveSubAgentRow_IsWorking_ByDefinition()
    {
        var (vm, feed, _) = Build();
        var session = TelemetryFixtures.Session("s", isLive: true, agents: new[] { TelemetryFixtures.Agent("ag-1") }) with { Activity = "working" };
        feed.Publish(Tree(session));

        Assert.Equal(SessionActivity.Working, Node(vm, "ag-1").Activity);
    }

    [Fact]
    public void TheState_IsSaidInTheTooltip_AndToAssistiveTech()
    {
        var (vm, feed, _) = Build();
        feed.Publish(Tree(Session("busy", "working"), Session("waiting", "idle")));

        Assert.EndsWith(" — working", Node(vm, "busy").TooltipText);
        Assert.Contains("Working.", Node(vm, "busy").AutomationDescription);
        Assert.EndsWith(" — idle", Node(vm, "waiting").TooltipText);
        Assert.Contains("Idle.", Node(vm, "waiting").AutomationDescription);
    }

    [Fact]
    public void ARowWithNoActivity_KeepsItsExactTooltip()
    {
        var (vm, feed, _) = Build();
        feed.Publish(Tree(Session("asleep", activity: null, live: false)));

        Assert.DoesNotContain("working", Node(vm, "asleep").TooltipText);
        Assert.DoesNotContain("idle", Node(vm, "asleep").TooltipText);
    }

    [Fact]
    public void AChangeOfActivity_IsAVisibleChange_AndRebuildsTheRows()
    {
        var (vm, feed, events) = Build();
        feed.Publish(Tree(Session("s", "working")));
        events.Clear();

        feed.Publish(Tree(Session("s", "idle")));

        Assert.Equal(new[] { "rebuilding" }, events);
        Assert.Equal(SessionActivity.Idle, Node(vm, "s").Activity);
    }

    [Fact]
    public void TheSameActivityOnTheNextTick_RebuildsNothing()
    {
        var (vm, feed, events) = Build();
        feed.Publish(Tree(Session("s", "working")));
        events.Clear();

        feed.Publish(Tree(Session("s", "working")));

        Assert.Empty(events);
    }

    [Fact]
    public void ASkippedTick_StillRefreshesTheDurationQuotedInTheTooltip_WithoutRebuilding()
    {
        var (vm, feed, events) = Build();
        feed.Publish(Tree(Session("s", "working", durationMs: 5_000)));
        var row = Node(vm, "s");
        string before = row.TooltipText;
        events.Clear();

        feed.Publish(Tree(Session("s", "working", durationMs: 725_000)));

        Assert.Empty(events); // no rebuild...
        Assert.Same(row, Node(vm, "s")); // ...the very same row object...
        Assert.NotEqual(before, row.TooltipText); // ...whose tooltip moved on
        Assert.EndsWith(" — working", row.TooltipText);
        Assert.Equal(725_000, row.DurationMs);
    }

    [Fact]
    public void TheTooltipChange_IsNotifiedToTheBinding()
    {
        var (vm, feed, _) = Build();
        feed.Publish(Tree(Session("s", "working", durationMs: 5_000)));
        var row = Node(vm, "s");
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        feed.Publish(Tree(Session("s", "working", durationMs: 725_000)));

        Assert.Contains(nameof(RootsPanelNodeViewModel.TooltipText), changed);
    }

    [Fact]
    public void ActivityOfSession_ReadsWhatThePanelShows_CaseInsensitively()
    {
        var (vm, feed, _) = Build();
        feed.Publish(Tree(Session("Busy-One", "working"), Session("asleep", activity: null, live: false)));

        Assert.Equal(SessionActivity.Working, vm.ActivityOfSession("busy-one"));
        Assert.Equal(SessionActivity.None, vm.ActivityOfSession("asleep"));
        Assert.Equal(SessionActivity.None, vm.ActivityOfSession("unknown"));
        Assert.Equal(SessionActivity.None, vm.ActivityOfSession(null));
        Assert.Equal(SessionActivity.None, vm.ActivityOfSession(Root)); // a root row is not a session
    }

    // --- panel C -----------------------------------------------------------------------------------------

    [Fact]
    public void ARunningSessionTab_ShowsTheActivityItIsGiven()
    {
        var tab = new TabViewModel("s", "My session");

        tab.Activity = SessionActivity.Working;

        Assert.Equal(SessionActivity.Working, tab.ShownActivity);
        Assert.True(tab.IsWorking);
        Assert.False(tab.IsIdle);
        Assert.Contains("Working.", tab.AutomationDescription);

        tab.Activity = SessionActivity.Idle;
        Assert.True(tab.IsIdle);
        Assert.Contains("Idle.", tab.AutomationDescription);
    }

    [Fact]
    public void AnEndedTab_ShowsNoIndicator_WhateverItWasLastTold()
    {
        var tab = new TabViewModel("s", "My session") { Activity = SessionActivity.Working };

        tab.HasEnded = true;

        Assert.Equal(SessionActivity.None, tab.ShownActivity);
        Assert.False(tab.IsWorking);
        Assert.DoesNotContain("Working.", tab.AutomationDescription);
    }

    [Fact]
    public void ShellAndFileTabs_NeverShowAnIndicator()
    {
        var shell = TabViewModel.ForShell("shell", "Terminal");
        var file = TabViewModel.ForFile(@"C:\projects\a\README.md");

        shell.Activity = SessionActivity.Working;
        file.Activity = SessionActivity.Working;

        Assert.Equal(SessionActivity.None, shell.ShownActivity);
        Assert.Equal(SessionActivity.None, file.ShownActivity);
    }

    [Fact]
    public void ATabs_ActivityChange_IsNotifiedToItsBindings()
    {
        var tab = new TabViewModel("s", "My session");
        var changed = new List<string?>();
        tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        tab.Activity = SessionActivity.Working;

        Assert.Contains(nameof(TabViewModel.ShownActivity), changed);
        Assert.Contains(nameof(TabViewModel.AutomationDescription), changed);
    }

    // --- the indicator control ---------------------------------------------------------------------------

    [Fact]
    public void TheIndicator_TakesNoRoomWhenThereIsNothingToShow_AndAFixedSquareOtherwise()
    {
        RunOnSta(() =>
        {
            var indicator = new ActivityIndicator();

            indicator.Measure(new Size(100, 100));
            Assert.Equal(0, indicator.DesiredSize.Width);
            Assert.Equal(Visibility.Collapsed, indicator.Visibility);

            indicator.State = SessionActivity.Working;
            indicator.Measure(new Size(100, 100));
            Assert.Equal(Visibility.Visible, indicator.Visibility);
            Assert.Equal(12, indicator.DesiredSize.Width);
            Assert.Equal(12, indicator.DesiredSize.Height);

            indicator.State = SessionActivity.None;
            Assert.Equal(Visibility.Collapsed, indicator.Visibility);
        });
    }

    [Fact]
    public void TheIndicator_SaysItsStateInWordsToo()
    {
        RunOnSta(() =>
        {
            var indicator = new ActivityIndicator { State = SessionActivity.Working };
            Assert.Equal("Working", indicator.ToolTip);
            Assert.Equal("Working", System.Windows.Automation.AutomationProperties.GetName(indicator));

            indicator.State = SessionActivity.Idle;
            Assert.Contains("Idle", (string)indicator.ToolTip);
            Assert.Equal("Idle", System.Windows.Automation.AutomationProperties.GetName(indicator));
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            throw captured;
        }
    }
}

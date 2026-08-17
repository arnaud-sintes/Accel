namespace Accel.Tests;

using System.Linq;
using Accel.App.Services;
using Accel.App.ViewModels;
using Xunit;

/// <summary>
/// Unit tests for panel E's <see cref="AgentGraphViewModel"/> - driven exactly like
/// <c>RootsPanelViewModelTests</c> (<see cref="FakeTelemetryFeed"/> + <see cref="RecordingUiThreadDispatcher"/>,
/// a real <see cref="SessionSelectionService"/> with its writer acquired in-test, and
/// <see cref="TelemetryFixtures"/> for <see cref="Accel.Metrics.RootsTreeDto"/> fixtures). No WPF
/// <c>Dispatcher</c>, no timer, no <c>FileSystemWatcher</c>.
/// </summary>
public class AgentGraphViewModelTests
{
    private static (AgentGraphViewModel Vm, FakeTelemetryFeed Feed, SessionSelectionService Selection, ISessionSelectionWriter Writer) Build()
    {
        var feed = new FakeTelemetryFeed();
        var dispatcher = new RecordingUiThreadDispatcher();
        var selection = new SessionSelectionService();
        var writer = selection.AcquireWriter();
        return (new AgentGraphViewModel(feed, dispatcher, selection), feed, selection, writer);
    }

    [Fact]
    public void Rebuild_WithFocusedSession_ProjectsParentFirstThenAgentsInOrder()
    {
        var (vm, feed, _, writer) = Build();
        var agentOne = TelemetryFixtures.Agent("agent-1");
        var agentTwo = TelemetryFixtures.Agent("agent-2");
        var session = TelemetryFixtures.Session("session-1", isLive: true, agents: new[] { agentOne, agentTwo });
        writer.SetFocused("session-1");

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(@"C:\projects", session) }));

        Assert.Equal(3, vm.Nodes.Count);
        Assert.Equal(AgentGraphNodeRole.Parent, vm.Nodes[0].Role);
        Assert.Equal("session-1", vm.Nodes[0].Key);
        Assert.Equal(AgentGraphNodeRole.Child, vm.Nodes[1].Role);
        Assert.Equal("agent-1", vm.Nodes[1].Key);
        Assert.Equal(AgentGraphNodeRole.Child, vm.Nodes[2].Role);
        Assert.Equal("agent-2", vm.Nodes[2].Key);
    }

    [Fact]
    public void Rebuild_ParentNode_CarriesModelBadgeEffortContextDurationAndTokens()
    {
        var (vm, feed, _, writer) = Build();
        var session = TelemetryFixtures.Session("session-1", isLive: true, durationMs: 424_000, consumedTokens: 148_200);
        writer.SetFocused("session-1");

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(@"C:\projects", session) }));

        var parent = vm.Nodes[0];
        Assert.Equal("S", parent.ModelBadge.Letter);
        Assert.True(parent.EffortLevel > 0);
        Assert.Equal($"{parent.Columns.Duration} · {parent.Columns.Tokens} · {parent.Columns.Context}", parent.DetailText);
        Assert.Contains("7m", parent.DetailText);
        Assert.Contains("148.2K", parent.DetailText);
    }

    [Fact]
    public void Rebuild_AgentNode_CarriesItsOwnDurationAndTokens()
    {
        var (vm, feed, _, writer) = Build();
        var agent = TelemetryFixtures.Agent("agent-1", durationMs: 12_000, consumedTokens: 842);
        var session = TelemetryFixtures.Session("session-1", isLive: true, agents: new[] { agent });
        writer.SetFocused("session-1");

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(@"C:\projects", session) }));

        var child = vm.Nodes[1];
        Assert.Equal(12L, child.DurationMs / 1000);
        Assert.Equal(842L, child.ConsumedTokens);
        Assert.Contains("12s", child.DetailText);
        Assert.Contains("842", child.DetailText);
    }

    [Fact]
    public void Rebuild_SessionNode_TooltipCarriesTheContextOnlyTokenCaveat()
    {
        var (vm, feed, _, writer) = Build();
        var session = TelemetryFixtures.Session("session-1", isLive: true);
        writer.SetFocused("session-1");

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(@"C:\projects", session) }));

        Assert.Contains("context-window usage", vm.Nodes[0].TooltipText);
    }

    [Fact]
    public void Rebuild_AgentNode_TooltipHasNoCaveat()
    {
        var (vm, feed, _, writer) = Build();
        var agent = TelemetryFixtures.Agent("agent-1");
        var session = TelemetryFixtures.Session("session-1", isLive: true, agents: new[] { agent });
        writer.SetFocused("session-1");

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(@"C:\projects", session) }));

        Assert.DoesNotContain("context-window usage", vm.Nodes[1].TooltipText);
    }

    [Fact]
    public void Rebuild_NoFocusedSession_ClearsNodesAndSetsNoSessionFocusedStatus()
    {
        var (vm, feed, _, _) = Build();
        var session = TelemetryFixtures.Session("session-1", isLive: true);

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(@"C:\projects", session) }));

        Assert.Empty(vm.Nodes);
        Assert.False(vm.HasGraph);
        Assert.False(vm.HasAgents);
        Assert.Contains("No session focused", vm.StatusText);
    }

    [Fact]
    public void Rebuild_FocusedSessionAbsentFromSnapshot_SetsNoLongerInTheTreeStatus()
    {
        var (vm, feed, _, writer) = Build();
        writer.SetFocused("session-missing");
        var session = TelemetryFixtures.Session("session-1", isLive: true);

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(@"C:\projects", session) }));

        Assert.Empty(vm.Nodes);
        Assert.False(vm.HasGraph);
        Assert.Contains("no longer in the tree", vm.StatusText);
    }

    [Fact]
    public void Rebuild_FocusedSessionWithZeroAgents_SetsHasGraphTrueAndHasAgentsFalse()
    {
        var (vm, feed, _, writer) = Build();
        var session = TelemetryFixtures.Session("session-1", isLive: true);
        writer.SetFocused("session-1");

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(@"C:\projects", session) }));

        Assert.True(vm.HasGraph);
        Assert.False(vm.HasAgents);
        Assert.Single(vm.Nodes);
    }

    [Fact]
    public void Rebuild_HistoricalFocusedSession_SetsSessionEndedStatusAndStillRenders()
    {
        var (vm, feed, _, writer) = Build();
        var session = TelemetryFixtures.Session("session-1", isLive: false);
        writer.SetFocused("session-1");

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(@"C:\projects", session) }));

        Assert.True(vm.HasGraph);
        Assert.Contains("ended", vm.StatusText);
        Assert.Single(vm.Nodes);
    }

    [Fact]
    public void FocusChange_WithNoNewSnapshot_ReprojectsFromTheCachedSnapshot()
    {
        var (vm, feed, _, writer) = Build();
        var sessionOne = TelemetryFixtures.Session("session-1", isLive: true);
        var sessionTwo = TelemetryFixtures.Session("session-2", isLive: true);
        writer.SetFocused("session-1");

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(@"C:\projects", sessionOne, sessionTwo) }));
        Assert.Equal("session-1", vm.Nodes[0].Key);

        writer.SetFocused("session-2");
        Assert.Equal("session-2", vm.Nodes[0].Key);
    }

    [Fact]
    public void FocusedSessionId_MatchesCaseInsensitively()
    {
        var (vm, feed, _, writer) = Build();
        var session = TelemetryFixtures.Session("Session-ABC", isLive: true);
        writer.SetFocused("session-abc");

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(@"C:\projects", session) }));

        Assert.True(vm.HasGraph);
        Assert.Equal("Session-ABC", vm.Nodes[0].Key);
    }

    [Fact]
    public void SnapshotFailed_KeepsTheLastGoodGraphAndSetsRefreshFailedStatus()
    {
        var (vm, feed, _, writer) = Build();
        var session = TelemetryFixtures.Session("session-1", isLive: true);
        writer.SetFocused("session-1");
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(@"C:\projects", session) }));

        feed.Fail("boom");

        Assert.Single(vm.Nodes);
        Assert.Equal("Refresh failed: boom", vm.StatusText);
    }

    [Fact]
    public void Constructed_WithAFeedThatAlreadyHasASnapshot_ProjectsImmediately()
    {
        var feed = new FakeTelemetryFeed();
        var dispatcher = new RecordingUiThreadDispatcher();
        var selection = new SessionSelectionService();
        var writer = selection.AcquireWriter();
        var session = TelemetryFixtures.Session("session-1", isLive: true);
        writer.SetFocused("session-1");
        feed.Latest = TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(@"C:\projects", session) });

        var vm = new AgentGraphViewModel(feed, dispatcher, selection);

        Assert.Single(vm.Nodes);
        Assert.Equal("session-1", vm.Nodes[0].Key);
    }

    [Fact]
    public void Dispose_UnhooksFeedEventsAndUnsubscribesFromSelection()
    {
        var (vm, feed, _, _) = Build();
        Assert.True(feed.HasSnapshotSubscribers);

        vm.Dispose();

        Assert.False(feed.HasSnapshotSubscribers);
    }

    [Fact]
    public void AutomationDescription_ChildIncludesTheParentSessionName()
    {
        var (vm, feed, _, writer) = Build();
        var agent = TelemetryFixtures.Agent("agent-1");
        var session = TelemetryFixtures.Session("session-1", isLive: true, name: "my session", agents: new[] { agent });
        writer.SetFocused("session-1");

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(@"C:\projects", session) }));

        Assert.Contains("child of session my session", vm.Nodes[1].AutomationDescription);
    }
}

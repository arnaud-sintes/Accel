namespace Glaude.Tests;

using System.Linq;
using Glaude.App.ViewModels;
using Glaude.Cli;
using Glaude.Metrics;
using Xunit;

/// <summary>
/// Unit tests for P1-T2's <see cref="RootsPanelViewModel"/> (panel A).
///
/// <para>The expansion/selection cases are the ported form of what <c>MonitorForm.RenderTree</c> +
/// <see cref="MonitorTreeExpansion"/> guarantee today (see CX-T1: those WinForms-coupled cases must
/// exist against the new ViewModel before the old ones are deleted): capture-before-clear, re-expand
/// by stable key (root path / session id / agent id), one-shot default expansion for first-seen live
/// nodes, no refighting a user's deliberate collapse, and selection restored by key or dropped when
/// the node is gone.</para>
///
/// <para>No WPF <c>Dispatcher</c>, no timer and no <c>FileSystemWatcher</c> are involved - the
/// ViewModel only ever sees an <c>ITelemetryFeed</c> and an <c>IUiThreadDispatcher</c>.</para>
/// </summary>
public class RootsPanelViewModelTests
{
    private const string RootPath = @"C:\projects";
    private const string OtherRootPath = @"C:\other";

    private static (RootsPanelViewModel Vm, FakeTelemetryFeed Feed, RecordingUiThreadDispatcher Dispatcher) Build()
    {
        var feed = new FakeTelemetryFeed();
        var dispatcher = new RecordingUiThreadDispatcher();
        return (new RootsPanelViewModel(feed, dispatcher), feed, dispatcher);
    }

    private static RootsPanelNodeViewModel Node(RootsPanelViewModel vm, string key) =>
        Flatten(vm).Single(n => n.Key == key);

    private static System.Collections.Generic.IEnumerable<RootsPanelNodeViewModel> Flatten(RootsPanelViewModel vm) =>
        vm.Roots.SelectMany(Descend);

    private static System.Collections.Generic.IEnumerable<RootsPanelNodeViewModel> Descend(RootsPanelNodeViewModel node) =>
        new[] { node }.Concat(node.Children.SelectMany(Descend));

    [Fact]
    public void Snapshot_RendersRootsSessionsAndAgentsInTreeOrder()
    {
        var (vm, feed, _) = Build();

        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(
                RootPath,
                TelemetryFixtures.Session("session-live", isLive: true, agents: new[] { TelemetryFixtures.Agent("agent-1") })),
            TelemetryFixtures.Root(OtherRootPath),
        }));

        Assert.Equal(new[] { RootPath, OtherRootPath }, vm.Roots.Select(r => r.Key));

        var rootNode = vm.Roots[0];
        Assert.Equal(RootsPanelNodeKind.Root, rootNode.Kind);

        var sessionNode = Assert.Single(rootNode.Children);
        Assert.Equal("session-live", sessionNode.Key);
        Assert.Equal(RootsPanelNodeKind.Session, sessionNode.Kind);
        Assert.Equal(MonitorNodeState.Live, sessionNode.State);

        var agentNode = Assert.Single(sessionNode.Children);
        Assert.Equal("agent-1", agentNode.Key);
        Assert.Equal(RootsPanelNodeKind.Agent, agentNode.Kind);
    }

    [Fact]
    public void Snapshot_RowTextAndColumnsComeFromTheSharedMonitorTreeBuilder()
    {
        var (vm, feed, _) = Build();
        var dto = TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("session-1", isLive: true, name: "a session")),
        });

        feed.Publish(dto);

        // Same pure builder the WinForms window walks, so the two render identical text/columns.
        var expected = MonitorTreeBuilder.Build(dto);
        Assert.Equal(expected.Roots[0].Text, vm.Roots[0].Text);
        Assert.Equal(expected.Roots[0].Sessions[0].Text, vm.Roots[0].Children[0].Text);
        Assert.Equal(expected.Roots[0].Sessions[0].Columns, vm.Roots[0].Children[0].Columns);
    }

    [Fact]
    public void EmptyRoot_GetsTheSingleKeylessNoSessionsPlaceholder()
    {
        var (vm, feed, _) = Build();

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(RootPath) }));

        var placeholder = Assert.Single(vm.Roots[0].Children);
        Assert.Equal(MonitorTreeBuilder.NoSessionsPlaceholder(), placeholder.Text);
        Assert.Equal(RootsPanelNodeKind.Placeholder, placeholder.Kind);
        Assert.Equal(string.Empty, placeholder.Key);
    }

    [Fact]
    public void UnattributedSessions_RenderAsATrailingSyntheticRoot()
    {
        var (vm, feed, _) = Build();

        feed.Publish(TelemetryFixtures.Tree(
            roots: new[] { TelemetryFixtures.Root(RootPath) },
            unattributedSessions: new[] { TelemetryFixtures.Session("orphan-session") }));

        Assert.Equal(2, vm.Roots.Count);
        Assert.Equal("(unattributed)", vm.Roots[1].Key);
        Assert.Equal("orphan-session", vm.Roots[1].Children[0].Key);
    }

    [Fact]
    public void EmptySnapshot_IsRenderedAsAnEmptyButWorkingTree()
    {
        var (vm, feed, _) = Build();

        feed.Publish(TelemetryFixtures.EmptyTree());

        // "Works but empty" must be distinguishable from "broken": a snapshot arrived (HasSnapshot),
        // the counters are all zero, and StatusText is the normal as-of line rather than a failure.
        Assert.Empty(vm.Roots);
        Assert.True(vm.HasSnapshot);
        Assert.Equal(0, vm.RootCount);
        Assert.Equal(0, vm.SessionCount);
        Assert.Contains("live state as of", vm.StatusText);
        Assert.DoesNotContain("Refresh failed", vm.StatusText);
    }

    [Fact]
    public void Counters_AndStatusText_ReflectTheSnapshot()
    {
        var (vm, feed, _) = Build();

        feed.Publish(TelemetryFixtures.Tree(
            roots: new[]
            {
                TelemetryFixtures.Root(
                    RootPath,
                    TelemetryFixtures.Session("s-live", isLive: true),
                    TelemetryFixtures.Session("s-old")),
                TelemetryFixtures.Root(OtherRootPath),
            },
            unattributedSessions: new[] { TelemetryFixtures.Session("s-orphan") }));

        Assert.Equal(2, vm.RootCount);
        Assert.Equal(3, vm.SessionCount);
        Assert.Equal(1, vm.LiveSessionCount);
        Assert.Contains("2 root(s), 3 session(s), 1 running", vm.StatusText);
    }

    [Fact]
    public void SnapshotFailure_SurfacesAsStatusTextOnly()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(RootPath) }));

        feed.Fail("disk exploded");

        Assert.Equal("Refresh failed: disk exploded", vm.StatusText);
        Assert.Single(vm.Roots); // last good tree kept, exactly as MonitorForm.RefreshAndRender does
    }

    [Fact]
    public void FirstSnapshot_DefaultExpandsANewLiveSessionAndItsRoot()
    {
        var (vm, feed, _) = Build();

        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-live", isLive: true)),
        }));

        Assert.True(Node(vm, RootPath).IsExpanded);
        Assert.True(Node(vm, "s-live").IsExpanded);
    }

    [Fact]
    public void FirstSnapshot_DoesNotDefaultExpandAHistoricalOnlyRoot()
    {
        var (vm, feed, _) = Build();

        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-old")),
        }));

        Assert.False(Node(vm, RootPath).IsExpanded);
        Assert.False(Node(vm, "s-old").IsExpanded);
    }

    [Fact]
    public void UserExpansion_IsPreservedAcrossARebuild()
    {
        var (vm, feed, _) = Build();
        var dto = TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-old")),
        });
        feed.Publish(dto);

        Node(vm, RootPath).IsExpanded = true;
        feed.Publish(dto);

        // Keyed on the stable root path, never on node index - the node objects themselves are all
        // new instances after the rebuild.
        Assert.True(Node(vm, RootPath).IsExpanded);
    }

    [Fact]
    public void UserCollapseOfAStillLiveSession_IsNotRefoughtByTheDefaultExpansionRule()
    {
        var (vm, feed, _) = Build();
        var dto = TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-live", isLive: true)),
        });
        feed.Publish(dto);
        Assert.True(Node(vm, "s-live").IsExpanded); // one-shot default on first sight

        Node(vm, "s-live").IsExpanded = false;
        feed.Publish(dto);

        // Still live, but already "ever seen" - so it stays exactly as the user left it.
        Assert.False(Node(vm, "s-live").IsExpanded);
    }

    [Fact]
    public void NewlyAppearedLiveAgent_CascadesExpansionUpToItsAlreadySeenSession()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-live", isLive: true)),
        }));
        Node(vm, "s-live").IsExpanded = false;

        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(
                RootPath,
                TelemetryFixtures.Session("s-live", isLive: true, agents: new[] { TelemetryFixtures.Agent("agent-new") })),
        }));

        // The cascade rule: a genuinely new live descendant re-opens its ancestors once, so the new
        // sub-agent isn't hidden under a collapsed parent.
        Assert.True(Node(vm, "s-live").IsExpanded);
    }

    [Fact]
    public void ExpandedKeyThatDisappears_IsSimplyDropped()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-gone")),
        }));
        Node(vm, "s-gone").IsExpanded = true;

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(RootPath) }));

        Assert.DoesNotContain("s-gone", Flatten(vm).Select(n => n.Key));
    }

    [Fact]
    public void Selection_IsPreservedByKeyAcrossARebuild()
    {
        var (vm, feed, _) = Build();
        var dto = TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-1"), TelemetryFixtures.Session("s-2")),
        });
        feed.Publish(dto);

        Node(vm, "s-2").IsSelected = true;
        Assert.Equal("s-2", vm.SelectedKey);

        feed.Publish(dto);

        Assert.Equal("s-2", vm.SelectedKey);
        Assert.True(Node(vm, "s-2").IsSelected);
        Assert.False(Node(vm, "s-1").IsSelected);
    }

    [Fact]
    public void Selection_IsClearedWhenTheSelectedNodeNoLongerExists()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-gone")),
        }));
        Node(vm, "s-gone").IsSelected = true;

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(RootPath) }));

        Assert.Null(vm.SelectedKey);
    }

    [Fact]
    public void SelectingTheKeylessPlaceholderRow_DoesNotBecomeASelectionKey()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(RootPath) }));

        vm.Roots[0].Children[0].IsSelected = true;

        Assert.Null(vm.SelectedKey);
    }

    [Fact]
    public void ConstructorPicksUpASnapshotTheFeedAlreadyHas()
    {
        var feed = new FakeTelemetryFeed
        {
            Latest = TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(RootPath) }),
        };

        var vm = new RootsPanelViewModel(feed, new RecordingUiThreadDispatcher());

        Assert.Single(vm.Roots);
        Assert.True(vm.HasSnapshot);
    }

    [Fact]
    public void SnapshotsFromTheFeedAreMarshalledThroughTheDispatcher()
    {
        var (vm, feed, dispatcher) = Build();
        dispatcher.RunInline = false;

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(RootPath) }));

        Assert.Empty(vm.Roots);          // not applied on the publishing thread...
        dispatcher.Drain();
        Assert.Single(vm.Roots);         // ...only once it reaches the UI thread.
    }

    [Fact]
    public void Start_StartsTheFeedAndNothingElse()
    {
        var (vm, feed, _) = Build();

        Assert.Equal(0, feed.StartCount);

        vm.Start();

        Assert.Equal(1, feed.StartCount);
    }

    [Fact]
    public void RefreshCommand_GoesThroughTheFeedsDebounceWindow()
    {
        var (vm, feed, _) = Build();

        vm.RefreshCommand.Execute(null);

        Assert.Equal(1, feed.RefreshRequestCount);
    }

    [Fact]
    public void CollapseAllCommand_CollapsesEveryNodeAndTheCollapseSurvivesARebuild()
    {
        var (vm, feed, _) = Build();
        var dto = TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(
                RootPath,
                TelemetryFixtures.Session("s-live", isLive: true, agents: new[] { TelemetryFixtures.Agent("a-1") })),
        });
        feed.Publish(dto);
        Assert.True(Node(vm, RootPath).IsExpanded);

        vm.CollapseAllCommand.Execute(null);
        Assert.All(Flatten(vm), n => Assert.False(n.IsExpanded));

        feed.Publish(dto);
        Assert.All(Flatten(vm), n => Assert.False(n.IsExpanded));
    }

    [Fact]
    public void Dispose_StopsApplyingFurtherSnapshots()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(RootPath) }));

        vm.Dispose();
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(RootPath), TelemetryFixtures.Root(OtherRootPath) }));

        Assert.Single(vm.Roots);
        Assert.False(feed.HasSnapshotSubscribers);
    }
}

namespace Accel.Tests;

using System.Linq;
using Accel.App.ViewModels;
using Accel.Cli;
using Accel.Metrics;
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

    /// <summary>P3-T1: panel A plus a real selection hub. The writer is what panel C would hold (tests
    /// stand in for it), and the panel itself only ever gets the read-only interface.</summary>
    private static (RootsPanelViewModel Vm, FakeTelemetryFeed Feed, Accel.App.Services.SessionSelectionService Selection, Accel.App.Services.ISessionSelectionWriter Writer) BuildWithSelection()
    {
        var feed = new FakeTelemetryFeed();
        var dispatcher = new RecordingUiThreadDispatcher();
        var selection = new Accel.App.Services.SessionSelectionService();
        var writer = selection.AcquireWriter();
        return (new RootsPanelViewModel(feed, dispatcher, selection: selection), feed, selection, writer);
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
        Assert.Contains("Refreshed at", vm.StatusText);
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
    public void SelectedRootPath_IsNull_WhenNothingIsSelected()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-1")) }));

        Assert.Null(vm.SelectedRootPath);
    }

    [Fact]
    public void SelectedRootPath_IsTheRootItselfWhenARootRowIsSelected()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-1")) }));

        vm.Roots[0].IsSelected = true;

        Assert.Equal(RootPath, vm.SelectedRootPath);
    }

    [Fact]
    public void SelectedRootPath_IsTheOwningRoot_WhenASessionRowIsSelected()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-1")),
            TelemetryFixtures.Root(OtherRootPath, TelemetryFixtures.Session("s-2")),
        }));

        Node(vm, "s-2").IsSelected = true;

        Assert.Equal(OtherRootPath, vm.SelectedRootPath);
    }

    [Fact]
    public void SelectedRootPath_IsTheOwningRoot_WhenAnAgentRowIsSelected()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-live", isLive: true, agents: new[] { TelemetryFixtures.Agent("agent-1") })),
        }));

        Node(vm, "agent-1").IsSelected = true;

        Assert.Equal(RootPath, vm.SelectedRootPath);
    }

    [Fact]
    public void SelectedRootPath_IsNull_WhenTheSelectedNodeNoLongerExists()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-gone")) }));
        Node(vm, "s-gone").IsSelected = true;

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(RootPath) }));

        Assert.Null(vm.SelectedRootPath);
    }

    [Fact]
    public void SessionNode_CarriesProjectDir_ForSessionRemoverPlan()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-1")) }));

        Assert.Equal("C--projects", Node(vm, "s-1").ProjectDir);
    }

    [Fact]
    public void RootPathFor_ResolvesAnySessionKey_RegardlessOfWhatIsCurrentlySelected()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-1")),
            TelemetryFixtures.Root(OtherRootPath, TelemetryFixtures.Session("s-2")),
        }));

        // Nothing is selected in the tree at all - RootPathFor must not depend on SelectedKey.
        Assert.Null(vm.SelectedKey);
        Assert.Equal(RootPath, vm.RootPathFor("s-1"));
        Assert.Equal(OtherRootPath, vm.RootPathFor("s-2"));
    }

    [Fact]
    public void RootPathFor_IsNull_ForAnUnknownKeyOrNull()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-1")) }));

        Assert.Null(vm.RootPathFor("no-such-key"));
        Assert.Null(vm.RootPathFor(null));
    }

    [Fact]
    public void FirstAvailableRootPath_ReturnsTheFirstRootThatExistsOnDisk()
    {
        var (vm, feed, _) = Build();
        var realDir = System.IO.Path.GetTempPath();
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(OtherRootPath), // doesn't exist on disk
            TelemetryFixtures.Root(realDir),
        }));

        Assert.Equal(realDir, vm.FirstAvailableRootPath);
    }

    [Fact]
    public void FirstAvailableRootPath_IsNull_WhenNoConfiguredRootExistsOnDisk()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(OtherRootPath) }));

        Assert.Null(vm.FirstAvailableRootPath);
    }

    [Fact]
    public void FirstAvailableRootPath_IsNull_WhenNoRootsAreConfigured()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.EmptyTree());

        Assert.Null(vm.FirstAvailableRootPath);
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

    // --- P1-T3b: root add/remove commands ---

    private static string NewFixtureConfigPath() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"accel-folders-vm-test-{Guid.NewGuid():N}.json");

    private static string NewFixtureFolderPath() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"accel-root-vm-test-{Guid.NewGuid():N}");

    [Fact]
    public void AddRootCommand_WhenUserPicksAFolder_PersistsItAndRefreshesViaTheFeed()
    {
        var feed = new FakeTelemetryFeed();
        var dispatcher = new RecordingUiThreadDispatcher();
        var picker = new FakeFolderPickerService();
        string configPath = NewFixtureConfigPath();
        string folder = NewFixtureFolderPath();
        picker.NextResult = folder;

        var vm = new RootsPanelViewModel(feed, dispatcher, folderPicker: picker, configPath: configPath);

        vm.AddRootCommand.Execute(null);

        Assert.True(System.IO.Directory.Exists(folder));
        Assert.Contains(folder, Accel.Server.RootFoldersConfig.LoadFull(new[] { configPath }).Roots);
        Assert.Equal(1, feed.RefreshRequestCount);

        System.IO.Directory.Delete(folder, recursive: true);
        System.IO.File.Delete(configPath);
    }

    [Fact]
    public void AddRootCommand_WhenUserCancelsTheDialog_DoesNothing()
    {
        var feed = new FakeTelemetryFeed();
        var dispatcher = new RecordingUiThreadDispatcher();
        var picker = new FakeFolderPickerService { NextResult = null }; // cancelled
        string configPath = NewFixtureConfigPath();

        var vm = new RootsPanelViewModel(feed, dispatcher, folderPicker: picker, configPath: configPath);

        vm.AddRootCommand.Execute(null);

        Assert.Equal(0, feed.RefreshRequestCount);
        Assert.False(System.IO.File.Exists(configPath));
    }

    [Fact]
    public void RemoveRootCommand_WhenConfirmed_DereferencesTheRootButNeverTouchesDisk()
    {
        var feed = new FakeTelemetryFeed();
        var dispatcher = new RecordingUiThreadDispatcher();
        var confirmation = new FakeUserConfirmationService { NextResult = true };
        string configPath = NewFixtureConfigPath();
        string folder = NewFixtureFolderPath();
        System.IO.Directory.CreateDirectory(folder);
        string fileInFolder = System.IO.Path.Combine(folder, "keep-me.txt");
        System.IO.File.WriteAllText(fileInFolder, "still here");
        Accel.App.Services.RootFolderEditor.AddRoot(configPath, folder);

        var vm = new RootsPanelViewModel(feed, dispatcher, confirmation: confirmation, configPath: configPath);
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(folder) }));
        var rootNode = vm.Roots[0];

        vm.RemoveRootCommand.Execute(rootNode);

        Assert.DoesNotContain(folder, Accel.Server.RootFoldersConfig.LoadFull(new[] { configPath }).Roots);
        Assert.Equal(1, feed.RefreshRequestCount);

        // The critical data-safety assertion: the folder and its contents are untouched on disk.
        Assert.True(System.IO.Directory.Exists(folder));
        Assert.True(System.IO.File.Exists(fileInFolder));
        Assert.Equal("still here", System.IO.File.ReadAllText(fileInFolder));

        System.IO.Directory.Delete(folder, recursive: true);
        System.IO.File.Delete(configPath);
    }

    [Fact]
    public void RemoveRootCommand_WhenUserDeclinesConfirmation_LeavesTheConfigUntouched()
    {
        var feed = new FakeTelemetryFeed();
        var dispatcher = new RecordingUiThreadDispatcher();
        var confirmation = new FakeUserConfirmationService { NextResult = false }; // declined
        string configPath = NewFixtureConfigPath();
        string folder = NewFixtureFolderPath();
        Accel.App.Services.RootFolderEditor.AddRoot(configPath, folder);

        var vm = new RootsPanelViewModel(feed, dispatcher, confirmation: confirmation, configPath: configPath);
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(folder) }));
        var rootNode = vm.Roots[0];

        vm.RemoveRootCommand.Execute(rootNode);

        Assert.Contains(folder, Accel.Server.RootFoldersConfig.LoadFull(new[] { configPath }).Roots);
        Assert.Equal(0, feed.RefreshRequestCount);

        System.IO.Directory.Delete(folder, recursive: true);
        System.IO.File.Delete(configPath);
    }

    [Fact]
    public void RemoveRootCommand_ConfirmationCopy_NeverContainsTheWordDelete()
    {
        var feed = new FakeTelemetryFeed();
        var dispatcher = new RecordingUiThreadDispatcher();
        var confirmation = new FakeUserConfirmationService { NextResult = false };
        string configPath = NewFixtureConfigPath();
        string folder = NewFixtureFolderPath();
        Accel.App.Services.RootFolderEditor.AddRoot(configPath, folder);

        var vm = new RootsPanelViewModel(feed, dispatcher, confirmation: confirmation, configPath: configPath);
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(folder) }));

        vm.RemoveRootCommand.Execute(vm.Roots[0]);

        Assert.NotNull(confirmation.LastMessage);
        Assert.DoesNotContain("delete", confirmation.LastMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(confirmation.LastTitle);
        Assert.DoesNotContain("delete", confirmation.LastTitle, StringComparison.OrdinalIgnoreCase);

        System.IO.Directory.Delete(folder, recursive: true);
        System.IO.File.Delete(configPath);
    }

    [Fact]
    public void RemoveRootCommand_ScopedAwayFromANonRootNode_DoesNothing()
    {
        var feed = new FakeTelemetryFeed();
        var dispatcher = new RecordingUiThreadDispatcher();
        var confirmation = new FakeUserConfirmationService { NextResult = true };
        string configPath = NewFixtureConfigPath();
        string folder = NewFixtureFolderPath();
        Accel.App.Services.RootFolderEditor.AddRoot(configPath, folder);

        var vm = new RootsPanelViewModel(feed, dispatcher, confirmation: confirmation, configPath: configPath);
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(folder, TelemetryFixtures.Session("session-1")),
        }));
        var sessionNode = vm.Roots[0].Children[0];

        vm.RemoveRootCommand.Execute(sessionNode);

        Assert.Equal(0, confirmation.CallCount);
        Assert.Contains(folder, Accel.Server.RootFoldersConfig.LoadFull(new[] { configPath }).Roots);

        System.IO.Directory.Delete(folder, recursive: true);
        System.IO.File.Delete(configPath);
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

    // --- P1-T4: model/effort badges, running/focused visual state, accessibility text ---

    [Fact]
    public void LiveSession_IsRunningTrue_HistoricalSession_IsRunningFalse()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-live", isLive: true), TelemetryFixtures.Session("s-old")),
        }));

        Assert.True(Node(vm, "s-live").IsRunning);
        Assert.False(Node(vm, "s-old").IsRunning);
    }

    // --- P3-T1: IsFocused is now driven by the real ISessionSelectionService (panel C writes it) ---

    [Fact]
    public void IsFocused_IsFalseForEveryRow_WhenNoSelectionServiceIsSupplied()
    {
        // The pre-P3 construction paths (and the WPF designer) pass no selection service; every row then
        // behaves exactly as it did while IsFocused was a hard-coded stub.
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-live", isLive: true)),
        }));

        Assert.False(Node(vm, "s-live").IsFocused);
    }

    [Fact]
    public void IsFocused_IsTrueOnlyForTheFocusedSessionRow()
    {
        var (vm, feed, selection, writer) = BuildWithSelection();
        writer.SetFocused("s-focused");

        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(
                RootPath,
                TelemetryFixtures.Session("s-focused", isLive: true),
                TelemetryFixtures.Session("s-other", isLive: true)),
        }));

        Assert.True(Node(vm, "s-focused").IsFocused);
        Assert.False(Node(vm, "s-other").IsFocused);
        Assert.False(Node(vm, RootPath).IsFocused); // a root row can never match a session id
        Assert.Equal("s-focused", selection.FocusedSessionId);
    }

    [Fact]
    public void IsFocused_FollowsALaterSelectionChange_WithoutARebuild()
    {
        var (vm, feed, _, writer) = BuildWithSelection();
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(
                RootPath,
                TelemetryFixtures.Session("s-1", isLive: true),
                TelemetryFixtures.Session("s-2", isLive: true)),
        }));

        var first = Node(vm, "s-1");
        var second = Node(vm, "s-2");

        writer.SetFocused("s-2");
        Assert.False(first.IsFocused);
        Assert.True(second.IsFocused);

        writer.SetFocused("s-1");
        Assert.True(first.IsFocused);
        Assert.False(second.IsFocused);

        writer.SetFocused(null);
        Assert.False(first.IsFocused);
        Assert.False(second.IsFocused);
    }

    [Fact]
    public void FocusChange_UpdatesTheVisualStateAndAccessibilityText_AndRaisesNotifications()
    {
        var (vm, feed, _, writer) = BuildWithSelection();
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-1", isLive: true)),
        }));

        var node = Node(vm, "s-1");
        var changed = new System.Collections.Generic.List<string?>();
        node.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        writer.SetFocused("s-1");

        Assert.Equal(SessionVisualStateResolver.Resolve(isRunning: true, isFocused: true), node.VisualState);
        Assert.Contains("focused", node.AutomationDescription, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(RootsPanelNodeViewModel.IsFocused), changed);
        Assert.Contains(nameof(RootsPanelNodeViewModel.VisualState), changed);
        Assert.Contains(nameof(RootsPanelNodeViewModel.AutomationDescription), changed);
    }

    [Fact]
    public void FocusSurvivesARebuild_BecauseFreshNodesAreReprojectedFromTheService()
    {
        var (vm, feed, _, writer) = BuildWithSelection();
        writer.SetFocused("s-1");
        var tree = TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-1", isLive: true)),
        });

        feed.Publish(tree);
        feed.Publish(tree); // second snapshot -> brand-new node instances

        Assert.True(Node(vm, "s-1").IsFocused);
    }

    [Fact]
    public void FocusIsCaseInsensitive_BecauseTabIdsAndTranscriptIdsNeedNotAgreeOnHexCasing()
    {
        var (vm, feed, _, writer) = BuildWithSelection();
        writer.SetFocused("5604B0D8-AAAA-BBBB-CCCC-DDDDDDDDDDDD");

        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("5604b0d8-aaaa-bbbb-cccc-dddddddddddd", isLive: true)),
        }));

        Assert.True(Node(vm, "5604b0d8-aaaa-bbbb-cccc-dddddddddddd").IsFocused);
    }

    [Fact]
    public void Dispose_StopsApplyingFurtherFocusChanges()
    {
        var (vm, feed, _, writer) = BuildWithSelection();
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-1", isLive: true)),
        }));
        var node = Node(vm, "s-1");

        vm.Dispose();
        writer.SetFocused("s-1");

        Assert.False(node.IsFocused);
    }

    [Fact]
    public void PanelA_OnlyEverReadsTheSelection_ItIsHandedTheReadOnlyInterface()
    {
        // The constructor parameter's type is the read interface, which has no mutator at all (see
        // SessionSelectionServiceTests) - panel A structurally cannot steal panel C's write role.
        var parameter = typeof(RootsPanelViewModel)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Single(p => p.Name == "selection");

        Assert.Equal(typeof(Accel.App.Services.ISessionSelectionService), parameter.ParameterType);
    }

    [Fact]
    public void SessionNode_ShowsAModelBadge_MatchingTheSonnetFixtureModelId()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-1", isLive: true)),
        }));

        var node = Node(vm, "s-1");
        Assert.True(node.ShowModelBadge);
        Assert.Equal("S", node.ModelBadge.Letter); // TelemetryFixtures.Session defaults ModelId to "claude-sonnet-5"
        Assert.True(node.ModelBadge.Matched);
    }

    [Fact]
    public void SessionNode_ShowsEffortBars_MatchingTheMediumFixtureEffort()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-1", isLive: true)),
        }));

        var node = Node(vm, "s-1");
        Assert.True(node.ShowEffortBars);
        Assert.Equal(2, node.EffortLevel); // TelemetryFixtures.Session defaults EffortLevel to "medium"
    }

    [Fact]
    public void RootAndPlaceholderNodes_NeverShowModelOrEffortBadges()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(RootPath) }));

        var rootNode = vm.Roots[0];
        var placeholderNode = rootNode.Children[0];

        Assert.False(rootNode.ShowModelBadge);
        Assert.False(rootNode.ShowEffortBars);
        Assert.False(placeholderNode.ShowModelBadge);
        Assert.False(placeholderNode.ShowEffortBars);
    }

    [Fact]
    public void AutomationDescription_MentionsRunningForALiveSession_AndIdleForAHistoricalOne()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-live", isLive: true), TelemetryFixtures.Session("s-old")),
        }));

        Assert.Contains("Running", Node(vm, "s-live").AutomationDescription);
        Assert.Contains("Idle", Node(vm, "s-old").AutomationDescription);
    }

    [Fact]
    public void TooltipText_IncludesSessionIdAndContextSummary()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-1", isLive: true)),
        }));

        var node = Node(vm, "s-1");
        Assert.Contains(node.Columns.Id, node.TooltipText);
        Assert.Contains(node.Columns.Context, node.TooltipText);
    }

    [Fact]
    public void Rebuild_SessionNode_ExposesDurationAndConsumedTokens()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-1", isLive: true, durationMs: 424_000, consumedTokens: 148_223)),
        }));

        var node = Node(vm, "s-1");

        Assert.Equal(424_000, node.DurationMs);
        Assert.Equal(148_223, node.ConsumedTokens);
        Assert.Equal("7m 04s", node.DurationText);
        Assert.Equal("148.2K", node.TokensText);
    }

    [Fact]
    public void TooltipText_WithDurationAndTokens_AppendsThem()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-1", isLive: true, durationMs: 424_000, consumedTokens: 148_223)),
        }));

        var node = Node(vm, "s-1");

        Assert.EndsWith($" — {node.Columns.Duration} — {node.Columns.Tokens}", node.TooltipText);
    }

    [Fact]
    public void TooltipText_WithoutDurationAndTokens_IsUnchanged()
    {
        // Pins backward compatibility: a row with no duration/tokens data (the ordinary case for
        // most existing fixtures/tests) must produce the exact same tooltip string it always has -
        // claude-agentgraph.md section 6.6's explicit compatibility guarantee.
        var (vmBaseline, feedBaseline, _) = Build();
        feedBaseline.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-1", isLive: true)),
        }));
        string baselineTooltip = Node(vmBaseline, "s-1").TooltipText;

        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootPath, TelemetryFixtures.Session("s-1", isLive: true, durationMs: null, consumedTokens: null)),
        }));
        string tooltip = Node(vm, "s-1").TooltipText;

        Assert.Equal(baselineTooltip, tooltip);
    }

    [Fact]
    public void AgentNode_AlsoResolvesModelBadgeAndEffortBars()
    {
        var (vm, feed, _) = Build();
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(
                RootPath,
                TelemetryFixtures.Session("s-live", isLive: true, agents: new[] { TelemetryFixtures.Agent("agent-1") })),
        }));

        var agentNode = Node(vm, "agent-1");
        Assert.True(agentNode.ShowModelBadge);
        Assert.Equal("S", agentNode.ModelBadge.Letter); // TelemetryFixtures.Agent defaults ModelId to "claude-sonnet-5"
        Assert.True(agentNode.ShowEffortBars);
        Assert.Equal(2, agentNode.EffortLevel); // TelemetryFixtures.Agent defaults EffortLevel to "medium"
    }
}

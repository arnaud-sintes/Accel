namespace Accel.Tests;

using System;
using System.IO;
using System.Linq;
using Accel.App.Services;
using Accel.App.ViewModels;
using Xunit;

/// <summary>Fake <see cref="IFilesEntryDialogService"/>: returns fixed strings (or null, for
/// "cancelled") instead of showing a real dialog - same role
/// <see cref="RecordingUiThreadDispatcher"/>/<see cref="FakeTelemetryFeed"/> play elsewhere in this
/// file.</summary>
internal sealed class FakeFilesEntryDialogService : IFilesEntryDialogService
{
    public string? NewEntryName { get; set; }
    public string? MoveDestination { get; set; }

    public string? PromptForNewEntryName(NewFileSystemEntryKind kind, string parentDirectoryPath) => NewEntryName;
    public string? PromptForMoveDestination(string currentFullPath, bool isDirectory) => MoveDestination;
}

/// <summary>
/// Unit tests for panel B's <see cref="FilesPanelViewModel"/> - driven exactly like
/// <see cref="AgentGraphViewModelTests"/> (<see cref="FakeTelemetryFeed"/> +
/// <see cref="RecordingUiThreadDispatcher"/>, a real <see cref="SessionSelectionService"/> with its
/// writer acquired in-test). Real temporary directories stand in for the focused folder, since
/// <see cref="Accel.Cli.FilesTreeBuilder"/> is genuine filesystem I/O, not mockable telemetry.
/// </summary>
public sealed class FilesPanelViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "accel-files-panel-tests-" + Guid.NewGuid().ToString("N"));

    public FilesPanelViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup only.
        }
    }

    private static (FilesPanelViewModel Vm, FakeTelemetryFeed Feed, SessionSelectionService Selection, ISessionSelectionWriter Writer) Build(
        RootsPanelViewModel? rootsPanel = null)
    {
        var feed = new FakeTelemetryFeed();
        var dispatcher = new RecordingUiThreadDispatcher();
        var selection = new SessionSelectionService();
        var writer = selection.AcquireWriter();
        return (new FilesPanelViewModel(feed, dispatcher, selection, rootsPanel), feed, selection, writer);
    }

    private static (FilesPanelViewModel Vm, FakeTelemetryFeed Feed, ISessionSelectionWriter Writer, FakeFilesEntryDialogService Dialogs, FakeFilesEntryConfirmationService Confirmation) BuildWithExplorerFakes()
    {
        var feed = new FakeTelemetryFeed();
        var dispatcher = new RecordingUiThreadDispatcher();
        var selection = new SessionSelectionService();
        var writer = selection.AcquireWriter();
        var dialogs = new FakeFilesEntryDialogService();
        var confirmation = new FakeFilesEntryConfirmationService();
        var vm = new FilesPanelViewModel(feed, dispatcher, selection, null, dialogs, confirmation);
        return (vm, feed, writer, dialogs, confirmation);
    }

    private static (FilesPanelViewModel Vm, FakeTelemetryFeed Feed, ISessionSelectionWriter Writer, FakeDirectoryWatcher Watcher) BuildWithWatcher()
    {
        var feed = new FakeTelemetryFeed();
        var dispatcher = new RecordingUiThreadDispatcher();
        var selection = new SessionSelectionService();
        var writer = selection.AcquireWriter();
        var watcher = new FakeDirectoryWatcher();
        var vm = new FilesPanelViewModel(feed, dispatcher, selection, null, null, null, watcher);
        return (vm, feed, writer, watcher);
    }

    private static void FocusRoot(FilesPanelViewModel vm, FakeTelemetryFeed feed, ISessionSelectionWriter writer, string root)
    {
        var session = TelemetryFixtures.Session("session-1", isLive: true) with { Cwd = root };
        writer.SetFocused("session-1");
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(root, session) }));
    }

    [Fact]
    public void NothingFocused_ShowsHintAndNoNodes()
    {
        var (vm, _, _, _) = Build();

        Assert.False(vm.HasTree);
        Assert.Empty(vm.Nodes);
        Assert.Equal("No folder or session focused.", vm.StatusText);
    }

    [Fact]
    public void FocusedSession_BuildsTreeFromItsCwd()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "readme.txt"), string.Empty);

        var (vm, feed, _, writer) = Build();
        var session = TelemetryFixtures.Session("session-1", isLive: true) with { Cwd = _root };
        writer.SetFocused("session-1");

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(_root, session) }));

        Assert.True(vm.HasTree);
        Assert.Equal(_root, vm.StatusText);
        Assert.Equal(new[] { "src", "readme.txt" }, vm.Nodes.Select(n => n.Name));
    }

    [Fact]
    public void FocusedSession_MissingCwdOnDisk_ShowsNotFound()
    {
        string missing = Path.Combine(_root, "gone");

        var (vm, feed, _, writer) = Build();
        var session = TelemetryFixtures.Session("session-1", isLive: true) with { Cwd = missing };
        writer.SetFocused("session-1");

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(_root, session) }));

        Assert.False(vm.HasTree);
        Assert.Equal($"Folder not found: {missing}", vm.StatusText);
    }

    [Fact]
    public void NoFocusedSession_FallsBackToRootsPanelSelection()
    {
        Directory.CreateDirectory(Path.Combine(_root, "child"));

        var feed = new FakeTelemetryFeed();
        var dispatcher = new RecordingUiThreadDispatcher();
        var rootsPanel = new RootsPanelViewModel(feed, dispatcher);
        var (vm, filesFeed, _, _) = Build(rootsPanel);

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(_root) }));
        filesFeed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(_root) }));

        rootsPanel.Roots.Single().IsSelected = true;

        Assert.True(vm.HasTree);
        Assert.Equal(_root, vm.StatusText);
        Assert.Equal("child", vm.Nodes.Single().Name);
    }

    [Fact]
    public void FocusedSessionTakesPriorityOverRootsPanelSelection()
    {
        string sessionCwd = Path.Combine(_root, "session-cwd");
        Directory.CreateDirectory(sessionCwd);
        Directory.CreateDirectory(Path.Combine(_root, "other"));

        var feed = new FakeTelemetryFeed();
        var dispatcher = new RecordingUiThreadDispatcher();
        var rootsPanel = new RootsPanelViewModel(feed, dispatcher);
        var (vm, filesFeed, _, writer) = Build(rootsPanel);

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(_root) }));
        rootsPanel.Roots.Single().IsSelected = true;

        var session = TelemetryFixtures.Session("session-1", isLive: true) with { Cwd = sessionCwd };
        writer.SetFocused("session-1");
        filesFeed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(_root, session) }));

        Assert.Equal($"{sessionCwd} (empty)", vm.StatusText);
    }

    [Fact]
    public void FolderNode_Expand_LazilyLoadsItsOwnChildren()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "child"));
        File.WriteAllText(Path.Combine(nested.FullName, "leaf.txt"), string.Empty);

        var (vm, feed, _, writer) = Build();
        var session = TelemetryFixtures.Session("session-1", isLive: true) with { Cwd = _root };
        writer.SetFocused("session-1");
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(_root, session) }));

        var node = vm.Nodes.Single();
        Assert.True(node.IsDirectory);
        Assert.False(node.IsExpanded);

        // Before expanding: only the sentinel placeholder is present (so the TreeView shows an
        // expand arrow) - the real "leaf.txt" grandchild has not been loaded yet.
        Assert.Single(node.Children);
        Assert.DoesNotContain(node.Children, c => c.Name == "leaf.txt");

        node.IsExpanded = true;

        Assert.Single(node.Children);
        Assert.Equal("leaf.txt", node.Children.Single().Name);
    }

    [Fact]
    public void FolderNode_WithNoEntriesOnDisk_HasNoPlaceholderChild()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty-child"));

        var (vm, feed, _, writer) = Build();
        var session = TelemetryFixtures.Session("session-1", isLive: true) with { Cwd = _root };
        writer.SetFocused("session-1");
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(_root, session) }));

        var node = vm.Nodes.Single();
        Assert.Empty(node.Children);
    }

    [Fact]
    public void RepublishingTheSameFocusedFolder_DoesNotCollapseAnAlreadyExpandedNode()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "child"));
        File.WriteAllText(Path.Combine(nested.FullName, "leaf.txt"), string.Empty);

        var (vm, feed, _, writer) = Build();
        var session = TelemetryFixtures.Session("session-1", isLive: true) with { Cwd = _root };
        writer.SetFocused("session-1");
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(_root, session) }));

        var node = vm.Nodes.Single();
        node.IsExpanded = true;
        Assert.Equal("leaf.txt", node.Children.Single().Name);

        // Same session, same cwd - simulates an unrelated telemetry tick (e.g. another root's own
        // session activity) that resolves to the identical focused folder. A full Nodes.Clear() +
        // rebuild on every such tick used to silently collapse this node moments after expanding it.
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(_root, session) }));

        var sameNode = vm.Nodes.Single();
        Assert.Same(node, sameNode);
        Assert.True(sameNode.IsExpanded);
        Assert.Equal("leaf.txt", sameNode.Children.Single().Name);
    }

    [Fact]
    public void ExpandingAFolder_RaisesFolderExpandedWithItsPath()
    {
        Directory.CreateDirectory(Path.Combine(_root, "child"));

        var (vm, feed, _, writer) = Build();
        var session = TelemetryFixtures.Session("session-1", isLive: true) with { Cwd = _root };
        writer.SetFocused("session-1");
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(_root, session) }));

        string? raised = null;
        vm.FolderExpanded += path => raised = path;

        vm.Nodes.Single().IsExpanded = true;

        Assert.Equal(Path.Combine(_root, "child"), raised);
    }

    [Fact]
    public async System.Threading.Tasks.Task NewFileCommand_CreatesFileOnDisk_AndRequestsRefresh()
    {
        var (vm, feed, writer, dialogs, _) = BuildWithExplorerFakes();
        FocusRoot(vm, feed, writer, _root);
        dialogs.NewEntryName = "new.txt";

        await vm.NewFileCommand.ExecuteAsync(null);

        Assert.True(File.Exists(Path.Combine(_root, "new.txt")));
        Assert.True(feed.RefreshRequestCount > 0);
    }

    [Fact]
    public async System.Threading.Tasks.Task NewFolderCommand_OnDirectoryNode_CreatesInsideThatDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "child"));
        var (vm, feed, writer, dialogs, _) = BuildWithExplorerFakes();
        FocusRoot(vm, feed, writer, _root);
        var childNode = vm.Nodes.Single(n => n.Name == "child");
        dialogs.NewEntryName = "nested";

        await vm.NewFolderCommand.ExecuteAsync(childNode);

        Assert.True(Directory.Exists(Path.Combine(_root, "child", "nested")));
    }

    [Fact]
    public async System.Threading.Tasks.Task NewFileCommand_CancelledDialog_IsANoOp()
    {
        var (vm, feed, writer, dialogs, _) = BuildWithExplorerFakes();
        FocusRoot(vm, feed, writer, _root);
        dialogs.NewEntryName = null;

        await vm.NewFileCommand.ExecuteAsync(null);

        Assert.Empty(Directory.GetFileSystemEntries(_root));
        Assert.False(feed.RefreshRequestCount > 0);
    }

    [Fact]
    public async System.Threading.Tasks.Task DeleteCommand_ConfirmedOnFile_RemovesItAndRaisesEntryRemovedOrMoved()
    {
        string target = Path.Combine(_root, "doomed.txt");
        File.WriteAllText(target, "bye");
        var (vm, feed, writer, dialogs, confirmation) = BuildWithExplorerFakes();
        FocusRoot(vm, feed, writer, _root);
        var node = vm.Nodes.Single(n => n.Name == "doomed.txt");
        confirmation.ConfirmDeleteResult = true;

        (string Path, bool WasDirectory)? raised = null;
        vm.EntryRemovedOrMoved += (path, wasDirectory) => raised = (path, wasDirectory);

        await vm.DeleteCommand.ExecuteAsync(node);

        Assert.False(File.Exists(target));
        Assert.Equal((target, false), raised);
        Assert.True(feed.RefreshRequestCount > 0);
    }

    [Fact]
    public async System.Threading.Tasks.Task DeleteCommand_DeclinedConfirmation_IsANoOp()
    {
        string target = Path.Combine(_root, "spared.txt");
        File.WriteAllText(target, "hi");
        var (vm, feed, writer, dialogs, confirmation) = BuildWithExplorerFakes();
        FocusRoot(vm, feed, writer, _root);
        var node = vm.Nodes.Single(n => n.Name == "spared.txt");
        confirmation.ConfirmDeleteResult = false;

        await vm.DeleteCommand.ExecuteAsync(node);

        Assert.True(File.Exists(target));
        Assert.False(feed.RefreshRequestCount > 0);
    }

    // -------------------------------------------------------------------------------------------
    // Contents-changed refresh (Refresh / IDirectoryWatcher). Distinct from every test above, which
    // exercises the focus-changed path (Rebuild): these assert that the tree tracks the *same*
    // folder's contents changing underneath it - by an agent, another editor, or this panel's own
    // explorer commands - which the focus-change path deliberately cannot do.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Refresh_PicksUpCreatedAndRemovedEntries()
    {
        File.WriteAllText(Path.Combine(_root, "before.txt"), "x");
        var (vm, feed, _, writer) = Build();
        FocusRoot(vm, feed, writer, _root);
        Assert.Equal(new[] { "before.txt" }, vm.Nodes.Select(n => n.Name).ToArray());

        // Exactly what a Claude Code session working in this folder does: one file appears, one goes.
        File.Delete(Path.Combine(_root, "before.txt"));
        Directory.CreateDirectory(Path.Combine(_root, "added-dir"));
        File.WriteAllText(Path.Combine(_root, "added.txt"), "y");

        vm.Refresh();

        Assert.Equal(new[] { "added-dir", "added.txt" }, vm.Nodes.Select(n => n.Name).ToArray());
    }

    [Fact]
    public void Refresh_KeepsExpandedFoldersExpanded_AndTheirLoadedChildren()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "a.cs"), "x");
        var (vm, feed, _, writer) = Build();
        FocusRoot(vm, feed, writer, _root);

        var src = vm.Nodes.Single(n => n.Name == "src");
        src.IsExpanded = true;
        Assert.Equal(new[] { "a.cs" }, src.Children.Select(n => n.Name).ToArray());

        File.WriteAllText(Path.Combine(_root, "src", "b.cs"), "y");
        File.WriteAllText(Path.Combine(_root, "top.txt"), "z");

        vm.Refresh();

        // Same node instance, still expanded - a clear-and-rebuild would have snapped it shut.
        Assert.Same(src, vm.Nodes.Single(n => n.Name == "src"));
        Assert.True(src.IsExpanded);
        Assert.Equal(new[] { "a.cs", "b.cs" }, src.Children.Select(n => n.Name).ToArray());
        Assert.Equal(new[] { "src", "top.txt" }, vm.Nodes.Select(n => n.Name).ToArray());
    }

    [Fact]
    public void Refresh_DoesNotLoadChildrenOfANeverExpandedFolder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "untouched"));
        File.WriteAllText(Path.Combine(_root, "untouched", "deep.txt"), "x");
        var (vm, feed, _, writer) = Build();
        FocusRoot(vm, feed, writer, _root);

        vm.Refresh();

        var folder = vm.Nodes.Single(n => n.Name == "untouched");
        Assert.False(folder.ChildrenLoaded);
        // Still just the expand-arrow placeholder: a refresh must not walk folders the user never
        // opened, or it would enumerate the whole tree on every agent file write.
        Assert.Single(folder.Children);
        Assert.Equal(string.Empty, folder.Children[0].Key);
    }

    [Fact]
    public void Refresh_FolderThatBecameNonEmpty_GainsItsExpandArrow()
    {
        string folder = Path.Combine(_root, "empty");
        Directory.CreateDirectory(folder);
        var (vm, feed, _, writer) = Build();
        FocusRoot(vm, feed, writer, _root);
        Assert.Empty(vm.Nodes.Single(n => n.Name == "empty").Children);

        File.WriteAllText(Path.Combine(folder, "appeared.txt"), "x");
        vm.Refresh();

        Assert.Single(vm.Nodes.Single(n => n.Name == "empty").Children);
    }

    [Fact]
    public void Refresh_FolderThatBecameEmpty_LosesItsExpandArrow()
    {
        string folder = Path.Combine(_root, "full");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "doomed.txt"), "x");
        var (vm, feed, _, writer) = Build();
        FocusRoot(vm, feed, writer, _root);
        Assert.Single(vm.Nodes.Single(n => n.Name == "full").Children);

        File.Delete(Path.Combine(folder, "doomed.txt"));
        vm.Refresh();

        Assert.Empty(vm.Nodes.Single(n => n.Name == "full").Children);
    }

    [Fact]
    public void Refresh_CaseOnlyRename_ShowsTheNewName()
    {
        File.WriteAllText(Path.Combine(_root, "readme.md"), "x");
        var (vm, feed, _, writer) = Build();
        FocusRoot(vm, feed, writer, _root);

        File.Move(Path.Combine(_root, "readme.md"), Path.Combine(_root, "README.md"));
        vm.Refresh();

        Assert.Equal(new[] { "README.md" }, vm.Nodes.Select(n => n.Name).ToArray());
    }

    [Fact]
    public void Refresh_RootItselfDeleted_ShowsFolderNotFound()
    {
        string root = Path.Combine(_root, "doomed-root");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "x");
        var (vm, feed, _, writer) = Build();
        FocusRoot(vm, feed, writer, root);
        Assert.True(vm.HasTree);

        Directory.Delete(root, recursive: true);
        vm.Refresh();

        Assert.False(vm.HasTree);
        Assert.Empty(vm.Nodes);
        Assert.Equal($"Folder not found: {root}", vm.StatusText);
    }

    [Fact]
    public void Refresh_NothingFocused_IsANoOp()
    {
        var (vm, _, _, _) = Build();

        vm.Refresh();

        Assert.False(vm.HasTree);
        Assert.Equal("No folder or session focused.", vm.StatusText);
    }

    [Fact]
    public void Refresh_ExpandedFolderDeleted_ReportsItAsCollapsed()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "a.cs"), "x");
        var (vm, feed, _, writer) = Build();
        FocusRoot(vm, feed, writer, _root);
        vm.Nodes.Single(n => n.Name == "src").IsExpanded = true;

        (string Collapsed, string? Ancestor)? reported = null;
        vm.FolderCollapsed += (collapsed, ancestor) => reported = (collapsed, ancestor);

        Directory.Delete(Path.Combine(_root, "src"), recursive: true);
        vm.Refresh();

        // The git section follows the expanded folder, so a folder deleted underneath it has to be
        // reported exactly like one the user closed - otherwise it keeps showing a folder that is gone.
        Assert.Equal((Path.Combine(_root, "src"), (string?)null), reported);
    }

    [Fact]
    public void Refresh_ReAppliesTheActiveSearchFilter()
    {
        File.WriteAllText(Path.Combine(_root, "match-me.txt"), "x");
        var (vm, feed, _, writer) = Build();
        FocusRoot(vm, feed, writer, _root);
        vm.SearchText = "match";

        File.WriteAllText(Path.Combine(_root, "other.txt"), "y");
        File.WriteAllText(Path.Combine(_root, "match-too.txt"), "z");
        vm.Refresh();

        Assert.True(vm.Nodes.Single(n => n.Name == "match-me.txt").IsVisible);
        Assert.True(vm.Nodes.Single(n => n.Name == "match-too.txt").IsVisible);
        Assert.False(vm.Nodes.Single(n => n.Name == "other.txt").IsVisible);
    }

    [Fact]
    public void WatcherChanged_RefreshesTheTree()
    {
        var (vm, feed, writer, watcher) = BuildWithWatcher();
        FocusRoot(vm, feed, writer, _root);
        Assert.Empty(vm.Nodes);

        File.WriteAllText(Path.Combine(_root, "from-an-agent.txt"), "x");
        watcher.RaiseChanged();

        Assert.Equal(new[] { "from-an-agent.txt" }, vm.Nodes.Select(n => n.Name).ToArray());
    }

    [Fact]
    public void FocusChange_PointsTheWatcherAtTheResolvedRoot()
    {
        string other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        var (vm, feed, writer, watcher) = BuildWithWatcher();

        FocusRoot(vm, feed, writer, _root);
        Assert.Equal(_root, watcher.WatchedPath);

        FocusRoot(vm, feed, writer, other);
        Assert.Equal(other, watcher.WatchedPath);
    }

    [Fact]
    public void Dispose_DisposesTheWatcher()
    {
        var (vm, _, _, watcher) = BuildWithWatcher();

        vm.Dispose();

        Assert.True(watcher.Disposed);
    }

    [Fact]
    public async System.Threading.Tasks.Task NewFileCommand_ShowsTheNewFileWithoutWaitingForTelemetry()
    {
        var (vm, feed, writer, dialogs, _) = BuildWithExplorerFakes();
        FocusRoot(vm, feed, writer, _root);
        dialogs.NewEntryName = "new.txt";

        await vm.NewFileCommand.ExecuteAsync(null);

        // No feed.Publish here on purpose: RequestRefresh lands in Rebuild, whose same-root fast path
        // is a no-op, so the command has to refresh this tree itself.
        Assert.Equal(new[] { "new.txt" }, vm.Nodes.Select(n => n.Name).ToArray());
    }

    [Fact]
    public async System.Threading.Tasks.Task DeleteCommand_RemovesTheRowWithoutWaitingForTelemetry()
    {
        File.WriteAllText(Path.Combine(_root, "doomed.txt"), "bye");
        var (vm, feed, writer, _, confirmation) = BuildWithExplorerFakes();
        FocusRoot(vm, feed, writer, _root);
        confirmation.ConfirmDeleteResult = true;

        await vm.DeleteCommand.ExecuteAsync(vm.Nodes.Single(n => n.Name == "doomed.txt"));

        Assert.Empty(vm.Nodes);
    }

    [Fact]
    public async System.Threading.Tasks.Task MoveRenameCommand_ShowsTheNewNameWithoutWaitingForTelemetry()
    {
        File.WriteAllText(Path.Combine(_root, "old.txt"), "x");
        var (vm, feed, writer, dialogs, _) = BuildWithExplorerFakes();
        FocusRoot(vm, feed, writer, _root);
        dialogs.MoveDestination = Path.Combine(_root, "new.txt");

        await vm.MoveRenameCommand.ExecuteAsync(vm.Nodes.Single(n => n.Name == "old.txt"));

        Assert.Equal(new[] { "new.txt" }, vm.Nodes.Select(n => n.Name).ToArray());
    }
}

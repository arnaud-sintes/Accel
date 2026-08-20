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
}

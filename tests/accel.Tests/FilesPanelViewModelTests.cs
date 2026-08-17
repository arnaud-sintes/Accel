namespace Accel.Tests;

using System;
using System.IO;
using System.Linq;
using Accel.App.Services;
using Accel.App.ViewModels;
using Xunit;

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
}

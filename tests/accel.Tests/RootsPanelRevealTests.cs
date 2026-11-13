namespace Accel.Tests;

using System.Collections.Generic;
using System.Linq;
using Accel.App.Services;
using Accel.App.ViewModels;
using Xunit;

/// <summary>
/// The view-model half of "selecting a tab scrolls panel A to its folder". Selecting a TreeViewItem never scrolls it
/// (WPF only scrolls an item that takes keyboard focus, and a tab click leaves focus in the tab strip), so the
/// panel asks the view explicitly: <see cref="RootsPanelViewModel.RevealRequested"/>.
/// </summary>
public class RootsPanelRevealTests
{
    private const string RootA = @"C:\projects\a";
    private const string RootB = @"C:\projects\b";

    private static (RootsPanelViewModel Vm, FakeTelemetryFeed Feed, ISessionSelectionWriter Writer, List<string> Reveals) Build()
    {
        var feed = new FakeTelemetryFeed();
        var selection = new SessionSelectionService();
        var writer = selection.AcquireWriter();
        var vm = new RootsPanelViewModel(feed, new RecordingUiThreadDispatcher(), selection: selection);
        var reveals = new List<string>();
        vm.RevealRequested += reveals.Add;
        return (vm, feed, writer, reveals);
    }

    private static void Publish(FakeTelemetryFeed feed, params string[] sessionIds) =>
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootA, sessionIds.Select(id => TelemetryFixtures.Session(id)).ToArray()),
            TelemetryFixtures.Root(RootB),
        }));

    [Fact]
    public void RevealSession_RaisesTheRequest_EvenWhenTheRowIsAlreadySelected()
    {
        var (vm, feed, writer, reveals) = Build();
        Publish(feed, "s-1", "s-2");
        writer.SetFocused("s-2");
        Assert.True(vm.Roots.SelectMany(r => r.Children).Single(n => n.Key == "s-2").IsSelected);

        // Selecting it again changes nothing (so no property change would ever scroll it) - the request is explicit.
        vm.RevealSession("s-2");
        vm.RevealSession("s-2");

        Assert.Equal(new[] { "s-2", "s-2" }, reveals);
    }

    [Fact]
    public void RevealSession_OpensACollapsedProject_BeforeAskingTheViewToScroll()
    {
        var (vm, feed, _, reveals) = Build();
        Publish(feed, "s-1");
        var root = vm.Roots.Single(r => r.Key == RootA);
        root.IsExpanded = false;
        bool wasOpenWhenRaised = false;
        vm.RevealRequested += _ => wasOpenWhenRaised = root.IsExpanded;

        vm.RevealSession("s-1");

        Assert.True(root.IsExpanded);
        Assert.True(wasOpenWhenRaised);
        Assert.Equal(new[] { "s-1" }, reveals);
    }

    [Fact]
    public void RevealRoot_OpensACollapsedProject()
    {
        var (vm, feed, _, reveals) = Build();
        Publish(feed, "s-1");
        var root = vm.Roots.Single(r => r.Key == RootB);
        root.IsExpanded = false;

        vm.RevealRoot(@"C:\projects\b\src\x.cs");

        Assert.True(root.IsExpanded);
        Assert.Equal(new[] { RootB }, reveals);
    }

    [Fact]
    public void ARevealForARowHiddenByTheSearchFilter_LeavesACollapsedProjectCollapsed()
    {
        var (vm, feed, _, _) = Build();
        Publish(feed, "s-1");
        vm.SearchText = "no-such-text-anywhere";
        var root = vm.Roots.Single(r => r.Key == RootA);
        root.IsExpanded = false;

        vm.RevealSession("s-1");

        Assert.False(root.IsExpanded);
    }

    [Fact]
    public void RevealSession_IsCaseInsensitive_AndUsesTheNodesOwnKey()
    {
        var (vm, feed, _, reveals) = Build();
        Publish(feed, "Session-One");

        vm.RevealSession("session-one");

        Assert.Equal(new[] { "Session-One" }, reveals);
    }

    [Fact]
    public void ASessionNotYetListed_IsRevealedByTheRebuildThatListsIt()
    {
        var (vm, feed, _, reveals) = Build();
        Publish(feed, "s-1");
        var rebuilt = new List<string?>();
        vm.TreeRebuilt += rebuilt.Add;

        vm.RevealSession("s-new");
        Assert.Empty(reveals);

        Publish(feed, "s-1");          // identical to the last tick: nothing is rebuilt, nothing to reveal
        Publish(feed, "s-1", "s-other"); // changed, but the awaited row is still not there
        Publish(feed, "s-1", "s-other", "s-new"); // listed now

        Assert.Equal(new string?[] { null, "s-new" }, rebuilt);
    }

    [Fact]
    public void ARevealForARowHiddenByTheSearchFilter_DoesNothing()
    {
        var (vm, feed, _, reveals) = Build();
        Publish(feed, "s-1", "s-2");
        vm.SearchText = "no-such-text-anywhere";
        Assert.All(vm.Roots.SelectMany(r => r.Children), n => Assert.False(n.IsVisible));

        vm.RevealSession("s-1");
        vm.RevealRoot(@"C:\projects\a\src\x.cs");

        Assert.Empty(reveals);
    }

    [Theory]
    [InlineData(@"C:\projects\a", RootA)]                          // a terminal opened in the project folder itself
    [InlineData(@"C:\projects\a\src\deep\File.cs", RootA)]         // a file anywhere below it
    [InlineData("C:/projects/b/readme.md", RootB)]                 // forward slashes
    public void RevealRoot_ResolvesTheHostingRoot(string path, string expectedRootKey)
    {
        var (vm, feed, _, reveals) = Build();
        Publish(feed, "s-1");

        vm.RevealRoot(path);

        Assert.Equal(new[] { expectedRootKey }, reveals);
    }

    [Theory]
    [InlineData(@"E:\elsewhere\x.cs")]
    [InlineData(@"C:\projects\a-other\x.cs")]
    [InlineData(null)]
    [InlineData("")]
    public void RevealRoot_DoesNothing_ForAPathNoRootContains(string? path)
    {
        var (vm, feed, _, reveals) = Build();
        Publish(feed, "s-1");

        vm.RevealRoot(path);

        Assert.Empty(reveals);
    }

    [Fact]
    public void FindChain_ReturnsTheRowsFromTheRootDown_OrNull()
    {
        var (vm, feed, _, _) = Build();
        Publish(feed, "s-1");

        var chain = vm.FindChain("s-1");

        Assert.NotNull(chain);
        Assert.Equal(new[] { RootA, "s-1" }, chain!.Select(n => n.Key));
        Assert.Equal(new[] { RootB }, vm.FindChain(RootB)!.Select(n => n.Key));
        Assert.Null(vm.FindChain("nope"));
        Assert.Null(vm.FindChain(null));
    }

    [Fact]
    public void AFocusedIdThatNeverGetsARow_LikeAPlainTerminalTab_IsGivenUpOn()
    {
        var (vm, feed, writer, _) = Build();
        Publish(feed, "s-1");

        writer.SetFocused("terminal-guid");
        for (var i = 0; i < 45; i++)
        {
            Publish(feed, "s-1", $"other-{i}"); // each tick differs, so each one is a real rebuild (an attempt)
        }

        // Long after it was given up on, a row with that key appearing must not be force-selected: the pending
        // request is not retried on every rebuild for as long as that tab stays focused.
        Publish(feed, "s-1", "other-0", "terminal-guid");

        Assert.NotEqual("terminal-guid", vm.SelectedKey);
    }

    [Fact]
    public void AFocusedSessionThatAppearsWithinAFewSnapshots_IsStillSelected()
    {
        var (vm, feed, writer, _) = Build();
        Publish(feed, "s-1");

        writer.SetFocused("s-new");
        Publish(feed, "s-1");
        Publish(feed, "s-1", "s-new");

        Assert.Equal("s-new", vm.SelectedKey);
    }
}

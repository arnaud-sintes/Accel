namespace Accel.Tests;

using System.Collections.Generic;
using System.Linq;
using Accel.App.ViewModels;
using Xunit;

/// <summary>Panel A showing the project that hosts a file, and the rebuild signals the view uses to avoid
/// scrolling the panel on every telemetry tick.</summary>
public class RootsPanelProjectTests
{
    private const string RootA = @"C:\projects\a";
    private const string RootB = @"C:\projects\b";

    private static (RootsPanelViewModel Vm, FakeTelemetryFeed Feed) Build()
    {
        var feed = new FakeTelemetryFeed();
        return (new RootsPanelViewModel(feed, new RecordingUiThreadDispatcher()), feed);
    }

    private static void PublishTwoRoots(FakeTelemetryFeed feed) =>
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(RootA, TelemetryFixtures.Session("s-a", isLive: true)),
            TelemetryFixtures.Root(RootB),
        }));

    [Fact]
    public void RootPathContaining_FindsTheHostingRoot_ForAFileAnywhereBelowIt()
    {
        var (vm, feed) = Build();
        PublishTwoRoots(feed);

        Assert.Equal(RootA, vm.RootPathContaining(@"C:\projects\a\src\deep\File.cs"));
        Assert.Equal(RootB, vm.RootPathContaining("C:/projects/b/readme.md"));
        Assert.Null(vm.RootPathContaining(@"C:\projects\a-other\File.cs"));
        Assert.Null(vm.RootPathContaining(@"D:\elsewhere\File.cs"));
        Assert.Null(vm.RootPathContaining(null));
    }

    [Fact]
    public void SelectRootContaining_SelectsThatRoot_AndLeavesTheSessionRowsAlone()
    {
        var (vm, feed) = Build();
        PublishTwoRoots(feed);

        vm.SelectRootContaining(@"C:\projects\b\src\File.cs");

        Assert.Equal(RootB, vm.SelectedKey);
        Assert.True(vm.Roots.Single(r => r.Key == RootB).IsSelected);
        Assert.False(vm.Roots.Single(r => r.Key == RootA).IsSelected);
    }

    [Fact]
    public void SelectRootContaining_DoesNothing_ForAPathNoRootContains()
    {
        var (vm, feed) = Build();
        PublishTwoRoots(feed);
        vm.SelectRootContaining(@"C:\projects\a\x.cs");

        vm.SelectRootContaining(@"E:\nowhere\x.cs");

        Assert.Equal(RootA, vm.SelectedKey);
    }

    [Fact]
    public void Rebuild_RaisesRebuildingThenRebuilt_AndAPlainRebuildDoesNotAskToReveal()
    {
        var (vm, feed) = Build();
        var events = new List<string>();
        vm.TreeRebuilding += () => events.Add("rebuilding");
        vm.TreeRebuilt += reveal => events.Add("rebuilt:" + reveal);

        PublishTwoRoots(feed);
        vm.SelectRootContaining(@"C:\projects\a\x.cs");
        events.Clear();

        // A telemetry tick re-applies the user's selection to fresh rows; that must not be a reveal.
        PublishTwoRoots(feed);

        Assert.Equal(new[] { "rebuilding", "rebuilt:False" }, events);
    }
}

namespace Accel.Tests;

using System.Collections.Generic;
using System.Linq;
using Accel.App.Services;
using Accel.App.ViewModels;
using Xunit;

/// <summary>Where a new terminal lands in the strip, what gets remembered for the next launch, and
/// which events a file tab raises.</summary>
public class TabsPlacementAndRestoreTests
{
    private const string ProjectA = @"C:\projects\a";
    private const string ProjectB = @"C:\projects\b";

    private static (TabsViewModel Tabs, SessionSelectionService Selection) Build()
    {
        var host = new FakePtySessionHost(System.Array.Empty<string>());
        var selection = new SessionSelectionService();
        var tabs = new TabsViewModel(host, selection.AcquireWriter(), new RecordingUiThreadDispatcher());
        return (tabs, selection);
    }

    private static IEnumerable<string> Ids(TabsViewModel tabs) => tabs.Tabs.Select(t => t.TabId);

    [Fact]
    public void NewTerminal_SitsRightOfTheRightmostTabOfItsProject()
    {
        var (tabs, _) = Build();
        tabs.AddTab("a1", "a1", ProjectA, ProjectA);
        tabs.AddTab("b1", "b1", ProjectB, ProjectB);
        tabs.AddTab("a2", "a2", ProjectA, ProjectA);
        tabs.AddTab("b2", "b2", ProjectB, ProjectB);

        tabs.AddShellTab("shell", "Terminal - a", ProjectA, ProjectA);

        Assert.Equal(new[] { "a1", "b1", "a2", "shell", "b2" }, Ids(tabs));
        Assert.Same(tabs.Tabs[3], tabs.SelectedTab);
    }

    [Fact]
    public void NewTerminal_OfAProjectWithNoTabs_AppendsAtTheEnd()
    {
        var (tabs, _) = Build();
        tabs.AddTab("a1", "a1", ProjectA, ProjectA);

        tabs.AddShellTab("shell", "Terminal - b", ProjectB, ProjectB);

        Assert.Equal(new[] { "a1", "shell" }, Ids(tabs));
    }

    [Fact]
    public void ASecondTerminalOfTheSameProject_GoesAfterTheFirst()
    {
        var (tabs, _) = Build();
        tabs.AddTab("a1", "a1", ProjectA, ProjectA);
        tabs.AddTab("b1", "b1", ProjectB, ProjectB);
        tabs.AddShellTab("shell1", "t1", ProjectA, ProjectA);

        tabs.AddShellTab("shell2", "t2", ProjectA, ProjectA);

        Assert.Equal(new[] { "a1", "shell1", "shell2", "b1" }, Ids(tabs));
    }

    [Fact]
    public void FileTabsOfTheProject_DoNotCountAsAnchors()
    {
        var (tabs, _) = Build();
        tabs.AddTab("a1", "a1", ProjectA, ProjectA);
        tabs.AddFileTab(@"C:\projects\a\README.md");

        tabs.AddShellTab("shell", "t", ProjectA, ProjectA);

        Assert.Equal(new[] { "a1", "shell", @"C:\projects\a\README.md" }, Ids(tabs));
    }

    [Fact]
    public void Placement_CanBeTurnedOff_SoARestoredOrderComesBackVerbatim()
    {
        var (tabs, _) = Build();
        tabs.AddTab("a1", "a1", ProjectA, ProjectA);
        tabs.AddTab("b1", "b1", ProjectB, ProjectB);

        tabs.AddShellTab("shell", "t", ProjectA, ProjectA, placeNextToProject: false);

        Assert.Equal(new[] { "a1", "b1", "shell" }, Ids(tabs));
    }

    [Fact]
    public void RestoreSnapshot_KeepsStripOrder_Selection_AndSkipsEndedAndGitTabs()
    {
        var (tabs, _) = Build();
        tabs.AddTab("session-1", "Refactor", ProjectA, ProjectA);
        tabs.AddShellTab("shell-1", "Terminal - a", ProjectA, ProjectA);
        tabs.AddFileTab(@"C:\projects\a\README.md");
        tabs.AddGitChangeTab(@"C:\projects\a\new.cs", "new.cs", ProjectA, "new.cs");
        tabs.AddTab("session-ended", "Old", ProjectA, ProjectA).HasEnded = true;
        tabs.SelectTab("shell-1");

        var snapshot = tabs.CreateRestoreSnapshot();

        Assert.Equal(
            new[]
            {
                new SavedTab(SavedTabKind.Session, "session-1", "Refactor", ProjectA),
                new SavedTab(SavedTabKind.Shell, "shell-1", "Terminal - a", ProjectA),
                new SavedTab(SavedTabKind.File, @"C:\projects\a\README.md", "README.md", null),
            },
            snapshot.Tabs);
        Assert.Equal(1, snapshot.SelectedIndex);
    }

    [Fact]
    public void RestoreSnapshot_HasNoSelection_WhenTheSelectedTabIsNotRestorable()
    {
        var (tabs, _) = Build();
        tabs.AddTab("session-1", "s", ProjectA, ProjectA);
        tabs.AddGitChangeTab(@"C:\projects\a\new.cs", "new.cs", ProjectA, "new.cs");

        Assert.Null(tabs.CreateRestoreSnapshot().SelectedIndex);
    }

    [Fact]
    public void SelectingAFileTab_RaisesFileTabActivated_WithItsPath_ButASessionTabDoesNot()
    {
        var (tabs, selection) = Build();
        var activated = new List<string>();
        tabs.FileTabActivated += activated.Add;

        tabs.AddTab("session-1", "s", ProjectA, ProjectA);
        Assert.Empty(activated);

        tabs.AddFileTab(@"C:\projects\a\README.md");

        Assert.Equal(new[] { @"C:\projects\a\README.md" }, activated);
        // Still no session focus change: the file tab leaves the focused session alone.
        Assert.Equal("session-1", selection.FocusedSessionId);
    }

    [Fact]
    public void TabSetChanged_FiresOnAddRemoveMoveAndSelection()
    {
        var (tabs, _) = Build();
        int fired = 0;
        tabs.TabSetChanged += () => fired++;

        tabs.AddTab("a", "a", ProjectA, ProjectA);
        tabs.AddTab("b", "b", ProjectB, ProjectB);
        int afterAdds = fired;
        Assert.True(afterAdds >= 2);

        tabs.MoveTab(tabs.Tabs[1], tabs.Tabs[0]);
        Assert.True(fired > afterAdds);

        int afterMove = fired;
        tabs.SelectTab("a");
        Assert.True(fired > afterMove);
    }
}

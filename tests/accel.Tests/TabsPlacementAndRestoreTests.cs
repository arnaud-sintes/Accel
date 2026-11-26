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

    // --- file tabs open beside their project's tabs -----------------------------------------------------

    private const string FileA1 = @"C:\projects\a\README.md";
    private const string FileA2 = @"C:\projects\a\src\Program.cs";
    private const string FileB1 = @"C:\projects\b\notes.md";

    [Fact]
    public void AFileTab_OpensRightAfterTheLastSessionTabOfItsProject()
    {
        var (tabs, _) = Build();
        tabs.AddTab("a1", "a1", ProjectA, ProjectA);
        tabs.AddTab("b1", "b1", ProjectB, ProjectB);
        tabs.AddTab("a2", "a2", ProjectA, ProjectA);
        tabs.AddTab("b2", "b2", ProjectB, ProjectB);

        tabs.AddFileTab(FileA1, ProjectA);

        Assert.Equal(new[] { "a1", "b1", "a2", FileA1, "b2" }, Ids(tabs));
        Assert.Same(tabs.Tabs[3], tabs.SelectedTab);
    }

    [Fact]
    public void AFileTabAfterAnotherFileOfTheSameProject_GoesRightOfIt_KeepingTheOpeningOrder()
    {
        var (tabs, _) = Build();
        tabs.AddTab("a1", "a1", ProjectA, ProjectA);
        tabs.AddTab("b1", "b1", ProjectB, ProjectB);
        tabs.AddFileTab(FileA1, ProjectA);

        tabs.AddFileTab(FileA2, ProjectA);

        Assert.Equal(new[] { "a1", FileA1, FileA2, "b1" }, Ids(tabs));
    }

    [Fact]
    public void AFileOfAProjectWithNoOpenTabs_OrWithNoProject_Appends()
    {
        var (tabs, _) = Build();
        tabs.AddTab("a1", "a1", ProjectA, ProjectA);

        tabs.AddFileTab(FileB1, ProjectB);                    // project b has nothing open
        tabs.AddFileTab(@"D:\elsewhere\x.txt", projectPath: null); // no project at all

        Assert.Equal(new[] { "a1", FileB1, @"D:\elsewhere\x.txt" }, Ids(tabs));
    }

    [Fact]
    public void AFileTab_FollowsAProjectsOpenFiles_EvenWithNoSessionLeft()
    {
        var (tabs, _) = Build();
        tabs.AddFileTab(FileA1, ProjectA);
        tabs.AddTab("b1", "b1", ProjectB, ProjectB);

        tabs.AddFileTab(FileA2, ProjectA);

        Assert.Equal(new[] { FileA1, FileA2, "b1" }, Ids(tabs));
    }

    [Fact]
    public void RestoringATabSet_KeepsTheSavedOrder_NotThePlacement()
    {
        var (tabs, _) = Build();
        tabs.AddTab("a1", "a1", ProjectA, ProjectA);
        tabs.AddTab("b1", "b1", ProjectB, ProjectB);

        tabs.AddFileTab(FileA1, ProjectA, placeNextToProject: false);

        Assert.Equal(new[] { "a1", "b1", FileA1 }, Ids(tabs));
    }

    [Fact]
    public void ReopeningAnOpenFile_SelectsItWhereItIs_AndDoesNotMoveIt()
    {
        var (tabs, _) = Build();
        tabs.AddTab("a1", "a1", ProjectA, ProjectA);
        tabs.AddFileTab(FileA1, ProjectA);
        tabs.AddTab("a2", "a2", ProjectA, ProjectA); // appended after the file: a2 is not placed (AddTab appends)
        var before = Ids(tabs).ToArray();

        var again = tabs.AddFileTab(FileA1, ProjectA);

        Assert.Equal(before, Ids(tabs));
        Assert.Same(again, tabs.SelectedTab);
    }

    [Fact]
    public void ANewTerminal_StillIgnoresFileTabsOfTheProject_NowThatTheyCarryAProject()
    {
        var (tabs, _) = Build();
        tabs.AddTab("a1", "a1", ProjectA, ProjectA);
        tabs.AddFileTab(FileA1, ProjectA);

        tabs.AddShellTab("shell", "t", ProjectA, ProjectA);

        Assert.Equal(new[] { "a1", "shell", FileA1 }, Ids(tabs));
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
    public void TabActivated_IsRaisedForEveryKindOfTab_WithTheTabItself()
    {
        var (tabs, _) = Build();
        var activated = new List<(string Id, bool HasPty)>();
        tabs.TabActivated += tab => activated.Add((tab.TabId, tab.HasPtySession));

        tabs.AddTab("session-1", "s", ProjectA, ProjectA);
        tabs.AddShellTab("shell-1", "t", ProjectA, ProjectA);
        tabs.AddFileTab(@"C:\projects\a\README.md");

        Assert.Equal(
            new[] { ("session-1", true), ("shell-1", true), (@"C:\projects\a\README.md", false) },
            activated);
    }

    [Fact]
    public void TabActivated_IsRaisedWhenSwitchingBackToAnEarlierTab_ButNotWhenNothingIsSelected()
    {
        var (tabs, _) = Build();
        tabs.AddTab("a", "a", ProjectA, ProjectA);
        tabs.AddTab("b", "b", ProjectB, ProjectB);
        var activated = new List<string>();
        tabs.TabActivated += tab => activated.Add(tab.TabId);

        tabs.SelectTab("a");
        Assert.Equal(new[] { "a" }, activated);

        tabs.SelectedTab = null;
        Assert.Equal(new[] { "a" }, activated);
    }

    // --- Ctrl+Tab / Ctrl+Shift+Tab ---------------------------------------------------------------------

    private static TabsViewModel ThreeTabs()
    {
        var (tabs, _) = Build();
        tabs.AddTab("a", "a", ProjectA, ProjectA);
        tabs.AddTab("b", "b", ProjectB, ProjectB);
        tabs.AddFileTab(@"C:\projects\a\README.md");
        return tabs;
    }

    [Fact]
    public void CycleTab_Forward_GoesRightThroughEveryKindOfTab_AndWrapsToTheFirst()
    {
        var tabs = ThreeTabs();
        tabs.SelectTab("a");

        tabs.CycleTab(forward: true);
        Assert.Equal("b", tabs.SelectedTabId);

        tabs.CycleTab(forward: true);
        Assert.Equal(@"C:\projects\a\README.md", tabs.SelectedTabId); // a file tab is part of the cycle

        tabs.CycleTab(forward: true);
        Assert.Equal("a", tabs.SelectedTabId); // wrapped
    }

    [Fact]
    public void CycleTab_Backward_GoesLeft_AndWrapsToTheLast()
    {
        var tabs = ThreeTabs();
        tabs.SelectTab("a");

        tabs.CycleTab(forward: false);
        Assert.Equal(@"C:\projects\a\README.md", tabs.SelectedTabId);

        tabs.CycleTab(forward: false);
        Assert.Equal("b", tabs.SelectedTabId);
    }

    [Fact]
    public void CycleTab_WithNothingSelected_StartsAtTheFirst_OrTheLast()
    {
        var tabs = ThreeTabs();
        tabs.SelectedTab = null;
        tabs.CycleTab(forward: true);
        Assert.Equal("a", tabs.SelectedTabId);

        tabs.SelectedTab = null;
        tabs.CycleTab(forward: false);
        Assert.Equal(@"C:\projects\a\README.md", tabs.SelectedTabId);
    }

    [Fact]
    public void CycleTab_FollowsTheStripOrder_NotTheOrderTabsWereOpened()
    {
        var (tabs, _) = Build();
        tabs.AddTab("a1", "a1", ProjectA, ProjectA);
        tabs.AddTab("b1", "b1", ProjectB, ProjectB);
        tabs.AddShellTab("shell", "t", ProjectA, ProjectA); // lands between a1 and b1
        tabs.SelectTab("a1");

        tabs.CycleTab(forward: true);

        Assert.Equal("shell", tabs.SelectedTabId);
    }

    [Fact]
    public void CycleTab_WithZeroOrOneTab_DoesNothing()
    {
        var (tabs, _) = Build();
        tabs.CycleTab(forward: true);
        Assert.Null(tabs.SelectedTab);

        tabs.AddTab("only", "only", ProjectA, ProjectA);
        int changes = 0;
        tabs.TabActivated += _ => changes++;

        tabs.CycleTab(forward: true);
        tabs.CycleTab(forward: false);

        Assert.Equal("only", tabs.SelectedTabId);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void CycleTab_SelectsLikeAClick_SoTheFocusedSessionFollows()
    {
        var (tabs, selection) = Build();
        tabs.AddTab("a", "a", ProjectA, ProjectA);
        tabs.AddTab("b", "b", ProjectB, ProjectB);
        tabs.SelectTab("a");

        tabs.CycleTab(forward: true);

        Assert.Equal("b", selection.FocusedSessionId);
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

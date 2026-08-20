namespace Accel.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Accel.App;
using Accel.App.Services;
using Accel.App.ViewModels;
using Accel.Orchestration;
using Xunit;

/// <summary>
/// A <see cref="PtyRegistry"/>-shaped double. A real registry needs real <see cref="PtySession"/> objects,
/// i.e. real ConPTYs and real child processes, which is exactly why <see cref="IPtySessionHost"/> exists -
/// the pure tab logic (add/remove/select, self-exit handling, close routing) is tested here, and the
/// real-process behaviour is proven by the <c>tabs-e2e-smoke-test</c>/<c>pty-registry-stress-test</c>
/// verbs instead.
/// </summary>
internal sealed class FakePtySessionHost : IPtySessionHost
{
    private readonly List<string> _tabIds;
    private readonly Dictionary<string, int> _pidsByTabId = new(StringComparer.OrdinalIgnoreCase);

    public FakePtySessionHost(params string[] tabIds) => _tabIds = tabIds.ToList();

    public event EventHandler<PtySessionEndedEventArgs>? SessionEnded;

    /// <summary>Every tabId <see cref="CloseAsync"/> was called with, in order - the assertion surface for
    /// "tab close routes through the registry".</summary>
    public List<string> Closed { get; } = new();

    /// <summary>Whether anything is still subscribed to <see cref="SessionEnded"/>.</summary>
    public bool HasSubscribers => SessionEnded is not null;

    public IReadOnlyList<string> TabIds() => _tabIds.ToArray();

    /// <summary>Test setup for <see cref="PollFocusedSessionId"/>-driven tests: registers a pid for a
    /// tabId, so <see cref="TryGetProcessId"/> stops returning null for it.</summary>
    public void SetProcessId(string tabId, int pid) => _pidsByTabId[tabId] = pid;

    public int? TryGetProcessId(string tabId) =>
        !string.IsNullOrEmpty(tabId) && _pidsByTabId.TryGetValue(tabId, out int pid) ? pid : null;

    public void Add(string tabId) => _tabIds.Add(tabId);

    public Task<PtyCloseResult> CloseAsync(string tabId, CancellationToken cancellationToken = default)
    {
        Closed.Add(tabId);
        _tabIds.Remove(tabId);

        // The registry fires SessionEnded for every close, including a user-initiated one (TornDown).
        var result = new PtyCloseResult(tabId, PtyCloseOutcome.Closed, 1234, 0, PtySessionExitReason.TornDown, TimeSpan.Zero, null);
        RaiseEnded(tabId, PtySessionExitReason.TornDown, 0, PtyCloseOutcome.Closed);
        return Task.FromResult(result);
    }

    /// <summary>Simulates a child that ended on its own.</summary>
    public void RaiseChildExited(string tabId, int? exitCode = 0)
    {
        _tabIds.Remove(tabId);
        RaiseEnded(tabId, PtySessionExitReason.ChildExited, exitCode, PtyCloseOutcome.Closed);
    }

    /// <summary>Drops a registration without any notification - stands in for a session that left the
    /// registry while nothing was subscribed, which is what <c>SyncFromHost</c> has to reconcile.</summary>
    public void RaiseChildExitedSilently(string tabId) => _tabIds.Remove(tabId);

    private void RaiseEnded(string tabId, PtySessionExitReason reason, int? exitCode, PtyCloseOutcome outcome) =>
        SessionEnded?.Invoke(this, new PtySessionEndedEventArgs(tabId, reason, exitCode, outcome));
}

/// <summary>
/// P3-T1 unit tests for panel C's <see cref="TabsViewModel"/>: tab add/remove/select, the single-writer
/// selection path, self-exit handling, and the invariant that closing a tab goes through the registry's
/// <see cref="IPtySessionHost.CloseAsync"/> - never <see cref="PtySession.Dispose"/> (which the ViewModel
/// could not call even if it wanted to: it never holds a session).
/// </summary>
public class TabsViewModelTests
{
    private static (TabsViewModel Tabs, FakePtySessionHost Host, SessionSelectionService Selection, RecordingUiThreadDispatcher Dispatcher) Build(
        params string[] existingTabIds)
    {
        var host = new FakePtySessionHost(existingTabIds);
        var selection = new SessionSelectionService();
        var dispatcher = new RecordingUiThreadDispatcher();
        return (new TabsViewModel(host, selection.AcquireWriter(), dispatcher), host, selection, dispatcher);
    }

    [Fact]
    public void AddTab_AddsAndSelects_AndWritesTheFocusedSessionId()
    {
        var (tabs, _, selection, _) = Build();

        tabs.AddTab("tab-a", "first");
        tabs.AddTab("tab-b", "second");

        Assert.Equal(new[] { "tab-a", "tab-b" }, tabs.Tabs.Select(t => t.TabId));
        Assert.Equal("second", tabs.SelectedTab!.Title);
        Assert.Equal("tab-b", selection.FocusedSessionId);
        Assert.False(tabs.IsEmpty);
    }

    [Fact]
    public void AddTab_IsIdempotentForTheSameTabId()
    {
        var (tabs, _, _, _) = Build();

        var first = tabs.AddTab("tab-a", "first");
        var again = tabs.AddTab("tab-a", "ignored duplicate title");

        Assert.Same(first, again);
        Assert.Single(tabs.Tabs);
        Assert.Equal("first", first.Title);
    }

    [Fact]
    public void AddTab_WithoutATitle_FallsBackToTheShortTabId()
    {
        var (tabs, _, _, _) = Build();

        var tab = tabs.AddTab("0123456789abcdef");

        Assert.Equal("01234567", tab.Title);
        Assert.Equal("01234567", tab.ShortTabId);
    }

    [Fact]
    public void SelectTab_ChangesTheFocusedSessionId_AndIsANoOpForAnUnknownTab()
    {
        var (tabs, _, selection, _) = Build();
        tabs.AddTab("tab-a");
        tabs.AddTab("tab-b");

        tabs.SelectTab("tab-a");
        Assert.Equal("tab-a", selection.FocusedSessionId);
        Assert.Equal("tab-a", tabs.SelectedTabId);

        // A stale UI command for a tab that no longer exists must not throw or clear the selection.
        tabs.SelectTab("tab-gone");
        Assert.Equal("tab-a", selection.FocusedSessionId);
    }

    [Fact]
    public void SelectTab_WithNull_ClearsTheFocus()
    {
        var (tabs, _, selection, _) = Build();
        tabs.AddTab("tab-a");

        tabs.SelectTab(null);

        Assert.Null(selection.FocusedSessionId);
        Assert.Null(tabs.SelectedTab);
    }

    [Fact]
    public void SelectingATab_ReattachesPanelDToThatTab()
    {
        var (tabs, _, _, _) = Build();
        var attached = new List<string>();
        tabs.AttachTerminalAsync = tabId =>
        {
            attached.Add(tabId);
            return Task.CompletedTask;
        };

        tabs.AddTab("tab-a");
        tabs.AddTab("tab-b");
        tabs.SelectTab("tab-a");

        // One attach per selection change, always for the newly selected tab - the reattach-one-control
        // model documented on TabsViewModel.
        Assert.Equal(new[] { "tab-a", "tab-b", "tab-a" }, attached);
    }

    [Fact]
    public async Task AFailingAttach_DoesNotBreakSelection()
    {
        var (tabs, _, selection, _) = Build();
        tabs.AttachTerminalAsync = _ => throw new InvalidOperationException("WebView2 not ready");

        tabs.AddTab("tab-a");

        Assert.Equal("tab-a", selection.FocusedSessionId);
        Assert.NotNull(tabs.LastAttach);
        await tabs.LastAttach!; // swallowed, not faulted
    }

    [Fact]
    public async Task CloseTab_RoutesThroughTheRegistry_AndRemovesTheTab()
    {
        var (tabs, host, selection, _) = Build();
        tabs.AddTab("tab-a");
        tabs.AddTab("tab-b");

        await tabs.CloseTabAsync("tab-b");

        Assert.Equal(new[] { "tab-b" }, host.Closed);
        Assert.Equal(new[] { "tab-a" }, tabs.Tabs.Select(t => t.TabId));

        // Focus follows to the remaining tab rather than being left pointing at a dead session.
        Assert.Equal("tab-a", selection.FocusedSessionId);
    }

    [Fact]
    public async Task CloseTab_ForTheLastTab_ClearsTheFocus()
    {
        var (tabs, host, selection, _) = Build();
        tabs.AddTab("tab-a");

        await tabs.CloseTabAsync("tab-a");

        Assert.Empty(tabs.Tabs);
        Assert.True(tabs.IsEmpty);
        Assert.Null(selection.FocusedSessionId);
        Assert.Equal(new[] { "tab-a" }, host.Closed);
    }

    [Fact]
    public async Task CloseTab_ForAnUnknownTab_IsANoOp()
    {
        var (tabs, host, _, _) = Build();
        tabs.AddTab("tab-a");

        await tabs.CloseTabAsync("tab-does-not-exist");

        Assert.Single(tabs.Tabs);
        Assert.Empty(host.Closed);
    }

    [Fact]
    public async Task CloseTab_SelectsTheLeftNeighbour()
    {
        var (tabs, _, selection, _) = Build();
        tabs.AddTab("tab-a");
        tabs.AddTab("tab-b");
        tabs.AddTab("tab-c");
        tabs.SelectTab("tab-b");

        await tabs.CloseTabAsync("tab-b");

        Assert.Equal("tab-c", selection.FocusedSessionId); // the tab that took index 1
        Assert.Equal(new[] { "tab-a", "tab-c" }, tabs.Tabs.Select(t => t.TabId));
    }

    [Fact]
    public async Task CloseTabCommand_IsTheBoundCommandPanelCUses()
    {
        var (tabs, host, _, _) = Build();
        var tab = tabs.AddTab("tab-a");

        Assert.True(tabs.CloseTabCommand.CanExecute(tab));
        tabs.CloseTabCommand.Execute(tab);
        await tabs.CloseTabCommand.ExecutionTask!;

        Assert.Equal(new[] { "tab-a" }, host.Closed);
        Assert.Empty(tabs.Tabs);
    }

    // ---------------------------------------------------------------------------------------------
    // P4-T5: StopTabAsync - the same registry teardown as CloseTabAsync, but the tab must survive.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task StopTab_RoutesThroughTheRegistry_ButKeepsTheTab_FlaggedAsEnded()
    {
        var (tabs, host, _, _) = Build();
        var tab = tabs.AddTab("tab-a", "first");

        await tabs.StopTabAsync(tab);

        Assert.Equal(new[] { "tab-a" }, host.Closed); // same teardown path as Close
        Assert.Single(tabs.Tabs); // but the tab itself is never removed
        Assert.True(tab.HasEnded);
        Assert.Equal("(exited 0)", tab.StatusSuffix);
    }

    [Fact]
    public async Task StopTab_MovesFocusAwayFromTheStoppedTab_WhenItWasFocused()
    {
        var (tabs, _, selection, _) = Build();
        var tabA = tabs.AddTab("tab-a");
        tabs.AddTab("tab-b");
        tabs.SelectTab("tab-a");

        await tabs.StopTabAsync(tabA);

        Assert.NotEqual("tab-a", selection.FocusedSessionId);
        Assert.Equal("tab-b", selection.FocusedSessionId);
    }

    [Fact]
    public async Task StopTab_IsANoOp_ForATabThatAlreadyEnded()
    {
        var (tabs, host, _, _) = Build();
        var tab = tabs.AddTab("tab-a");
        host.RaiseChildExited("tab-a");
        Assert.True(tab.HasEnded);

        await tabs.StopTabAsync(tab);

        Assert.Empty(host.Closed); // never even asked the registry to close an already-ended tab
    }

    [Fact]
    public async Task StopTab_IsANoOp_ForNull()
    {
        var (tabs, host, _, _) = Build();

        await tabs.StopTabAsync(null);

        Assert.Empty(host.Closed);
    }

    [Fact]
    public async Task StopTabCommand_IsTheBoundCommandTheDoubleClickGestureUses()
    {
        var (tabs, host, _, _) = Build();
        var tab = tabs.AddTab("tab-a");

        Assert.True(tabs.StopTabCommand.CanExecute(tab));
        tabs.StopTabCommand.Execute(tab);
        await tabs.StopTabCommand.ExecutionTask!;

        Assert.Equal(new[] { "tab-a" }, host.Closed);
        Assert.Single(tabs.Tabs);
        Assert.True(tab.HasEnded);
    }

    [Fact]
    public void ASessionThatExitsOnItsOwn_KeepsItsTab_FlaggedAsEnded()
    {
        var (tabs, host, selection, _) = Build();
        tabs.AddTab("tab-a", "the only session");

        host.RaiseChildExited("tab-a", exitCode: 3);

        var tab = Assert.Single(tabs.Tabs);
        Assert.True(tab.HasEnded);
        Assert.Equal(3, tab.ExitCode);
        Assert.Equal("(exited 3)", tab.StatusSuffix);
        Assert.Contains("Ended", tab.AutomationDescription, StringComparison.Ordinal);

        // No live tab is left, so nothing is focused - panels B/E must not keep pointing at a dead session.
        Assert.Null(selection.FocusedSessionId);
    }

    [Fact]
    public void ASelfExit_MovesFocusToARemainingLiveTab()
    {
        var (tabs, host, selection, _) = Build();
        tabs.AddTab("tab-a");
        tabs.AddTab("tab-b");
        tabs.SelectTab("tab-b");

        host.RaiseChildExited("tab-b");

        Assert.Equal(2, tabs.Tabs.Count);
        Assert.Equal("tab-a", selection.FocusedSessionId);
    }

    [Fact]
    public void ASelfExitWithNoExitCode_StillShowsAnEndedState()
    {
        var (tabs, host, _, _) = Build();
        tabs.AddTab("tab-a");

        host.RaiseChildExited("tab-a", exitCode: null);

        Assert.Equal("(ended)", tabs.Tabs[0].StatusSuffix);
    }

    [Fact]
    public void SessionEnded_ForAnUnknownTab_IsIgnored()
    {
        var (tabs, host, _, _) = Build();
        tabs.AddTab("tab-a");

        host.RaiseChildExited("some-other-tab");

        Assert.False(tabs.Tabs[0].HasEnded);
    }

    [Fact]
    public void SessionEnded_IsMarshalledOntoTheUiThread()
    {
        var (tabs, host, _, dispatcher) = Build();
        tabs.AddTab("tab-a");
        dispatcher.RunInline = false;

        host.RaiseChildExited("tab-a");

        // PtyRegistry raises SessionEnded on a thread-pool thread, so the ViewModel must not mutate its
        // ObservableCollection inline.
        Assert.False(tabs.Tabs[0].HasEnded);
        dispatcher.Drain();
        Assert.True(tabs.Tabs[0].HasEnded);
    }

    [Fact]
    public void Construction_ProjectsSessionsThatAreAlreadyRegistered()
    {
        var (tabs, _, selection, _) = Build("pre-existing-1", "pre-existing-2");

        Assert.Equal(2, tabs.Tabs.Count);
        Assert.Equal("pre-existing-1", selection.FocusedSessionId);
    }

    [Fact]
    public void SyncFromHost_AddsUnknownRegistrations_AndFlagsVanishedOnes()
    {
        var (tabs, host, _, _) = Build();
        tabs.AddTab("tab-a");
        host.Add("tab-a");
        host.Add("tab-b"); // registered by some other path

        tabs.SyncFromHost();

        Assert.Equal(new[] { "tab-a", "tab-b" }, tabs.Tabs.Select(t => t.TabId));
        Assert.All(tabs.Tabs, t => Assert.False(t.HasEnded));

        // Now "tab-a" disappears from the registry without a SessionEnded (e.g. it was closed before this
        // ViewModel subscribed): the reconciliation must not leave it looking alive.
        host.RaiseChildExitedSilently("tab-a");
        tabs.SyncFromHost();

        Assert.True(tabs.Tabs.Single(t => t.TabId == "tab-a").HasEnded);
    }

    [Fact]
    public void Dispose_UnsubscribesFromTheRegistry_AndClosesNothing()
    {
        var (tabs, host, _, _) = Build();
        tabs.AddTab("tab-a");

        tabs.Dispose();

        Assert.False(host.HasSubscribers);
        Assert.Empty(host.Closed); // disposing a ViewModel must never tear a session down (P3-T4's job)
    }

    // ---------------------------------------------------------------------------------------------
    // PollFocusedSessionId: /clear rotates the session id Claude Code reports for a pid without
    // touching the pty/tabId at all - see the method's own remarks for the full scenario.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void PollFocusedSessionId_ReFocusesToTheCurrentSessionId_WhenTheStatusFileHasDrifted()
    {
        var host = new FakePtySessionHost("tab-a");
        host.SetProcessId("tab-a", 4321);
        var selection = new SessionSelectionService();
        var dispatcher = new RecordingUiThreadDispatcher();
        var tabs = new TabsViewModel(
            host,
            selection.AcquireWriter(),
            dispatcher,
            statusReader: pid => pid == 4321
                ? new ClaudeSessionStatusSnapshot(pid, "new-session-after-clear", null, null, "idle", null)
                : null);

        tabs.SelectTab("tab-a");
        Assert.Equal("tab-a", selection.FocusedSessionId); // unchanged at selection time

        tabs.PollFocusedSessionId();

        Assert.Equal("new-session-after-clear", selection.FocusedSessionId);
    }

    [Fact]
    public void PollFocusedSessionId_IsANoOp_WhenTheSelectedTabHasNoKnownPid()
    {
        var host = new FakePtySessionHost("tab-a"); // no SetProcessId call
        var selection = new SessionSelectionService();
        var tabs = new TabsViewModel(host, selection.AcquireWriter(), new RecordingUiThreadDispatcher());

        tabs.SelectTab("tab-a");
        tabs.PollFocusedSessionId();

        Assert.Equal("tab-a", selection.FocusedSessionId);
    }

    [Fact]
    public void PollFocusedSessionId_IsANoOp_WhenTheStatusFileHasNoSessionId()
    {
        var host = new FakePtySessionHost("tab-a");
        host.SetProcessId("tab-a", 4321);
        var selection = new SessionSelectionService();
        var tabs = new TabsViewModel(
            host,
            selection.AcquireWriter(),
            new RecordingUiThreadDispatcher(),
            statusReader: _ => null); // status file missing/unreadable - degrade to "unknown", not a guess

        tabs.SelectTab("tab-a");
        tabs.PollFocusedSessionId();

        Assert.Equal("tab-a", selection.FocusedSessionId);
    }

    [Fact]
    public void PollFocusedSessionId_NeverAppliesAcrossATabSwitch()
    {
        var host = new FakePtySessionHost("tab-a", "tab-b");
        host.SetProcessId("tab-a", 111);
        var selection = new SessionSelectionService();
        var dispatcher = new RecordingUiThreadDispatcher { RunInline = false };
        var tabs = new TabsViewModel(
            host,
            selection.AcquireWriter(),
            dispatcher,
            statusReader: pid => pid == 111 ? new ClaudeSessionStatusSnapshot(pid, "rotated-id", null, null, "idle", null) : null);

        tabs.SelectTab("tab-a");
        tabs.PollFocusedSessionId(); // queued on the dispatcher, not yet applied
        tabs.SelectTab("tab-b"); // selection moves on before the queued post runs

        dispatcher.Drain();

        // The stale resolution for tab-a must not clobber the newer tab-b selection.
        Assert.Equal("tab-b", selection.FocusedSessionId);
    }

    [Fact]
    public void PollFocusedSessionId_IsANoOp_WhenNothingIsSelected()
    {
        var host = new FakePtySessionHost();
        var selection = new SessionSelectionService();
        var tabs = new TabsViewModel(host, selection.AcquireWriter(), new RecordingUiThreadDispatcher());

        tabs.PollFocusedSessionId();

        Assert.Null(selection.FocusedSessionId);
    }

    [Fact]
    public void Dispose_StopsThePollTimer_AndDoesNotThrow()
    {
        var host = new FakePtySessionHost("tab-a");
        var selection = new SessionSelectionService();
        var tabs = new TabsViewModel(
            host,
            selection.AcquireWriter(),
            new RecordingUiThreadDispatcher(),
            statusPollInterval: TimeSpan.FromMilliseconds(20));

        tabs.Dispose();

        // No crash from a timer callback firing after disposal - PollFocusedSessionId's own _disposed
        // check makes any race here harmless even before the timer is actually torn down.
        Thread.Sleep(50);
    }

    [Fact]
    public void TabsViewModel_NeverExposesAPtySession()
    {
        // The structural half of "never call PtySession.Dispose": no member of the tab layer hands out a
        // session, so no dispose call is even expressible there.
        var offenders = typeof(TabsViewModel).GetMembers()
            .Concat(typeof(TabViewModel).GetMembers())
            .Concat(typeof(IPtySessionHost).GetMembers())
            .Where(m => MemberType(m) is { } type && typeof(PtySession).IsAssignableFrom(type))
            .Select(m => m.Name)
            .ToArray();

        Assert.Empty(offenders);
    }

    private static Type? MemberType(System.Reflection.MemberInfo member) => member switch
    {
        System.Reflection.PropertyInfo property => property.PropertyType,
        System.Reflection.FieldInfo field => field.FieldType,
        System.Reflection.MethodInfo method => method.ReturnType,
        _ => null,
    };

    // ---------------------------------------------------------------------------------------------
    // Panel C's per-kind tab icon (IconGlyph/IconColorHex) - pins down that all four kinds a tab can
    // be (Session, File, single-pane GitChange, GitChange diff) get their own distinct glyph, and that
    // none of them collide with each other.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void IconGlyph_IsDistinctAcrossAllFiveTabKinds()
    {
        var session = new TabViewModel("tab-a", "a");
        var shell = TabViewModel.ForShell("tab-b", "b");
        var file = TabViewModel.ForFile(@"C:\project\file.cs");
        var gitFile = TabViewModel.ForGitChange(@"C:\project\new.cs", "new.cs", @"C:\project", "new.cs");
        var gitDiff = TabViewModel.ForGitDiff(
            @"C:\project\changed.cs", "changed.cs", @"C:\project", "changed.cs", GitDiffSide.Index, GitDiffSide.WorkingTree);

        var glyphs = new[] { session.IconGlyph, shell.IconGlyph, file.IconGlyph, gitFile.IconGlyph, gitDiff.IconGlyph };

        Assert.All(glyphs, g => Assert.False(string.IsNullOrEmpty(g)));
        Assert.Equal(glyphs.Length, glyphs.Distinct().Count());
    }

    [Fact]
    public void IconColorHex_SharesTheDangerColorAcrossBothGitChangeKinds_ButDiffersFromSessionShellAndFile()
    {
        var session = new TabViewModel("tab-a", "a");
        var shell = TabViewModel.ForShell("tab-b", "b");
        var file = TabViewModel.ForFile(@"C:\project\file.cs");
        var gitFile = TabViewModel.ForGitChange(@"C:\project\new.cs", "new.cs", @"C:\project", "new.cs");
        var gitDiff = TabViewModel.ForGitDiff(
            @"C:\project\changed.cs", "changed.cs", @"C:\project", "changed.cs", GitDiffSide.Index, GitDiffSide.WorkingTree);

        Assert.Equal(gitFile.IconColorHex, gitDiff.IconColorHex);
        Assert.NotEqual(session.IconColorHex, shell.IconColorHex);
        Assert.NotEqual(session.IconColorHex, file.IconColorHex);
        Assert.NotEqual(session.IconColorHex, gitFile.IconColorHex);
        Assert.NotEqual(shell.IconColorHex, file.IconColorHex);
        Assert.NotEqual(shell.IconColorHex, gitFile.IconColorHex);
        Assert.NotEqual(file.IconColorHex, gitFile.IconColorHex);
    }

    // ---------------------------------------------------------------------------------------------
    // Shell tabs (TabKind.Shell) - panel A's root-folder "Open terminal here" context menu item.
    // Mechanically identical to a Session tab wherever HasPtySession is the deciding factor: attach/
    // detach the terminal, close through the host, and reconcile against SyncFromHost.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AddShellTab_AddsAndSelects_AndBehavesLikeASessionTabForAttach()
    {
        var (tabs, _, selection, _) = Build();

        tabs.AddShellTab("shell-a", "Terminal - project");

        var tab = Assert.Single(tabs.Tabs);
        Assert.Equal(TabKind.Shell, tab.Kind);
        Assert.True(tab.HasPtySession);
        Assert.Equal("shell-a", selection.FocusedSessionId);
        Assert.Same(tab, tabs.SelectedTab);
    }

    [Fact]
    public void AddShellTab_ReopeningTheSameTabId_SelectsRatherThanDuplicates()
    {
        var (tabs, _, _, _) = Build();
        tabs.AddShellTab("shell-a");
        tabs.AddTab("session-a");

        var reopened = tabs.AddShellTab("shell-a");

        Assert.Equal(2, tabs.Tabs.Count);
        Assert.Same(reopened, tabs.SelectedTab);
    }

    [Fact]
    public async Task CloseTabAsync_OnAShellTab_RoutesThroughTheHost_LikeASessionTab()
    {
        var (tabs, host, _, _) = Build();
        tabs.AddShellTab("shell-a");

        await tabs.CloseTabAsync("shell-a");

        Assert.Contains("shell-a", host.Closed);
        Assert.Empty(tabs.Tabs);
    }

    [Fact]
    public void SyncFromHost_ReconcilesAShellTab_ExactlyLikeASessionTab()
    {
        var (tabs, host, _, _) = Build();
        tabs.AddShellTab("shell-a");
        host.Add("shell-a");

        host.RaiseChildExitedSilently("shell-a");
        tabs.SyncFromHost();

        Assert.True(tabs.Tabs.Single(t => t.TabId == "shell-a").HasEnded);
    }

    // ---------------------------------------------------------------------------------------------
    // TabViewModel.IsMarkdown / IsPreviewMode and TabsViewModel.ToggleMarkdownPreviewCommand - the
    // FILE/GIT panel markdown content/HTML preview toggle. Diff tabs are explicitly out of scope
    // (per the feature's own scoping decision) - IsMarkdown must stay false for one even though its
    // extension resolves to Markdown.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void IsMarkdown_TrueForAMarkdownFileTab_FalseForOtherExtensionsAndKinds()
    {
        var markdownFile = TabViewModel.ForFile(@"C:\project\README.md");
        var markdownGitChange = TabViewModel.ForGitChange(@"C:\project\NOTES.markdown", "NOTES.markdown", @"C:\project", "NOTES.markdown");
        var csharpFile = TabViewModel.ForFile(@"C:\project\file.cs");
        var session = new TabViewModel("tab-a", "a");

        Assert.True(markdownFile.IsMarkdown);
        Assert.True(markdownGitChange.IsMarkdown);
        Assert.False(csharpFile.IsMarkdown);
        Assert.False(session.IsMarkdown);
    }

    [Fact]
    public void IsMarkdown_FalseForAMarkdownDiffTab_EvenThoughTheExtensionResolvesToMarkdown()
    {
        var markdownDiff = TabViewModel.ForGitDiff(
            @"C:\project\README.md", "README.md", @"C:\project", "README.md", GitDiffSide.Head, GitDiffSide.WorkingTree);

        Assert.True(markdownDiff.IsGitDiffTab);
        Assert.False(markdownDiff.IsMarkdown);
    }

    [Fact]
    public void IsPreviewMode_DefaultsFalse()
    {
        var markdownFile = TabViewModel.ForFile(@"C:\project\README.md");

        Assert.False(markdownFile.IsPreviewMode);
    }

    [Fact]
    public void ToggleMarkdownPreviewCommand_FlipsIsPreviewMode()
    {
        var (tabs, _, _, _) = Build();
        var tab = tabs.AddFileTab(@"C:\project\README.md");

        tabs.ToggleMarkdownPreviewCommand.Execute(tab);
        Assert.True(tab.IsPreviewMode);

        tabs.ToggleMarkdownPreviewCommand.Execute(tab);
        Assert.False(tab.IsPreviewMode);
    }

    [Fact]
    public void ToggleMarkdownPreviewCommand_OnANullTab_IsANoOp()
    {
        var (tabs, _, _, _) = Build();

        tabs.ToggleMarkdownPreviewCommand.Execute(null);
    }

    [Fact]
    public async Task ToggleMarkdownPreviewCommand_OnTheSelectedTab_RerendersThroughShowFileAsync()
    {
        var (tabs, _, _, _) = Build();
        var tab = tabs.AddFileTab(@"C:\project\README.md");

        var renderedTabs = new List<TabViewModel>();
        tabs.ShowFileAsync = t =>
        {
            renderedTabs.Add(t);
            return Task.CompletedTask;
        };

        tabs.ToggleMarkdownPreviewCommand.Execute(tab);
        await tabs.LastAttach!;

        Assert.Same(tab, Assert.Single(renderedTabs));
    }

    [Fact]
    public void ToggleMarkdownPreviewCommand_OnATabThatIsNotSelected_DoesNotRerender()
    {
        var (tabs, _, _, _) = Build();
        var selected = tabs.AddFileTab(@"C:\project\README.md");
        var other = tabs.AddFileTab(@"C:\project\OTHER.md");
        tabs.SelectedTab = selected;

        var renderCount = 0;
        tabs.ShowFileAsync = _ =>
        {
            renderCount++;
            return Task.CompletedTask;
        };

        tabs.ToggleMarkdownPreviewCommand.Execute(other);

        Assert.True(other.IsPreviewMode);
        Assert.Equal(0, renderCount);
    }

    // --- T8: the close guard for a dirty, editable tab -----------------------------------------

    [Fact]
    public async Task CloseTabAsync_OnADirtyTab_Cancel_LeavesTheTabOpenAndStillDirty()
    {
        var (tabs, _, _, _) = Build();
        var tab = tabs.AddFileTab(@"C:\project\README.md");
        tab.IsEditable = true;
        tab.IsDirty = true;

        tabs.ConfirmCloseDirtyTabAsync = _ => Task.FromResult(AccelDialogChoice.Cancel);
        var saveCalled = false;
        tabs.SaveFileAsync = _ =>
        {
            saveCalled = true;
            return Task.FromResult(true);
        };

        await tabs.CloseTabAsync(tab);

        Assert.Contains(tab, tabs.Tabs);
        Assert.True(tab.IsDirty);
        Assert.False(saveCalled);
    }

    [Fact]
    public async Task CloseTabAsync_OnADirtyTab_Save_SavesThenClosesTheTab()
    {
        var (tabs, _, _, _) = Build();
        var tab = tabs.AddFileTab(@"C:\project\README.md");
        tab.IsEditable = true;
        tab.IsDirty = true;

        tabs.ConfirmCloseDirtyTabAsync = _ => Task.FromResult(AccelDialogChoice.Primary);
        var savedTabs = new List<TabViewModel>();
        tabs.SaveFileAsync = t =>
        {
            savedTabs.Add(t);
            return Task.FromResult(true);
        };

        await tabs.CloseTabAsync(tab);

        Assert.Same(tab, Assert.Single(savedTabs));
        Assert.DoesNotContain(tab, tabs.Tabs);
    }

    [Fact]
    public async Task CloseTabAsync_OnADirtyTab_SaveThatFails_LeavesTheTabOpenAndDoesNotClose()
    {
        var (tabs, _, _, _) = Build();
        var tab = tabs.AddFileTab(@"C:\project\README.md");
        tab.IsEditable = true;
        tab.IsDirty = true;

        tabs.ConfirmCloseDirtyTabAsync = _ => Task.FromResult(AccelDialogChoice.Primary);
        tabs.SaveFileAsync = _ => Task.FromResult(false);

        await tabs.CloseTabAsync(tab);

        Assert.Contains(tab, tabs.Tabs);
    }

    [Fact]
    public async Task CloseTabAsync_OnADirtyTab_Discard_ClosesTheTabWithoutSaving()
    {
        var (tabs, _, _, _) = Build();
        var tab = tabs.AddFileTab(@"C:\project\README.md");
        tab.IsEditable = true;
        tab.IsDirty = true;

        tabs.ConfirmCloseDirtyTabAsync = _ => Task.FromResult(AccelDialogChoice.Secondary);
        var saveCalled = false;
        tabs.SaveFileAsync = _ =>
        {
            saveCalled = true;
            return Task.FromResult(true);
        };

        await tabs.CloseTabAsync(tab);

        Assert.False(saveCalled);
        Assert.DoesNotContain(tab, tabs.Tabs);
    }

    [Fact]
    public async Task CloseTabAsync_OnADirtyTab_WithNoConfirmHookWired_ClosesUnprompted()
    {
        // Mirrors every other Tabs-optional hook in this ViewModel (ShowFileAsync, SaveFileAsync, ...):
        // a null hook (tests, the pure-scaffolding construction path) must not block a close outright.
        var (tabs, _, _, _) = Build();
        var tab = tabs.AddFileTab(@"C:\project\README.md");
        tab.IsEditable = true;
        tab.IsDirty = true;

        await tabs.CloseTabAsync(tab);

        Assert.DoesNotContain(tab, tabs.Tabs);
    }

    [Fact]
    public async Task CloseTabAsync_OnACleanEditableTab_NeverAsksTheConfirmHook()
    {
        var (tabs, _, _, _) = Build();
        var tab = tabs.AddFileTab(@"C:\project\README.md");
        tab.IsEditable = true;
        tab.IsDirty = false;

        var confirmCalled = false;
        tabs.ConfirmCloseDirtyTabAsync = _ =>
        {
            confirmCalled = true;
            return Task.FromResult(AccelDialogChoice.Cancel);
        };

        await tabs.CloseTabAsync(tab);

        Assert.False(confirmCalled);
        Assert.DoesNotContain(tab, tabs.Tabs);
    }
}

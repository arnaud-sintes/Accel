namespace Glaude.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Glaude.App.Services;
using Glaude.App.ViewModels;
using Glaude.Orchestration;
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

    public FakePtySessionHost(params string[] tabIds) => _tabIds = tabIds.ToList();

    public event EventHandler<PtySessionEndedEventArgs>? SessionEnded;

    /// <summary>Every tabId <see cref="CloseAsync"/> was called with, in order - the assertion surface for
    /// "tab close routes through the registry".</summary>
    public List<string> Closed { get; } = new();

    /// <summary>Whether anything is still subscribed to <see cref="SessionEnded"/>.</summary>
    public bool HasSubscribers => SessionEnded is not null;

    public IReadOnlyList<string> TabIds() => _tabIds.ToArray();

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
}

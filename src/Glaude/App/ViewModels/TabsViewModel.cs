namespace Glaude.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Glaude.App.Services;
using Glaude.Orchestration;

/// <summary>
/// One tab in panel C - a projection of one <c>PtyRegistry</c> registration, never a second copy of its
/// state. Holds no <see cref="PtySession"/> reference at all (see <see cref="IPtySessionHost"/> for why),
/// so nothing here can dispose a session.
/// </summary>
public sealed partial class TabViewModel : ObservableObject
{
    public TabViewModel(string tabId, string title)
    {
        ArgumentException.ThrowIfNullOrEmpty(tabId);
        TabId = tabId;
        _title = string.IsNullOrWhiteSpace(title) ? ShortId(tabId) : title;
    }

    /// <summary>The registry tabId, which is also the <c>--session-id</c> GUID and therefore the id panel
    /// A keys its session rows on - see <c>MainWindow.CreateSession_Click</c>.</summary>
    public string TabId { get; }

    /// <summary>First 8 characters of the tabId, as a fallback label / a stable short identifier.</summary>
    public string ShortTabId => ShortId(TabId);

    [ObservableProperty]
    private string _title;

    /// <summary>
    /// True once the session behind this tab has ended. Set from <c>PtyRegistry.SessionEnded</c> with
    /// <see cref="PtySessionExitReason.ChildExited"/> - i.e. `claude` finished, the user typed
    /// <c>exit</c>, or it crashed. The tab deliberately <b>stays</b> in the strip in that case (with
    /// frozen scrollback) instead of vanishing: a session disappearing with no explanation is the failure
    /// mode P3-T2's own notes call out, and P4-T5 will build the real exit banner on top of this flag.
    /// A tab closed by the user is removed outright, so this flag is never seen for that path.
    /// </summary>
    [ObservableProperty]
    private bool _hasEnded;

    /// <summary>The child's exit code, if it was observed.</summary>
    [ObservableProperty]
    private int? _exitCode;

    /// <summary>What the tab strip shows next to the title: nothing while running, "(ended)"/"(exited N)"
    /// afterwards. Kept as text rather than a colour so the state is not colour-only (panel A's P1-T4
    /// accessibility rule applies here too), but the real styling is out of scope for P3-T1.</summary>
    public string StatusSuffix => HasEnded ? (ExitCode is { } code ? $"(exited {code})" : "(ended)") : string.Empty;

    /// <summary>Accessible description of the whole tab, for <c>AutomationProperties.Name</c>.</summary>
    public string AutomationDescription =>
        HasEnded ? $"Session tab: {Title}. Ended {StatusSuffix}." : $"Session tab: {Title}. Running.";

    partial void OnHasEndedChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusSuffix));
        OnPropertyChanged(nameof(AutomationDescription));
    }

    partial void OnExitCodeChanged(int? value)
    {
        OnPropertyChanged(nameof(StatusSuffix));
        OnPropertyChanged(nameof(AutomationDescription));
    }

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(AutomationDescription));

    private static string ShortId(string tabId) => tabId.Length <= 8 ? tabId : tabId[..8];

    public override string ToString() => $"{Title} {StatusSuffix}".TrimEnd();
}

/// <summary>
/// P3-T1: panel C's ViewModel - the tab strip above panel D, one tab per open <c>PtySession</c>.
///
/// <para><b>It is the only writer of the focused session id</b> (locked-in decision 8). It holds an
/// <see cref="ISessionSelectionWriter"/>, which <see cref="SessionSelectionService.AcquireWriter"/> yields
/// exactly once per service, and every other panel gets the read-only
/// <see cref="ISessionSelectionService"/>. Selecting a tab (from the UI or via
/// <see cref="SelectTab(string)"/>) writes through that writer; nothing else in the app can.</para>
///
/// <para><b>It never owns a session.</b> Tabs are a projection of <see cref="IPtySessionHost"/> (i.e.
/// <c>PtyRegistry</c>): <see cref="CloseTabAsync"/> calls <see cref="IPtySessionHost.CloseAsync"/> and
/// <b>never</b> <see cref="PtySession.Dispose"/> - which is not merely a rule followed here but one that
/// cannot be broken, since the host interface hands out no session references in the first place. Session
/// state is not duplicated either: this class holds tabIds plus per-tab presentation flags, and re-reads
/// <see cref="IPtySessionHost.TabIds"/> whenever it needs the truth (see
/// <see cref="SyncFromHost"/>).</para>
///
/// <para><b>Panel D hosts ONE <c>TerminalView</c> that is reattached per selection</b>, rather than one
/// per tab with visibility toggling. Reasons, in order of weight: (1) each <c>TerminalView</c> is a
/// WebView2 instance, i.e. its own browser/renderer/GPU process trio plus a shared user-data folder -
/// N tabs would mean 3N extra processes and N copies of xterm.js, for panels that are invisible; (2)
/// P2-T5b's <c>glaudeAttachPty</c> was already written to be re-callable (it closes the previous socket,
/// resets its accumulator, and identity-checks the old socket's late <c>onclose</c> against the new one -
/// a bug it was fixed for precisely because <c>terminal-e2e-smoke-test</c> reattaches the same control to
/// a second tabId), so reattaching is the already-proven path; (3) the WebSocket route is keyed by tabId,
/// so one control can serve any tab with no extra server state. The known cost is that scrollback is not
/// preserved across a tab switch - the reattached xterm shows the new session's output from the moment of
/// attach. Per-tab scrollback is presentation polish (P4-T5 keeps a frozen buffer for an <i>ended</i>
/// tab) and is deliberately out of scope here.</para>
///
/// <para><b>Threading.</b> Every mutation happens on the UI thread: <see cref="IPtySessionHost.SessionEnded"/>
/// arrives on a thread-pool thread and is marshalled through <see cref="IUiThreadDispatcher"/>, the same
/// discipline <c>RootsPanelViewModel</c> uses for telemetry snapshots.</para>
/// </summary>
public sealed partial class TabsViewModel : ObservableObject, IDisposable
{
    private readonly IPtySessionHost _host;
    private readonly ISessionSelectionWriter _selection;
    private readonly IUiThreadDispatcher _dispatcher;
    private bool _disposed;

    /// <summary>
    /// TabIds currently being torn down via <see cref="StopTabAsync"/> rather than
    /// <see cref="CloseTabAsync(TabViewModel?)"/> - both routes end up producing the exact same
    /// <see cref="PtySessionExitReason.TornDown"/> notification from <see cref="_host"/>, so this is the
    /// only way <see cref="OnSessionEnded"/> can tell "the user asked to stop this (keep the tab, show an
    /// exit banner)" apart from "the user closed this (remove the tab)" once that notification arrives.
    /// A tabId is only ever removed by <see cref="OnSessionEnded"/> itself (never proactively after the
    /// awaited close, which would race the event's own independently-dispatched delivery) - except for
    /// the one case where no such event will ever come: <see cref="StopTabAsync"/> removes it itself when
    /// <see cref="_host"/> reports <see cref="PtyCloseOutcome.NotFound"/>, since a no-op close raises
    /// nothing for <see cref="OnSessionEnded"/> to consume.
    /// </summary>
    private readonly HashSet<string> _stopping = new(StringComparer.OrdinalIgnoreCase);

    public TabsViewModel(IPtySessionHost host, ISessionSelectionWriter selection, IUiThreadDispatcher dispatcher)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        _host.SessionEnded += OnSessionEnded;
        SyncFromHost();
    }

    /// <summary>The tabs, in the order they were opened.</summary>
    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    /// <summary>
    /// Panel D's attach hook: <c>tabId =&gt; TerminalView.AttachPtyAsync(tabId, port)</c>, set by the
    /// window that owns the single <c>TerminalView</c> (see this class's remarks). Left null in tests and
    /// in the pure-scaffolding construction path, in which case selection still updates
    /// <see cref="ISessionSelectionService.FocusedSessionId"/> and simply does not touch a terminal.
    /// A failing attach is swallowed (logged nowhere yet, as P2-T5b's own attach call site already does):
    /// it must not leave the selection half-applied or crash the UI thread.
    /// </summary>
    public Func<string, Task>? AttachTerminalAsync { get; set; }

    /// <summary>Awaitable form of the most recent attach, for the smoke-test/verification paths that need
    /// to know when panel D has finished reattaching. Null before the first selection.</summary>
    public Task? LastAttach { get; private set; }

    /// <summary>The selected tab. Two-way bound to panel C's <c>ListBox.SelectedItem</c>; assigning it is
    /// what writes the focused session id.</summary>
    [ObservableProperty]
    private TabViewModel? _selectedTab;

    /// <summary>Convenience for the view/tests: the selected tabId, or null.</summary>
    public string? SelectedTabId => SelectedTab?.TabId;

    /// <summary>Whether there are no tabs at all - lets panel C show an empty-state hint instead of a bare
    /// strip.</summary>
    public bool IsEmpty => Tabs.Count == 0;

    partial void OnSelectedTabChanged(TabViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedTabId));

        // The single write path for the whole app.
        _selection.SetFocused(value?.TabId);

        if (value is null || AttachTerminalAsync is null)
        {
            return;
        }

        LastAttach = AttachSafelyAsync(value.TabId);
    }

    /// <summary>
    /// Adds a tab for a session that has just been registered under <paramref name="tabId"/> in the
    /// registry, and selects it (a newly created session is what the user wants to look at). Idempotent
    /// for a tabId that already has a tab - it is selected instead of duplicated.
    /// </summary>
    public TabViewModel AddTab(string tabId, string? title = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(tabId);

        var existing = Find(tabId);
        if (existing is not null)
        {
            SelectedTab = existing;
            return existing;
        }

        var tab = new TabViewModel(tabId, title ?? string.Empty);
        Tabs.Add(tab);
        OnPropertyChanged(nameof(IsEmpty));
        SelectedTab = tab;
        return tab;
    }

    /// <summary>Selects the tab for <paramref name="tabId"/> if there is one; a no-op otherwise (a stale
    /// UI command for a tab that has since been closed must not throw).</summary>
    public void SelectTab(string? tabId)
    {
        if (string.IsNullOrEmpty(tabId))
        {
            SelectedTab = null;
            return;
        }

        var tab = Find(tabId);
        if (tab is not null)
        {
            SelectedTab = tab;
        }
    }

    /// <summary>
    /// Closes a tab <b>through the registry</b> - <see cref="IPtySessionHost.CloseAsync"/>, i.e.
    /// <c>PtyRegistry.CloseAsync</c>, which is the single owner of <see cref="PtySession.Dispose"/>. The
    /// tab is removed here rather than waiting for <c>SessionEnded</c>, so the strip reacts immediately;
    /// the event still arrives (with <see cref="PtySessionExitReason.TornDown"/>) and finds nothing to do.
    /// </summary>
    [RelayCommand]
    public async Task CloseTabAsync(TabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        RemoveTab(tab);

        // CloseAsync never throws and reports failures as data (PtyCloseResult); there is nothing
        // actionable for the tab strip in the result today - the tab is gone either way, and a
        // force-kill/failed-kill is the registry's own concern (and its logging surface, P3-T4).
        await _host.CloseAsync(tab.TabId).ConfigureAwait(true);
    }

    /// <summary>String overload for call sites that only have the id (e.g. a keyboard shortcut or a
    /// scripted verification), matching the task's <c>CloseTab(tabId)</c> shape.</summary>
    public Task CloseTabAsync(string tabId) => CloseTabAsync(Find(tabId));

    /// <summary>
    /// P4-T5: kills the tab's session (through <see cref="IPtySessionHost.CloseAsync"/>, exactly the same
    /// graceful-then-forced teardown <see cref="CloseTabAsync(TabViewModel?)"/> uses - P3-T2/P3-T4's
    /// mechanism is reused verbatim, nothing about it is reimplemented here) but, unlike
    /// <see cref="CloseTabAsync(TabViewModel?)"/>, deliberately does <b>not</b> remove the tab: it stays in
    /// the strip with its scrollback frozen and an exit banner, indistinguishable from a session that
    /// happened to end on its own (see <see cref="MarkEnded"/>) - "stopped by the user" and "ended by
    /// itself" are the same state as far as the rest of this app is concerned. A no-op for a tab that has
    /// already ended, or whose session is not (or no longer) registered.
    /// </summary>
    [RelayCommand]
    public async Task StopTabAsync(TabViewModel? tab)
    {
        if (tab is null || tab.HasEnded)
        {
            return;
        }

        _stopping.Add(tab.TabId);
        var result = await _host.CloseAsync(tab.TabId).ConfigureAwait(true);
        if (result.Outcome == PtyCloseOutcome.NotFound)
        {
            // Nothing was actually closed, so no SessionEnded notification will ever arrive to consume
            // this flag - leaving it would leak it forever (harmlessly, but pointlessly).
            _stopping.Remove(tab.TabId);
        }
    }

    /// <summary>
    /// Reconciles the strip against the registry: adds a tab for any registered tabId that has none (e.g.
    /// a session registered by another code path, or one that existed before this ViewModel was built),
    /// and marks as ended any running tab whose session is no longer registered. Never invents registry
    /// state - the registry is always the source of truth, this is a projection of it.
    /// </summary>
    public void SyncFromHost()
    {
        var live = new HashSet<string>(_host.TabIds(), StringComparer.OrdinalIgnoreCase);

        foreach (var tabId in live.Where(id => Find(id) is null).ToArray())
        {
            var tab = new TabViewModel(tabId, string.Empty);
            Tabs.Add(tab);
        }

        foreach (var tab in Tabs.Where(t => !t.HasEnded && !live.Contains(t.TabId)).ToArray())
        {
            MarkEnded(tab, null);
        }

        OnPropertyChanged(nameof(IsEmpty));
        SelectedTab ??= Tabs.FirstOrDefault(t => !t.HasEnded);
    }

    private async Task AttachSafelyAsync(string tabId)
    {
        try
        {
            var attach = AttachTerminalAsync;
            if (attach is not null)
            {
                await attach(tabId).ConfigureAwait(true);
            }
        }
        catch
        {
            // Same posture as P2-T5b's original attach call site: a failed attach (WebView2 not ready,
            // the route gone) must not corrupt selection state or crash the UI thread. The session stays
            // registered and the user can reselect the tab to retry.
        }
    }

    /// <summary>
    /// The registry's self-exit/teardown notification. <see cref="PtySessionExitReason.ChildExited"/>
    /// means the session ended by itself, so the tab is kept and flagged (never silently removed - the
    /// user needs to see that it ended and why). <see cref="PtySessionExitReason.TornDown"/> means Glaude
    /// closed it, in which case <see cref="CloseTabAsync"/> already removed the tab and this is a no-op.
    /// </summary>
    private void OnSessionEnded(object? sender, PtySessionEndedEventArgs e) => _dispatcher.Post(() =>
    {
        if (_disposed)
        {
            return;
        }

        var tab = Find(e.TabId);
        if (tab is null)
        {
            return;
        }

        if (e.Reason == PtySessionExitReason.TornDown)
        {
            // Both CloseTabAsync and StopTabAsync produce this same reason - _stopping is the only signal
            // that distinguishes "the user asked to stop this one (keep it, mark it ended)" from "the
            // user closed this one (remove it)"; see _stopping's own remarks.
            if (_stopping.Remove(e.TabId))
            {
                MarkEnded(tab, e.ExitCode);
            }
            else
            {
                RemoveTab(tab);
            }

            return;
        }

        MarkEnded(tab, e.ExitCode);
    });

    private void MarkEnded(TabViewModel tab, int? exitCode)
    {
        tab.ExitCode = exitCode;
        tab.HasEnded = true;

        // An ended tab keeps its scrollback but must not keep the focus: panels B/E (and panel A's
        // highlight) would otherwise stay pointed at a session that no longer exists. Move to another
        // live tab if there is one, else clear the focus.
        if (ReferenceEquals(SelectedTab, tab))
        {
            SelectedTab = Tabs.FirstOrDefault(t => !t.HasEnded && !ReferenceEquals(t, tab));
        }
    }

    private void RemoveTab(TabViewModel tab)
    {
        int index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        bool wasSelected = ReferenceEquals(SelectedTab, tab);
        Tabs.RemoveAt(index);
        OnPropertyChanged(nameof(IsEmpty));

        if (!wasSelected)
        {
            return;
        }

        // Prefer the neighbour to the left (the usual tab-strip behaviour), then anything left.
        SelectedTab = Tabs.Count == 0
            ? null
            : Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    private TabViewModel? Find(string tabId) =>
        Tabs.FirstOrDefault(t => string.Equals(t.TabId, tabId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Unsubscribes from the registry. Deliberately does <b>not</b> close any session: the
    /// registry owns those, and app-exit teardown is P3-T4's <c>CloseAllAsync</c>/<c>Dispose</c>.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.SessionEnded -= OnSessionEnded;
    }
}

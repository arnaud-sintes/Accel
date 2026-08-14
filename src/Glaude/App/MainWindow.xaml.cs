namespace Glaude.App;

using System;
using System.Windows;
using Glaude.App.Services;
using Glaude.App.ViewModels;
using Glaude.Orchestration;
using Glaude.Server;

/// <summary>
/// The WPF shell's main window. Layout is still P1-T1b's scaffolding (menu bar +
/// GridSplitter-separated placeholder panels B/C/D/E); P1-T2 gives panel A a real
/// <see cref="RootsPanelViewModel"/> and binds its <c>TreeView</c> to it.
///
/// <para>Composition is deliberately constructor-injected and minimal - no DI container: the window
/// takes the panel ViewModel(s) it hosts and assigns them as the corresponding panel's
/// <c>DataContext</c>. See <c>Program.cs</c>'s dev-only <c>ui-preview</c> verb for the only
/// composition point that currently builds the graph (feed + dispatcher + ViewModel); wiring the
/// shell into the real `glaude` startup path is a later task.</para>
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Scaffolding/designer constructor - panel A renders as an empty tree.</summary>
    public MainWindow()
        : this(null)
    {
    }

    public MainWindow(RootsPanelViewModel? rootsPanel)
        : this(rootsPanel, null, 0)
    {
    }

    /// <summary>
    /// P2-T5b's overload, kept so nothing that predates P3-T1 has to change: no tab strip and no session
    /// registry means the "Create session" menu item can still launch and attach a session, it just has
    /// no tab (see <see cref="CreateSession_Click"/>).
    /// </summary>
    public MainWindow(RootsPanelViewModel? rootsPanel, PtyRouteRegistry? ptyRouteRegistry, int ptyWebSocketPort)
        : this(rootsPanel, ptyRouteRegistry, ptyWebSocketPort, null, null)
    {
    }

    /// <summary>
    /// P3-T1's real composition: <paramref name="tabs"/> is panel C's tab strip (the only writer of
    /// <c>ISessionSelectionService</c>) and <paramref name="sessionRegistry"/> is the app-lifetime
    /// <see cref="PtyRegistry"/> that owns every live session's lifetime. Together they replace P2-T6's
    /// stopgap <c>List&lt;(tabId, session)&gt;</c> ownership bridge, which disposed sessions itself and is
    /// gone from this file entirely.
    ///
    /// <para><paramref name="ptyRouteRegistry"/>/<paramref name="ptyWebSocketPort"/> stay as P2-T5b left
    /// them: the <c>tabId -&gt; IPtyEndpoint</c> map behind the <c>/pty/{tabId}</c> WebSocket route (a
    /// different registry from <see cref="PtyRegistry"/> - that one owns process lifetime, this one owns
    /// route reachability), plus the loopback port its Kestrel host is bound to. Every parameter is
    /// optional and null degrades gracefully, so the designer/scaffolding paths still work.</para>
    /// </summary>
    public MainWindow(
        RootsPanelViewModel? rootsPanel,
        PtyRouteRegistry? ptyRouteRegistry,
        int ptyWebSocketPort,
        TabsViewModel? tabs,
        PtyRegistry? sessionRegistry)
        : this(rootsPanel, ptyRouteRegistry, ptyWebSocketPort, tabs, sessionRegistry, null)
    {
    }

    /// <summary>
    /// P3-T3: <paramref name="selection"/> feeds panels B/E's stub <see cref="FocusedSessionStubViewModel"/>
    /// readers - the same read-only <see cref="ISessionSelectionService"/> panel A already consumes for
    /// <c>IsFocused</c>. Null degrades exactly like every other optional parameter here: the panel keeps its
    /// P1-T1b placeholder text with no status line beneath it.
    /// </summary>
    public MainWindow(
        RootsPanelViewModel? rootsPanel,
        PtyRouteRegistry? ptyRouteRegistry,
        int ptyWebSocketPort,
        TabsViewModel? tabs,
        PtyRegistry? sessionRegistry,
        ISessionSelectionService? selection)
    {
        InitializeComponent();

        RootsPanel = rootsPanel;
        Tabs = tabs;
        _ptyRouteRegistry = ptyRouteRegistry;
        _ptyWebSocketPort = ptyWebSocketPort;
        _sessionRegistry = sessionRegistry;

        if (rootsPanel is not null)
        {
            // Scoped to panel A only - deliberately not Window.DataContext, so the remaining
            // placeholder panels can't accidentally start binding against panel A's ViewModel
            // (locked-in decision 8: no point-to-point panel bindings).
            PanelA.DataContext = rootsPanel;
        }

        if (tabs is not null)
        {
            PanelC.DataContext = tabs;

            // Panel D hosts exactly ONE TerminalView, reattached per selected tab (see TabsViewModel's
            // class remarks for why one-and-reattach beats one-control-per-tab). This is the only place
            // that knows both the control and the port, so the attach hook is wired here rather than
            // making the ViewModel aware of WPF.
            tabs.AttachTerminalAsync = tabId => Terminal.AttachPtyAsync(tabId, _ptyWebSocketPort);
        }

        if (selection is not null)
        {
            // Two independent instances, not one shared DataContext: each is a plain reader with no
            // panel-specific state yet, and Phases 5/6 replace both outright rather than share them.
            _panelBStub = new FocusedSessionStubViewModel(selection);
            _panelEStub = new FocusedSessionStubViewModel(selection);
            PanelB.DataContext = _panelBStub;
            PanelE.DataContext = _panelEStub;
        }

        Closed += (_, _) =>
        {
            // No session teardown here any more: PtyRegistry is the single owner of PtySession.Dispose
            // (P3-T2) and app-exit teardown is P3-T4's job (CloseAllAsync/Dispose around the app loop).
            // This only drops the tab strip's registry subscription plus panels B/E's own.
            Tabs?.Dispose();
            _panelBStub?.Dispose();
            _panelEStub?.Dispose();
        };
    }

    private readonly FocusedSessionStubViewModel? _panelBStub;
    private readonly FocusedSessionStubViewModel? _panelEStub;

    /// <summary>Panel A's ViewModel, or null when the window was constructed as bare scaffolding.</summary>
    public RootsPanelViewModel? RootsPanel { get; }

    /// <summary>Panel C's ViewModel (the tab strip), or null in the scaffolding paths.</summary>
    public TabsViewModel? Tabs { get; }

    /// <summary>
    /// The <c>tabId -&gt; IPtyEndpoint</c> registry backing whichever <c>EventServer</c> instance's
    /// Kestrel host is actually listening (null means "no terminal wiring available", e.g. the
    /// pure-scaffolding construction path).
    /// </summary>
    private readonly PtyRouteRegistry? _ptyRouteRegistry;

    /// <summary>The port that registry's owning Kestrel instance is bound to.</summary>
    private readonly int _ptyWebSocketPort;

    /// <summary>
    /// P3-T2's registry: the app-lifetime <c>tabId -&gt; PtySession</c> map and the only thing allowed to
    /// dispose a session. Null in the scaffolding paths, in which case a created session is registered
    /// for the route only and left for the job object to reap.
    /// </summary>
    private readonly PtyRegistry? _sessionRegistry;

    /// <summary>
    /// P2-T6 + P3-T1: opens the "Create session" dialog modally. On confirm, the dialog's ViewModel has
    /// already generated the session GUID, built the argv array, resolved/validated the launch spec, and
    /// started a live <see cref="PtySession"/> (see <see cref="CreateSessionDialogViewModel.Confirm"/>).
    /// This handler now gives that session a real owner and a real tab:
    /// <list type="number">
    /// <item><b>tabId = the session GUID.</b> Deliberately <see cref="CreateSessionDialogViewModel.LastGeneratedSessionId"/>
    /// rendered with <c>ToString()</c> - the same "D" (dashed) form the dialog passed to
    /// <c>--session-id</c> - and not a second, unrelated <c>Guid.NewGuid()</c> as P2-T5b's stopgap used.
    /// That equality is load-bearing rather than cosmetic: it is what lets panel A (whose session rows are
    /// keyed by the transcript's session id, dashed) light up as focused when panel C selects a tab, and
    /// it keeps the registry, the <c>/pty/{tabId}</c> route and `claude`'s own id one value instead of
    /// three. The dashed form is required for the panel-A match; the id is still an unguessable GUID, so
    /// the route's security posture is unchanged.</item>
    /// <item><b>Registered with <see cref="PtyRegistry"/> first</b>, which takes ownership of disposal
    /// (nothing in this file disposes a session any more), then with the route registry so the WebSocket
    /// can reach it.</item>
    /// <item><b>A tab is added to panel C</b>, which selects it, which writes the focused session id and
    /// reattaches panel D's terminal to this session - so the attach happens through the ordinary
    /// selection path, not a special create-time one.</item>
    /// </list>
    /// </summary>
    private void CreateSession_Click(object sender, RoutedEventArgs e)
    {
        // Defaults the new session's working directory to whichever root the user currently has
        // selected in panel A (a root row itself, or a session/agent row under one) - see
        // RootsPanelViewModel.SelectedRootPath - falling back to the first configured root that
        // still exists on disk when nothing is selected. Deliberately never left null/blank here:
        // an unset working directory has claude inherit Glaude's own process directory (its build
        // output folder) as the child's cwd - not a real project, and not something the user has
        // ever trusted - so Claude Code's first-run trust prompt blocks the session until someone
        // notices and answers it inside the terminal, which made a freshly created session look
        // like it had simply never started (reported bug: "no opened session visible in panel A").
        // The dialog's own working-directory field is still fully editable/browsable before confirm.
        var initialWorkingDirectory = RootsPanel?.SelectedRootPath ?? RootsPanel?.FirstAvailableRootPath;
        var viewModel = new CreateSessionDialogViewModel(initialWorkingDirectory: initialWorkingDirectory);
        var dialog = new CreateSessionDialog(viewModel) { Owner = this };
        dialog.ShowDialog();

        if (!dialog.Confirmed || viewModel.LastStartedSession is not { } session)
        {
            return;
        }

        // The GUID the dialog already generated for --session-id IS the tabId (see this method's doc).
        string tabId = (viewModel.LastGeneratedSessionId ?? Guid.NewGuid()).ToString();

        try
        {
            _sessionRegistry?.Register(tabId, session);
        }
        catch (Exception)
        {
            // Register only throws for a duplicate tabId (impossible for a fresh GUID) or a disposed
            // registry (the app is shutting down - it has already started closing this session, see
            // Register's own contract). Either way there is nothing useful to add to the UI here, and
            // the session must not be disposed from this file.
            return;
        }

        _ptyRouteRegistry?.RegisterSession(tabId, session);

        // Selecting the new tab is what attaches panel D (TabsViewModel.AttachTerminalAsync, wired in
        // the constructor). Nothing is awaited: AddTab is synchronous and the attach is fire-and-forget
        // by design, exactly as P2-T5b's own call site was.
        Tabs?.AddTab(tabId, string.IsNullOrWhiteSpace(viewModel.DisplayName) ? null : viewModel.DisplayName);
    }
}

namespace Glaude.App;

using System;
using System.Collections.Generic;
using System.Windows;
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
    /// P2-T5b: <paramref name="ptyRegistry"/>/<paramref name="ptyWebSocketPort"/> are this task's
    /// own minimal stopgap for getting a `tabId` + a reachable <c>/pty/{tabId}</c> WebSocket route
    /// end to end, ahead of Phase 3's real <c>PtyRegistry</c>/<c>TabsViewModel</c> — see
    /// <see cref="CreateSession_Click"/>. Both are optional and default to "terminal wiring
    /// disabled" (null registry), which is what every existing caller of the single-argument
    /// constructor still gets — this constructor overload does not change behaviour for anything
    /// that predates P2-T5b.
    /// </summary>
    public MainWindow(RootsPanelViewModel? rootsPanel, PtyRouteRegistry? ptyRegistry, int ptyWebSocketPort)
    {
        InitializeComponent();

        RootsPanel = rootsPanel;
        _ptyRegistry = ptyRegistry;
        _ptyWebSocketPort = ptyWebSocketPort;
        if (rootsPanel is not null)
        {
            // Scoped to panel A only - deliberately not Window.DataContext, so the remaining
            // placeholder panels can't accidentally start binding against panel A's ViewModel
            // (locked-in decision 8: no point-to-point panel bindings).
            PanelA.DataContext = rootsPanel;
        }

        Closed += (_, _) =>
        {
            // P2-T6: temporary ownership bridge (see CreateSession_Click) - Phase 3's PtyRegistry
            // is the real owner once it exists. Disposing here is what stops a session created
            // through this menu item from outliving the window as an unreachable, un-disposed
            // object (the child process itself is still safety-netted by GlaudeJobObject.Shared's
            // kill-on-close regardless, but a graceful Dispose is still the right thing to attempt
            // first).
            foreach (var (tabId, session) in _sessionsPendingRegistry)
            {
                _ptyRegistry?.Unregister(tabId);
                try
                {
                    session.Dispose();
                }
                catch
                {
                    // Best-effort only on the way out.
                }
            }

            _sessionsPendingRegistry.Clear();
        };
    }

    /// <summary>Panel A's ViewModel, or null when the window was constructed as bare scaffolding.</summary>
    public RootsPanelViewModel? RootsPanel { get; }

    /// <summary>
    /// P2-T5b/P2-T6 stopgap: the <c>tabId -&gt; IPtyEndpoint</c> registry backing whichever
    /// <c>EventServer</c> instance's Kestrel host is actually listening (null means "no terminal
    /// wiring available", e.g. the pure-scaffolding/ui-preview-without-a-server construction path).
    /// </summary>
    private readonly PtyRouteRegistry? _ptyRegistry;

    /// <summary>The port that registry's owning Kestrel instance is bound to.</summary>
    private readonly int _ptyWebSocketPort;

    /// <summary>
    /// P2-T6: sessions started through the "Create session" dialog before Phase 3's
    /// <c>PtyRegistry</c>/<c>TabsViewModel</c> exist to actually own them. This is a deliberately
    /// minimal, temporary bridge - not a registry - so a session created from this menu item is at
    /// least reachable and disposed on window close rather than immediately unreferenced. Phase 3
    /// replaces this list outright; nothing here is meant to survive that refactor. Each entry's
    /// tabId is whatever it was registered under in <see cref="_ptyRegistry"/> (P2-T5b addition),
    /// so window-close teardown can unregister it, not just dispose the session.
    /// </summary>
    private readonly List<(string TabId, PtySession Session)> _sessionsPendingRegistry = new();

    /// <summary>
    /// P2-T6: opens the "Create session" dialog modally. On confirm, the dialog's ViewModel has
    /// already generated the session GUID, built the argv array, resolved/validated the launch spec,
    /// and started a live <see cref="PtySession"/> (see <see cref="CreateSessionDialogViewModel.Confirm"/>) -
    /// this handler's job is to keep that session reachable (<see cref="_sessionsPendingRegistry"/>)
    /// until Phase 3 gives it a real owner, and — P2-T5b's addition — to register it under a fresh
    /// tabId in <see cref="_ptyRegistry"/> (if one was supplied) and attach panel D's terminal to
    /// it over the WebSocket route, so the newly created session is actually visible/interactive
    /// rather than just alive in the background.
    /// </summary>
    private async void CreateSession_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = new CreateSessionDialogViewModel();
        var dialog = new CreateSessionDialog(viewModel) { Owner = this };
        dialog.ShowDialog();

        if (!dialog.Confirmed || viewModel.LastStartedSession is not { } session)
        {
            return;
        }

        string tabId = Guid.NewGuid().ToString("N");
        _sessionsPendingRegistry.Add((tabId, session));

        if (_ptyRegistry is null)
        {
            // No server/registry was supplied (e.g. the pure-scaffolding construction path) -
            // the session is still started and kept reachable above, it is just not wired to
            // panel D's terminal.
            return;
        }

        _ptyRegistry.RegisterSession(tabId, session);
        try
        {
            await Terminal.AttachPtyAsync(tabId, _ptyWebSocketPort);
        }
        catch
        {
            // Best-effort: a failed attach (e.g. WebView2 not yet initialized) must not leave the
            // session un-tracked or crash the UI thread - the session stays registered and
            // reachable; the user can be given a retry affordance in a later phase.
        }
    }
}

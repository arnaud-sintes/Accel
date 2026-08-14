namespace Glaude.App;

using System.Collections.Generic;
using System.Windows;
using Glaude.App.ViewModels;
using Glaude.Orchestration;

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
    {
        InitializeComponent();

        RootsPanel = rootsPanel;
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
            foreach (var session in _sessionsPendingRegistry)
            {
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
    /// P2-T6: sessions started through the "Create session" dialog before Phase 3's
    /// <c>PtyRegistry</c>/<c>TabsViewModel</c> exist to actually own them. This is a deliberately
    /// minimal, temporary bridge - not a registry - so a session created from this menu item is at
    /// least reachable and disposed on window close rather than immediately unreferenced. Phase 3
    /// replaces this list outright; nothing here is meant to survive that refactor.
    /// </summary>
    private readonly List<PtySession> _sessionsPendingRegistry = new();

    /// <summary>
    /// P2-T6: opens the "Create session" dialog modally. On confirm, the dialog's ViewModel has
    /// already generated the session GUID, built the argv array, resolved/validated the launch spec,
    /// and started a live <see cref="PtySession"/> (see <see cref="CreateSessionDialogViewModel.Confirm"/>) -
    /// this handler's only job is to keep that session reachable (<see cref="_sessionsPendingRegistry"/>)
    /// until Phase 3 gives it a real owner.
    /// </summary>
    private void CreateSession_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = new CreateSessionDialogViewModel();
        var dialog = new CreateSessionDialog(viewModel) { Owner = this };
        dialog.ShowDialog();

        if (dialog.Confirmed && viewModel.LastStartedSession is { } session)
        {
            _sessionsPendingRegistry.Add(session);
        }
    }
}

namespace Accel.App;

using System;
using System.Threading;
using System.Windows;
using Accel.App.Services;
using Accel.Orchestration;

/// <summary>
/// The modal "please wait" shown while the model/effort catalog is read out of Claude Code - see
/// ModelCatalogUpdateDialog.xaml for the layout and for why this blocks rather than running in the
/// background.
///
/// <para><b>Why modal.</b> The catalog feeds the Create-session dialog's model and effort pickers. A
/// background refresh (the original design) meant a user who opened that dialog in the first ~15
/// seconds after startup silently got the built-in fallback list instead of their account's real
/// one - the exact case this feature exists to fix. Blocking once per Claude Code upgrade is the
/// cheaper trade.</para>
///
/// <para><b>Every exit path leaves the app usable.</b> Skip, Escape, the close box, a probe failure
/// and a probe crash all end the same way: the dialog closes and
/// <see cref="ModelCatalogService.Current"/> keeps whatever it already had (a cached catalog, or
/// <see cref="Accel.Metrics.ModelCatalog.BuiltIn"/>). Nothing here can prevent the window from
/// opening, which is the one property a blocking startup dialog absolutely must have.</para>
/// </summary>
public partial class ModelCatalogUpdateDialog : Window
{
    private readonly ModelCatalogService _service;
    private readonly string _probeDirectory;
    private readonly CancellationTokenSource _cancellation = new();

    private bool _finished;
    private bool _closing;

    private ModelCatalogUpdateDialog(ModelCatalogService service, string probeDirectory)
    {
        InitializeComponent();

        _service = service;
        _probeDirectory = probeDirectory;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    /// <summary>The outcome of the run this dialog drove, or null if it was closed before one
    /// completed. Exposed for diagnostics and tests rather than for control flow - a caller has
    /// nothing to decide, since every outcome is already handled.</summary>
    public ModelCatalogProbeOutcome? Outcome { get; private set; }

    /// <summary>
    /// Runs a blocking catalog update if one is warranted, and returns immediately otherwise. Called
    /// from the main window's <c>Loaded</c> handler.
    ///
    /// <para>No dialog appears at all when the cached catalog still matches the installed
    /// <c>claude.exe</c> (the overwhelmingly common case - see
    /// <see cref="ModelCatalogService.NeedsRefresh"/>), or when there is no root folder to run
    /// discovery in. Startup is only ever blocked when there is genuinely something to learn.</para>
    /// </summary>
    public static void RunIfNeeded(Window? owner, ModelCatalogService? service, string? probeDirectory)
    {
        if (service is null || string.IsNullOrWhiteSpace(probeDirectory) || !service.NeedsRefresh())
        {
            return;
        }

        try
        {
            var dialog = new ModelCatalogUpdateDialog(service, probeDirectory)
            {
                Owner = owner ?? Application.Current?.MainWindow,
            };

            dialog.ShowDialog();
        }
        catch (Exception)
        {
            // The catch that makes "nothing here can prevent the window from opening" true rather
            // than merely intended. This runs inside the main window's Loaded handler, and the risk
            // is not the probe (RefreshAsync already reports its failures as outcomes) but this
            // dialog's own construction: XAML StaticResource lookups fail at *runtime*, so renaming
            // a Theme.xaml key would otherwise turn a cosmetic mistake into an app that cannot start.
            // Swallowing it costs a stale model list for one run; not swallowing it costs the app.
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Progress<T> captures this (the UI) thread's SynchronizationContext at construction, so the
        // probe's trace lines - raised on a background thread - land back here without an explicit
        // Dispatcher hop.
        _service.Progress = new Progress<string>(line => StatusText.Text = line);

        try
        {
            var result = await _service.RefreshAsync(_probeDirectory, _cancellation.Token);
            Outcome = result.Outcome;
        }
        catch (Exception)
        {
            // RefreshAsync already swallows probe failures and reports them as outcomes, so reaching
            // here means something more unusual went wrong. It still must not be allowed to escape:
            // this runs during the main window's Loaded, and an exception here would take the app
            // down at startup over a model list.
        }
        finally
        {
            _service.Progress = null;
            _finished = true;

            // Guarded, because this method is `async void`: on the Skip/Escape/close path the window
            // is already gone by the time the await resumes (cancelling the token makes RefreshAsync
            // return promptly), and calling Close() on a window that has already closed throws - which
            // in an async void handler is an unhandled exception on the dispatcher, i.e. a crash, at
            // startup, over a model list.
            if (!_closing)
            {
                Close();
            }
        }
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Skip, Escape and the close box all arrive here while the probe is still running. Cancelling
        // the token is what actually ends it: RefreshAsync returns Cancelled, and the probe's own
        // teardown reaps its `claude` child through that session's Job Object. The window is allowed
        // to close immediately rather than waiting for that, since nothing downstream depends on it.
        _closing = true;

        if (!_finished)
        {
            _cancellation.Cancel();
        }

        _service.Progress = null;
    }
}

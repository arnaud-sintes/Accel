using System.Linq;
using System.Windows.Forms;
using Glaude.Cli;
using Glaude.Server;
using Microsoft.Extensions.DependencyInjection;

// Throwaway, hidden dev-only verb for P1-T1b's/P1-T2's own visual verification of the WPF shell
// (App/App.xaml + App/MainWindow.xaml + panel A's live RootsPanelViewModel) - NOT part of
// ArgParser's documented surface
// and not wired into the real combined-start path. Deliberately checked before ArgParser.Parse
// so it can never collide with or affect the real Verb dispatch/tests. Scope decision: kept
// minimal and undocumented on purpose; wiring the WPF shell into the actual `glaude` startup is
// a later task (P1-T2+), not this one.
if (args.Length > 0 && string.Equals(args[0], "ui-preview", StringComparison.Ordinal))
{
    var verify = args.Length > 1 && string.Equals(args[1], "--verify", StringComparison.Ordinal);
    var uiPreviewThread = new Thread(() => RunUiPreview(verify));
    uiPreviewThread.SetApartmentState(ApartmentState.STA);
    uiPreviewThread.Start();
    uiPreviewThread.Join();
    return 0;
}

// P2-T2: hidden dev-only verb, same rationale and same placement rules as `ui-preview` above -
// ConPtySession's whole point is real OS resource lifecycle (a real pseudoconsole, a real child
// process, real kernel handles), which no unit test with fakes can prove. Checked before
// ArgParser.Parse so it cannot collide with the documented verb surface or its tests. Optional
// iteration count for the handle-leak loop: `pty-smoke-test [iterations]` (default 50).
if (args.Length > 0 && string.Equals(args[0], "pty-smoke-test", StringComparison.Ordinal))
{
    var iterations = args.Length > 1 && int.TryParse(args[1], out var parsedIterations) ? parsedIterations : 50;
    return Glaude.Orchestration.ConPtySmokeTest.Run(Console.Out, iterations);
}

// P2-T3: hidden dev-only verb, same rationale and same placement rules as `pty-smoke-test` above -
// PtySession is real process lifecycle plus a real Job Object, which no unit test with fakes can
// prove. It launches cmd.exe (never claude.exe) for the same reason ConPtySmokeTest does. Optional
// cycle count for the leak loop: `pty-session-smoke-test [cycles]` (default 10).
if (args.Length > 0 && string.Equals(args[0], "pty-session-smoke-test", StringComparison.Ordinal))
{
    var cycles = args.Length > 1 && int.TryParse(args[1], out var parsedCycles) ? parsedCycles : 10;
    return Glaude.Orchestration.PtySessionSmokeTest.Run(Console.Out, cycles);
}

// P2-T5b: hidden dev-only verb, same rationale and placement rules as the smoke tests above -
// proves xterm.js (WebView2, panel D) is actually wired to a live PtySession over a real
// /pty/{tabId} WebSocket route on a real EventServer/Kestrel instance, end to end. No unit test
// can establish this (it needs a real WebView2 + a real loopback socket + a real cmd.exe child).
if (args.Length > 0 && string.Equals(args[0], "terminal-e2e-smoke-test", StringComparison.Ordinal))
{
    return Glaude.App.TerminalE2ESmokeTest.Run(Console.Out);
}

// Runs the WPF shell scaffolding standalone, on its own STA thread (mirrors RunCombinedAsync's
// existing WinForms STA thread below - this process's real Main is not STA, since the combined
// app already needed WinForms on a dedicated thread rather than the process's own). Dev-only,
// see the `ui-preview` check above for why this exists.
static void RunUiPreview(bool verify)
{
    if (verify)
    {
        // P1-T4 verification: WPF binding errors ("System.Windows.Data Error") normally only go
        // to OutputDebugString, invisible to a plain console run - route them to stdout too so
        // `ui-preview --verify`'s own console output is enough to prove the new badge/glyph/
        // colour bindings actually resolved cleanly, without needing an attached debugger.
        System.Diagnostics.PresentationTraceSources.Refresh();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Error;
    }

    var wpfApp = new Glaude.App.App();

    // P1-T2's composition point (dev-only): the panel-A object graph, wired the same way the real
    // startup path eventually will be, but WITHOUT touching RunCombinedAsync - an EventServer
    // instance is constructed for its in-process State/Roots/RootsTree (which is exactly what the
    // telemetry feed reads - no HTTP, no /roots/tree polling), AND (P2-T5b addition) its Kestrel
    // host is actually started on an ephemeral loopback port so the "Create session" menu item
    // (panel D's terminal) has a real /pty/{tabId} route to attach to when clicked interactively -
    // see MainWindow.CreateSession_Click. Best-effort: a failure to start the listener degrades to
    // "terminal wiring unavailable" (ptyRegistry stays null below) rather than aborting the whole
    // preview.
    var server = new EventServer();
    Microsoft.AspNetCore.Builder.WebApplication? ptyWebApp = null;
    int ptyPort = 0;
    try
    {
        ptyWebApp = server.BuildApp(0);
        ptyWebApp.StartAsync().GetAwaiter().GetResult();
        var addressesFeature = ptyWebApp.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        ptyPort = new Uri(addressesFeature!.Addresses.First()).Port;
    }
    catch
    {
        ptyWebApp = null;
    }

    var dispatcher = new Glaude.App.Services.WpfUiThreadDispatcher(
        System.Windows.Threading.Dispatcher.CurrentDispatcher);
    var feed = new Glaude.App.Services.TelemetryFeed(
        new Glaude.App.Services.EventServerTelemetrySource(server),
        dispatcher,
        new Glaude.App.Services.DispatcherDebounceTimer(System.Windows.Threading.Dispatcher.CurrentDispatcher));
    var rootsPanel = new Glaude.App.ViewModels.RootsPanelViewModel(feed, dispatcher);

    var mainWindow = new Glaude.App.MainWindow(
        rootsPanel,
        ptyWebApp is not null ? server.PtySessions : null,
        ptyPort);
    mainWindow.Loaded += (_, _) => rootsPanel.Start();
    mainWindow.Closed += (_, _) =>
    {
        rootsPanel.Dispose();
        feed.Dispose();
        if (ptyWebApp is not null)
        {
            try
            {
                ptyWebApp.StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // Best-effort on the way out.
            }
        }

        // P2-T5: found necessary empirically (see TerminalView.Dispose's own doc comment) -
        // without this, `ui-preview`'s process exits with code 0 while its msedgewebview2.exe
        // child processes (browser/renderer/GPU) linger behind it.
        mainWindow.Terminal.Dispose();
    };

    if (verify)
    {
        // Non-interactive layout check (P1-T1b verification): once the window has laid out,
        // measure every placeholder panel's actual rendered size and print it, then close the
        // window so this can run unattended (e.g. from a CI/manual verification shell) instead
        // of requiring a human to eyeball a live window. A build success alone doesn't prove the
        // splitters/rows/columns didn't collapse to zero, so this is a real measurement, not a
        // guess.
        mainWindow.ContentRendered += (_, _) =>
        {
            mainWindow.Dispatcher.BeginInvoke(new Action(async () =>
            {
                void Report(string name, System.Windows.FrameworkElement element) =>
                    Console.WriteLine($"{name}: {element.ActualWidth:0.#} x {element.ActualHeight:0.#}");

                Report("PanelA", mainWindow.PanelA);

                // P1-T2 verification: prove the data path (feed -> ViewModel -> bound TreeView)
                // actually produced rows, and make "works but empty" (a snapshot arrived, it just
                // has no roots/sessions on this machine) distinguishable from "broken" (no snapshot
                // at all / an exception surfaced in StatusText).
                Console.WriteLine($"PanelA.HasSnapshot: {rootsPanel.HasSnapshot}");
                Console.WriteLine($"PanelA.Status: {rootsPanel.StatusText}");
                Console.WriteLine($"PanelA.Counts: roots={rootsPanel.RootCount} sessions={rootsPanel.SessionCount} live={rootsPanel.LiveSessionCount}");
                Console.WriteLine($"PanelA.TreeViewItems: {mainWindow.RootsTreeView.Items.Count}");

                // Prove the HierarchicalDataTemplate + two-way IsExpanded binding really work at
                // runtime (not just that the ViewModel holds rows): expand the first root through
                // the ViewModel and count the child containers WPF then generates.
                if (rootsPanel.Roots.Count > 0)
                {
                    rootsPanel.Roots[0].IsExpanded = true;
                    mainWindow.RootsTreeView.UpdateLayout();
                    var firstContainer = mainWindow.RootsTreeView.ItemContainerGenerator.ContainerFromIndex(0)
                        as System.Windows.Controls.TreeViewItem;
                    Console.WriteLine($"PanelA.FirstRootContainer.IsExpanded: {firstContainer?.IsExpanded}");
                    Console.WriteLine($"PanelA.FirstRootContainer.ChildItems: {firstContainer?.Items.Count}");
                }

                foreach (var rootNode in rootsPanel.Roots)
                {
                    Console.WriteLine($"  [{rootNode.Kind}] {rootNode.Text} (expanded={rootNode.IsExpanded}, children={rootNode.Children.Count})");
                    foreach (var child in rootNode.Children)
                    {
                        Console.WriteLine($"    [{child.Kind}] {child.Text}");
                    }
                }

                Report("PanelB", mainWindow.PanelB);
                Report("PanelC", mainWindow.PanelC);
                Report("PanelD", mainWindow.PanelD);
                Report("PanelE", mainWindow.PanelE);

                // P2-T5 verification: no live screenshot is available in this environment, so
                // prove panel D's WebView2-hosted xterm.js page actually loaded and initialized
                // without a JS error by awaiting TerminalView.Initialization (CoreWebView2 ready
                // + navigated) and then reading back document.title via ExecuteScriptAsync -
                // index.html sets it to "glaude-terminal-ready" on success or
                // "glaude-terminal-error:<message>" if the xterm.js/FitAddon script threw.
                try
                {
                    await mainWindow.Terminal.Initialization;
                    var titleJson = await mainWindow.Terminal.Browser.CoreWebView2.ExecuteScriptAsync("document.title");
                    Console.WriteLine($"Terminal.document.title: {titleJson}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Terminal.Initialization: FAILED - {ex}");
                }

                mainWindow.Close();
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        };
    }

    wpfApp.Run(mainWindow);
}

// Combined-app entry point (post "one combined app" refactor): running `glaude` with no
// arguments installs the hooks (best-effort - a refusal is printed but never aborts startup),
// starts the Kestrel event server in-process, and opens the WinForms monitor window on its own
// STA thread wired DIRECTLY to that same EventServer instance - no HTTP, no separate process.
// The only other user-facing surface is `--port <n>` and `--uninstall`. `statusline` and
// `subagent-statusline` remain as separate internal verbs since Claude Code itself invokes them
// as short-lived child processes - see ArgParser's doc comments.
var parsed = ArgParser.Parse(args);

switch (parsed.Verb)
{
    case Verb.StatusLine:
        {
            var store = new FileBackedStatusLineChainStore(FileBackedStatusLineChainStore.DefaultPath());
            return await StatusLineCommand.RunAsync(parsed.Port, store);
        }

    case Verb.SubagentStatusLine:
        return await SubagentStatusLineCommand.RunAsync(parsed.Port);

    case Verb.Doctor:
        return DoctorCommand.Run(Console.Out);

    case Verb.Unknown:
        Console.Error.WriteLine($"Unknown argument: '{parsed.UnknownVerbText}'");
        Console.Error.WriteLine("Usage: glaude [--port <n>] [--uninstall] | glaude doctor");
        return 1;

    case Verb.Start:
    default:
        if (parsed.Uninstall)
        {
            return UninstallCommand.Run(Console.Out);
        }

        return await RunCombinedAsync(parsed.Port, parsed.DumpRawDir);
}

// Installs hooks (best-effort), starts the Kestrel host in-process, and runs the WinForms
// monitor window on a dedicated STA thread until the window is closed (or Ctrl+C is pressed),
// then stops Kestrel cleanly so no listener is left orphaned on the port - see project plan's
// "Process lifecycle for combined start" section for why this exact shape (StartAsync, not the
// blocking RunAsync, so this async Main can join the STA thread and still shut Kestrel down
// afterwards).
static async Task<int> RunCombinedAsync(int port, string? dumpRawDir)
{
    // Best-effort install: InstallCommand.Run already prints its own warning and returns 1 on
    // refusal (e.g. settings.json unwritable) - that refusal must never abort the whole process,
    // it just means hooks weren't (re)registered this run.
    InstallCommand.Run(port, Console.Out);

    var server = new EventServer();
    var app = server.BuildApp(port, dumpRawDir);
    await app.StartAsync();
    Console.WriteLine($"Glaude listening on http://127.0.0.1:{port}");
    if (dumpRawDir is not null)
    {
        Console.WriteLine($"Raw payload capture enabled -> {dumpRawDir}");
    }

    Form? form = null;
    var uiThread = new Thread(() =>
    {
        // Classic (framework-independent of any generated ApplicationConfiguration partial)
        // WinForms startup sequence.
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var monitorForm = new MonitorForm(server);
        form = monitorForm;
        Application.Run(monitorForm);
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();

    ConsoleCancelEventHandler onCancel = (_, e) =>
    {
        // Ctrl+C: close the window (which ends Application.Run on the UI thread) rather than
        // letting the CLR tear the process down mid-request, so Kestrel still gets a chance to
        // stop cleanly below.
        e.Cancel = true;
        if (form is { IsDisposed: false })
        {
            try
            {
                form.BeginInvoke(new Action(() => form.Close()));
            }
            catch
            {
                // Best-effort only - if the handle is gone there's nothing left to close.
            }
        }
    };
    Console.CancelKeyPress += onCancel;

    try
    {
        uiThread.Join();
    }
    finally
    {
        Console.CancelKeyPress -= onCancel;
    }

    await server.StopAsync();
    return 0;
}

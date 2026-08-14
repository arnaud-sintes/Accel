using System.Windows.Forms;
using Glaude.Cli;
using Glaude.Server;

// Throwaway, hidden dev-only verb for P1-T1b's own visual verification of the WPF shell
// scaffolding (App/App.xaml + App/MainWindow.xaml) - NOT part of ArgParser's documented surface
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

// Runs the WPF shell scaffolding standalone, on its own STA thread (mirrors RunCombinedAsync's
// existing WinForms STA thread below - this process's real Main is not STA, since the combined
// app already needed WinForms on a dedicated thread rather than the process's own). Dev-only,
// see the `ui-preview` check above for why this exists.
static void RunUiPreview(bool verify)
{
    var wpfApp = new Glaude.App.App();
    var mainWindow = new Glaude.App.MainWindow();

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
            mainWindow.Dispatcher.BeginInvoke(new Action(() =>
            {
                void Report(string name, System.Windows.FrameworkElement element) =>
                    Console.WriteLine($"{name}: {element.ActualWidth:0.#} x {element.ActualHeight:0.#}");

                Report("PanelA", mainWindow.PanelA);
                Report("PanelB", mainWindow.PanelB);
                Report("PanelC", mainWindow.PanelC);
                Report("PanelD", mainWindow.PanelD);
                Report("PanelE", mainWindow.PanelE);
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

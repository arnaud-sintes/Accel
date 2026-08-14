using System.Windows.Forms;
using Glaude.Cli;
using Glaude.Server;

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

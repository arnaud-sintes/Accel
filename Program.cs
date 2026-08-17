using System.Linq;
using Accel.Cli;
using Accel.Server;
using Microsoft.Extensions.DependencyInjection;

// P2-T2: hidden dev-only verb, same rationale and placement rules as the WPF shell's own
// composition in RunCombinedAsync below -
// ConPtySession's whole point is real OS resource lifecycle (a real pseudoconsole, a real child
// process, real kernel handles), which no unit test with fakes can prove. Checked before
// ArgParser.Parse so it cannot collide with the documented verb surface or its tests. Optional
// iteration count for the handle-leak loop: `pty-smoke-test [iterations]` (default 50).
if (args.Length > 0 && string.Equals(args[0], "pty-smoke-test", StringComparison.Ordinal))
{
    var iterations = args.Length > 1 && int.TryParse(args[1], out var parsedIterations) ? parsedIterations : 50;
    return Accel.Orchestration.ConPtySmokeTest.Run(Console.Out, iterations);
}

// P2-T3: hidden dev-only verb, same rationale and same placement rules as `pty-smoke-test` above -
// PtySession is real process lifecycle plus a real Job Object, which no unit test with fakes can
// prove. It launches cmd.exe (never claude.exe) for the same reason ConPtySmokeTest does. Optional
// cycle count for the leak loop: `pty-session-smoke-test [cycles]` (default 10).
if (args.Length > 0 && string.Equals(args[0], "pty-session-smoke-test", StringComparison.Ordinal))
{
    var cycles = args.Length > 1 && int.TryParse(args[1], out var parsedCycles) ? parsedCycles : 10;
    return Accel.Orchestration.PtySessionSmokeTest.Run(Console.Out, cycles);
}

// P3-T2: hidden dev-only verb, same rationale and same placement rules as the smoke tests above -
// PtyRegistry's load-bearing properties are concurrency and OS-resource ownership under real load (N
// real children torn down at once from N threads, double-closes, self-exit racers, force-kill), which no
// unit test with fakes can establish. Launches cmd.exe, never claude.exe. Optional tab count:
// `pty-registry-stress-test [tabs]` (default 30).
if (args.Length > 0 && string.Equals(args[0], "pty-registry-stress-test", StringComparison.Ordinal))
{
    var tabs = args.Length > 1 && int.TryParse(args[1], out var parsedTabs) ? parsedTabs : 30;
    return Accel.Orchestration.PtyRegistryStressTest.Run(Console.Out, tabs);
}

// P3-T4: hidden dev-only verb, same rationale and placement rules as the smoke tests above. Two halves:
// app-exit graceful shutdown reaching PtyRegistry.CloseAllAsync down all THREE exit paths (explicit
// Dispose/finally, console control events, AppDomain.ProcessExit), and startup orphan reconciliation of
// `accel-sessions.json` against the real OS process table (adoptable vs. stale, risk register item 5).
// Neither is establishable with fakes: one needs a real process actually exiting, the other needs real
// live/dead PIDs. Launches cmd.exe, never claude.exe, and only ever writes a temp-path registry file -
// never the user's real ~/.claude profile. See PtyShutdownReconcileSmokeTest's remarks for why the
// coordinator is proven here in isolation rather than driven through a live RunCombinedAsync run.
if (args.Length > 0 && string.Equals(args[0], "pty-shutdown-orphan-test", StringComparison.Ordinal))
{
    return Accel.Orchestration.PtyShutdownReconcileSmokeTest.Run(Console.Out);
}

// P3-T4's re-invoked child, used by `pty-shutdown-orphan-test`'s third exit-path check only: proving that
// AppDomain.ProcessExit really does reach the session teardown requires a process that really exits, which
// can only be a separate one. Not a user-facing verb in any sense.
if (args.Length > 1 &&
    string.Equals(args[0], Accel.Orchestration.PtyShutdownReconcileSmokeTest.ProcessExitChildVerb, StringComparison.Ordinal))
{
    return Accel.Orchestration.PtyShutdownReconcileSmokeTest.RunProcessExitChild(Console.Out, args[1]);
}

// P2-T5b: hidden dev-only verb, same rationale and placement rules as the smoke tests above -
// proves xterm.js (WebView2, panel D) is actually wired to a live PtySession over a real
// /pty/{tabId} WebSocket route on a real EventServer/Kestrel instance, end to end. No unit test
// can establish this (it needs a real WebView2 + a real loopback socket + a real cmd.exe child).
if (args.Length > 0 && string.Equals(args[0], "terminal-e2e-smoke-test", StringComparison.Ordinal))
{
    return Accel.App.TerminalE2ESmokeTest.Run(Console.Out);
}

// P3-T1: hidden dev-only verb, same rationale and placement rules as the smoke tests above - proves the
// real tab strip (panel C) drives ISessionSelectionService, panel D's reattach, panel A's IsFocused
// highlight and PtyRegistry-routed tab close, against real child processes and real XAML bindings. No
// unit test can establish any of that. Launches cmd.exe, never claude.exe.
if (args.Length > 0 && string.Equals(args[0], "tabs-e2e-smoke-test", StringComparison.Ordinal))
{
    return Accel.App.TabsE2ESmokeTest.Run(Console.Out);
}

// Combined-app entry point (post "one combined app" refactor): running `accel` with no
// arguments installs the hooks (best-effort - a refusal is printed but never aborts startup),
// starts the Kestrel event server in-process, and opens the WPF shell on its own STA thread wired
// DIRECTLY to that same EventServer instance - no HTTP, no separate process. The only other
// user-facing surface is `--port <n>` and `--uninstall`. `statusline` and `subagent-statusline`
// remain as separate internal verbs since Claude Code itself invokes them as short-lived child
// processes - see ArgParser's doc comments.
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
        Console.Error.WriteLine("Usage: accel [--port <n>] [--uninstall] [--verbose] | accel doctor");
        return 1;

    case Verb.Start:
    default:
        if (parsed.Uninstall)
        {
            return UninstallCommand.Run(Console.Out);
        }

        return await RunCombinedAsync(parsed.Port, parsed.DumpRawDir, parsed.Verbose);
}

// Installs hooks (best-effort), starts the Kestrel host in-process, and runs the WPF shell on a
// dedicated STA thread until the window is closed (or Ctrl+C is pressed), then stops Kestrel
// cleanly so no listener is left orphaned on the port - see project plan's "Process lifecycle for
// combined start" section for why this exact shape (StartAsync, not the blocking RunAsync, so
// this async Main can join the STA thread and still shut Kestrel down afterwards).
static async Task<int> RunCombinedAsync(int port, string? dumpRawDir, bool verbose = false)
{
    // Best-effort install: InstallCommand.Run already prints its own warning and returns 1 on
    // refusal (e.g. settings.json unwritable) - that refusal must never abort the whole process,
    // it just means hooks weren't (re)registered this run. A regular (non-verbose) launch only
    // surfaces the lines that matter at startup - a refusal, or a repaired port drift - through
    // StartupOnlyWriter below; --verbose gets InstallCommand's full per-run summary unfiltered.
    InstallCommand.Run(port, verbose ? Console.Out : new StartupOnlyWriter(Console.Out));

    var server = new EventServer();
    var app = server.BuildApp(port, dumpRawDir, verbose);
    await app.StartAsync();
    Console.WriteLine($"Accel listening on http://127.0.0.1:{port}");
    if (dumpRawDir is not null)
    {
        Console.WriteLine($"Raw payload capture enabled -> {dumpRawDir}");
    }

    Accel.App.MainWindow? mainWindow = null;
    var uiThread = new Thread(() =>
    {
        var wpfApp = new Accel.App.App();

        // The real startup composition: an EventServer instance's in-process State/Roots/RootsTree
        // feeds panel A (no HTTP, no /roots/tree polling) and the same Kestrel host already started
        // above serves panel D's /pty/{tabId} route, so the port is already known - no ephemeral-port
        // discovery needed here.
        var dispatcher = new Accel.App.Services.WpfUiThreadDispatcher(
            System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var feed = new Accel.App.Services.TelemetryFeed(
            new Accel.App.Services.EventServerTelemetrySource(server),
            dispatcher,
            new Accel.App.Services.DispatcherDebounceTimer(System.Windows.Threading.Dispatcher.CurrentDispatcher));

        // The selection hub is created here, its single write capability goes to panel C's
        // TabsViewModel and nothing else, and panel A gets the read-only interface (locked-in
        // decision 8 - TabsViewModel is the only writer of FocusedSessionId).
        var selection = new Accel.App.Services.SessionSelectionService();
        var sessionRegistry = new Accel.Orchestration.PtyRegistry();
        var rootsPanel = new Accel.App.ViewModels.RootsPanelViewModel(feed, dispatcher, selection: selection);

        // Panel E: a second reader on the same feed/dispatcher/selection triple as rootsPanel - never
        // a filtered view of rootsPanel's own tree (design doc "claude-agentgraph.md" §7.1/§7.7).
        var agentGraph = new Accel.App.ViewModels.AgentGraphViewModel(feed, dispatcher, selection);

        // Panel B (Phase 5): a read-only file/folder tree rooted at the focused session's cwd, or
        // (via the rootsPanel reference) panel A's own tree selection when no session is focused.
        var filesPanel = new Accel.App.ViewModels.FilesPanelViewModel(feed, dispatcher, selection, rootsPanel);
        // statusPollInterval: re-checks the selected tab's Claude Code status file for a session id
        // that has drifted from the launch-time tabId (e.g. the user typed /clear) - see
        // TabsViewModel.PollFocusedSessionId's remarks for why panel A would otherwise show that
        // session as a permanently unfocused, disconnected row.
        var tabs = new Accel.App.ViewModels.TabsViewModel(
            sessionRegistry,
            selection.AcquireWriter(),
            dispatcher,
            statusPollInterval: TimeSpan.FromSeconds(1));

        mainWindow = new Accel.App.MainWindow(rootsPanel, server.PtySessions, port, tabs, sessionRegistry, selection, agentGraph, filesPanel);
        mainWindow.Loaded += (_, _) => rootsPanel.Start();
        mainWindow.Closed += (_, _) =>
        {
            // Disposed immediately before rootsPanel.Dispose() - before feed.Dispose() - so the panel
            // unhooks from a feed that still exists.
            agentGraph.Dispose();
            filesPanel.Dispose();
            rootsPanel.Dispose();
            feed.Dispose();

            // Closing the registry is what stops every session created through the "Create session"
            // menu item from outliving this window - it closes every registered session through the
            // one blessed path (dispose -> verify -> force-kill the tree).
            sessionRegistry.Dispose();

            // Terminal (WebView2) is disposed here; Kestrel itself is stopped by this method's own
            // `await server.StopAsync()` below, after uiThread.Join() - this window does not own the
            // Kestrel host's lifecycle (it was already running before the window opened).
            mainWindow!.Terminal.Dispose();
        };

        wpfApp.Run(mainWindow);
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();

    ConsoleCancelEventHandler onCancel = (_, e) =>
    {
        // Ctrl+C: close the window (which ends wpfApp.Run on the UI thread) rather than letting the
        // CLR tear the process down mid-request, so Kestrel still gets a chance to stop cleanly below.
        e.Cancel = true;
        var window = mainWindow;
        if (window is not null)
        {
            try
            {
                window.Dispatcher.BeginInvoke(new Action(() => window.Close()));
            }
            catch
            {
                // Best-effort only - if the dispatcher is already shutting down there's nothing left to close.
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

/// <summary>
/// Wraps a regular launch's <see cref="InstallCommand.Run(int, TextWriter)"/> call so only the
/// lines that actually matter at startup reach the console: a refusal (an error - install was
/// aborted, nothing was written) or a repaired port drift (the port Accel is about to listen on
/// silently changed from what was previously installed). Every other line
/// <see cref="InstallCommand"/> writes on a normal, nothing-to-report run (its full per-field
/// "Installed/Already installed/..." summary) is deliberately swallowed here - that detail is
/// only useful with <c>--verbose</c>, which bypasses this writer entirely (see
/// <c>RunCombinedAsync</c>) in favor of the real <see cref="Console.Out"/>.
/// </summary>
sealed class StartupOnlyWriter : TextWriter
{
    private static readonly string[] AlwaysPass = { "Refused", "Port drift repaired" };

    private readonly TextWriter _inner;

    public StartupOnlyWriter(TextWriter inner) => _inner = inner;

    public override System.Text.Encoding Encoding => _inner.Encoding;

    public override void WriteLine(string? value)
    {
        if (value is not null && Array.Exists(AlwaysPass, marker => value.Contains(marker, StringComparison.Ordinal)))
        {
            _inner.WriteLine(value);
        }
    }
}

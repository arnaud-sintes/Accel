namespace Accel.App;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Accel.App.Services;
using Accel.App.ViewModels;
using Accel.Metrics;
using Accel.Orchestration;
using Accel.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// P3-T1: hidden dev-only diagnostic, reachable via the undocumented <c>tabs-e2e-smoke-test</c> verb -
/// same rationale and placement rules as <c>terminal-e2e-smoke-test</c>/<c>pty-registry-stress-test</c>
/// (see Program.cs). It drives the real <see cref="MainWindow"/> (real XAML bindings, real WebView2 panel
/// D, real <see cref="EventServer"/>/Kestrel, real <see cref="PtyRegistry"/>, two real child processes)
/// through the exact call sequence the "Create session" menu item performs, and asserts the things unit
/// tests structurally cannot:
/// <list type="number">
/// <item><b>Two tabs appear</b> in panel C's bound <c>ListBox</c> (not merely in the ViewModel's
/// collection).</item>
/// <item><b>Selecting a tab updates <see cref="ISessionSelectionService.FocusedSessionId"/></b>.</item>
/// <item><b>Panel D reattaches to the SELECTED session</b>, proven with the marker-echo technique
/// <c>terminal-e2e-smoke-test</c>/<c>PtySessionSmokeTest</c> use: each session echoes its own unique
/// marker and xterm.js's accumulator is required to contain the selected session's marker <i>and not the
/// other one's</i> - i.e. it is genuinely reattached, not showing stale output.</item>
/// <item><b>Panel A's <c>IsFocused</c> reflects real selection</b>: a fixture telemetry snapshot carrying
/// both session ids is pushed through <see cref="RootsPanelViewModel"/> and exactly the focused row is
/// required to report <c>IsFocused</c> (plus the matching visual state / automation text).</item>
/// <item><b>Closing a tab goes through <see cref="PtyRegistry.CloseAsync"/></b>: the registry entry is
/// gone and the real child process is observably gone, with no <c>PtySession.Dispose</c> call anywhere in
/// the ViewModel layer (it cannot even be expressed there - see <see cref="IPtySessionHost"/>).</item>
/// <item><b>A session that exits on its own</b> (a real <c>exit</c> typed into the child) leaves its tab
/// in the strip flagged as ended, rather than vanishing silently.</item>
/// </list>
///
/// <para>Launches <c>cmd.exe</c>, never <c>claude.exe</c> - same reasoning as every other smoke test in
/// this codebase: predictable, no auth, no side effects on a real session.</para>
/// </summary>
public static class TabsE2ESmokeTest
{
    private const string MarkerOne = "ACCEL_TAB_ONE_OK";
    private const string MarkerTwo = "ACCEL_TAB_TWO_OK";
    private const string MarkerOneAgain = "ACCEL_TAB_ONE_RESELECTED_OK";

    /// <summary>Runs every check on a dedicated STA thread (WPF/WebView2 requirement); 0 if all passed.</summary>
    public static int Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var passed = false;
        var thread = new Thread(() => RunOnStaThread(output, result => passed = result));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        output.WriteLine();
        output.WriteLine(passed
            ? "tabs-e2e-smoke-test: ALL CHECKS PASSED"
            : "tabs-e2e-smoke-test: AT LEAST ONE CHECK FAILED");
        return passed ? 0 : 1;
    }

    private static void RunOnStaThread(TextWriter output, Action<bool> reportResult)
    {
        var wpfApp = new App();

        var server = new EventServer();
        WebApplication webApp = server.BuildApp(0);
        webApp.StartAsync().GetAwaiter().GetResult();
        int port = new Uri(webApp.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .First()).Port;
        output.WriteLine($"== tabs-e2e-smoke-test: real EventServer bound on loopback port {port} ==");

        // The exact P3-T1 composition MainWindow gets in Program.cs's ui-preview: one selection service,
        // its single writer handed to TabsViewModel only, the read-only interface to panel A.
        var dispatcher = new WpfUiThreadDispatcher(Dispatcher.CurrentDispatcher);
        var selection = new SessionSelectionService();
        var registry = new PtyRegistry();
        var feed = new InertTelemetryFeed();
        var rootsPanel = new RootsPanelViewModel(feed, dispatcher, selection: selection);
        var agentGraph = new AgentGraphViewModel(feed, dispatcher, selection);
        var tabs = new TabsViewModel(registry, selection.AcquireWriter(), dispatcher);

        var window = new MainWindow(rootsPanel, server.PtySessions, port, tabs, registry, selection, agentGraph)
        {
            Width = 1100,
            Height = 700,
            ShowInTaskbar = false,
            Title = "accel-tabs-e2e-smoke-test",
        };

        window.ContentRendered += (_, _) =>
        {
            window.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var ok = false;
                try
                {
                    ok = await RunChecksAsync(output, window, tabs, selection, rootsPanel, agentGraph, registry, server, port);
                }
                catch (Exception ex)
                {
                    output.WriteLine($"  [FAIL] unhandled exception during checks: {ex}");
                }
                finally
                {
                    reportResult(ok);

                    // Teardown happens here, on the async continuation, NOT in the window's Closed
                    // handler: PtyRegistry.Dispose blocks (it waits for every close) and
                    // WebApplication.StopAsync's graceful shutdown waits for the still-open /pty
                    // WebSocket, and doing either synchronously on the dispatcher thread wedges the
                    // process after the last check - observed while building this verb, which is why the
                    // WebView2 control is disposed (closing the socket from the client side) before the
                    // host is stopped.
                    await Task.Run(registry.Dispose);
                    window.Terminal.Dispose();
                    try
                    {
                        await webApp.StopAsync(TimeSpan.FromSeconds(10));
                    }
                    catch
                    {
                        // Best-effort on the way out.
                    }

                    window.Close();
                }
            }), DispatcherPriority.ContextIdle);
        };

        // Everything blocking is torn down before Close() (see the finally above); this only drops the
        // panel-A/panel-E subscriptions.
        window.Closed += (_, _) =>
        {
            agentGraph.Dispose();
            rootsPanel.Dispose();
        };

        wpfApp.Run(window);
    }

    private static async Task<bool> RunChecksAsync(
        TextWriter output,
        MainWindow window,
        TabsViewModel tabs,
        SessionSelectionService selection,
        RootsPanelViewModel rootsPanel,
        AgentGraphViewModel agentGraph,
        PtyRegistry registry,
        EventServer server,
        int port)
    {
        await window.Terminal.Initialization;
        var ok = true;

        // --- Two sessions, created exactly the way MainWindow.CreateSession_Click does it ---
        var idOne = Guid.NewGuid().ToString();
        var idTwo = Guid.NewGuid().ToString();
        var sessionOne = PtySession.Start(CmdSpec(), new PtySessionOptions { Columns = 100, Rows = 30 });
        var sessionTwo = PtySession.Start(CmdSpec(), new PtySessionOptions { Columns = 100, Rows = 30 });

        registry.Register(idOne, sessionOne);
        server.PtySessions.RegisterSession(idOne, sessionOne);
        tabs.AddTab(idOne, "session one");

        registry.Register(idTwo, sessionTwo);
        server.PtySessions.RegisterSession(idTwo, sessionTwo);
        tabs.AddTab(idTwo, "session two");

        output.WriteLine();
        output.WriteLine("== check 1: two tabs, in panel C's BOUND ListBox ==");
        window.TabsList.UpdateLayout();
        var twoTabs = tabs.Tabs.Count == 2 && window.TabsList.Items.Count == 2;
        ok &= twoTabs;
        output.WriteLine($"  [{Pf(twoTabs)}] TabsViewModel.Tabs={tabs.Tabs.Count}, panel C ListBox.Items={window.TabsList.Items.Count}");
        output.WriteLine($"      tabIds: {string.Join(", ", tabs.Tabs.Select(t => t.TabId))}");
        output.WriteLine($"      (each tabId is the session GUID / --session-id value, not a second unrelated id)");

        // --- Selection writes the focus hub, and panel D follows the SELECTED session ---
        output.WriteLine();
        output.WriteLine("== check 2+3: selection -> FocusedSessionId, and panel D reattaches to the selected session ==");

        // Tab two is selected (AddTab selects what it adds), so prove that one first.
        var focusTwo = string.Equals(selection.FocusedSessionId, idTwo, StringComparison.OrdinalIgnoreCase);
        ok &= focusTwo;
        output.WriteLine($"  [{Pf(focusTwo)}] after AddTab(session two): FocusedSessionId={selection.FocusedSessionId} (expected {idTwo})");

        ok &= await AttachedAndEchoesAsync(output, window, tabs, sessionTwo, MarkerTwo, new[] { MarkerOne, MarkerOneAgain });

        tabs.SelectTab(idOne);
        var focusOne = string.Equals(selection.FocusedSessionId, idOne, StringComparison.OrdinalIgnoreCase)
            && string.Equals(tabs.SelectedTabId, idOne, StringComparison.OrdinalIgnoreCase);
        ok &= focusOne;
        output.WriteLine($"  [{Pf(focusOne)}] after SelectTab(session one): FocusedSessionId={selection.FocusedSessionId} (expected {idOne})");

        ok &= await AttachedAndEchoesAsync(output, window, tabs, sessionOne, MarkerOneAgain, new[] { MarkerTwo });

        // --- Panel A's IsFocused, against a fixture snapshot carrying both real session ids ---
        output.WriteLine();
        output.WriteLine("== check 4: panel A's IsFocused reflects the real selection (session one is focused) ==");
        rootsPanel.Rebuild(FixtureTree(idOne, idTwo));
        var nodeOne = FindNode(rootsPanel, idOne);
        var nodeTwo = FindNode(rootsPanel, idTwo);
        var focusHighlight = nodeOne is { IsFocused: true } && nodeTwo is { IsFocused: false };
        ok &= focusHighlight;
        output.WriteLine($"  [{Pf(focusHighlight)}] node(session one).IsFocused={nodeOne?.IsFocused}, node(session two).IsFocused={nodeTwo?.IsFocused}");
        output.WriteLine($"      node(session one) automation text: {nodeOne?.AutomationDescription}");
        output.WriteLine($"      node(session two) automation text: {nodeTwo?.AutomationDescription}");

        // And it follows a live selection change with no rebuild and no telemetry round trip.
        tabs.SelectTab(idTwo);
        await Task.Yield();
        var followsSwitch = nodeOne is { IsFocused: false } && nodeTwo is { IsFocused: true };
        ok &= followsSwitch;
        output.WriteLine($"  [{Pf(followsSwitch)}] after switching to session two (no Rebuild): node one={nodeOne?.IsFocused}, node two={nodeTwo?.IsFocused}");

        // --- Panel E's real AgentGraphControl, against a fixture snapshot carrying a live sub-agent ---
        // Selection is currently idTwo (the SelectTab(idTwo) call just above).
        var agentId = Guid.NewGuid().ToString();
        agentGraph.Rebuild(FixtureTreeWithAgent(idTwo, agentId, idOne));

        output.WriteLine();
        output.WriteLine("== check 7: panel E's graph re-targets when the focused tab changes ==");
        var initialNode = agentGraph.Nodes.Count > 0 ? agentGraph.Nodes[0] : null;
        var initialTarget = string.Equals(initialNode?.Key, idTwo, StringComparison.OrdinalIgnoreCase);
        ok &= initialTarget;
        output.WriteLine($"  [{Pf(initialTarget)}] Nodes[0].Key={initialNode?.Key} (expected the focused session {idTwo})");

        // A focus change with no new telemetry must still re-target the graph (design doc §7.1's
        // third change-signal row) - re-projecting the SAME cached snapshot is the whole cost.
        tabs.SelectTab(idOne);
        await Task.Yield();
        var retargetedNode = agentGraph.Nodes.Count > 0 ? agentGraph.Nodes[0] : null;
        var retargeted = string.Equals(retargetedNode?.Key, idOne, StringComparison.OrdinalIgnoreCase);
        ok &= retargeted;
        output.WriteLine($"  [{Pf(retargeted)}] after switching to session one (no Rebuild): Nodes[0].Key={retargetedNode?.Key} (expected {idOne})");

        // Switch back and re-project the fixture that actually carries the sub-agent, so checks 8/9
        // below have a real parent+child graph to walk in the visual tree.
        tabs.SelectTab(idTwo);
        await Task.Yield();
        window.AgentGraph.UpdateLayout();

        output.WriteLine();
        output.WriteLine("== check 8: panel E renders one card container per node ==");
        var cardCount = CountRealizedCardContainers(window);
        var expectedCards = agentGraph.Nodes.Count;
        var oneCardPerNode = cardCount == expectedCards && expectedCards == 2;
        ok &= oneCardPerNode;
        output.WriteLine($"  [{Pf(oneCardPerNode)}] realized card containers={cardCount} (expected {expectedCards}: 1 parent + 1 agent)");

        output.WriteLine();
        output.WriteLine("== check 9: panel E renders one bezier connector per child ==");
        var connectors = window.AgentGraph.ConnectorLayer.Children.OfType<System.Windows.Shapes.Shape>().ToArray();
        var oneConnectorPerChild = connectors.Length == agentGraph.Nodes.Count - 1;
        var allBezier = connectors.All(p => p.RenderedGeometry is PathGeometry { Figures.Count: 1 } geometry
            && geometry.Figures[0].Segments.Count == 1
            && geometry.Figures[0].Segments[0] is BezierSegment);
        ok &= oneConnectorPerChild && allBezier;
        output.WriteLine($"  [{Pf(oneConnectorPerChild)}] connector count={connectors.Length} (expected {agentGraph.Nodes.Count - 1})");
        output.WriteLine($"  [{Pf(allBezier)}] every connector's Data is a PathGeometry whose single PathFigure has a single BezierSegment");

        // --- Closing a tab routes through PtyRegistry ---
        output.WriteLine();
        output.WriteLine("== check 5: closing a tab goes through PtyRegistry.CloseAsync (never PtySession.Dispose) ==");
        int pidTwo = sessionTwo.ProcessId;
        await tabs.CloseTabAsync(idTwo);
        var closed = !registry.TryGet(idTwo, out _)
            && tabs.Tabs.All(t => !string.Equals(t.TabId, idTwo, StringComparison.OrdinalIgnoreCase))
            && await WaitForAsync(() => Task.FromResult(sessionTwo.ExitTask.IsCompleted), TimeSpan.FromSeconds(10));
        var pidGone = await WaitForAsync(() => Task.FromResult(!ProcessIsAlive(pidTwo)), TimeSpan.FromSeconds(10));
        ok &= closed && pidGone;
        output.WriteLine($"  [{Pf(closed)}] registry entry removed, tab removed, session's ExitTask completed (exit reason: {sessionTwo.ExitReason})");
        output.WriteLine($"  [{Pf(pidGone)}] the real child process (pid {pidTwo}) is gone");
        output.WriteLine($"      selection after the close: FocusedSessionId={selection.FocusedSessionId ?? "<null>"} (moved to the remaining tab)");

        // --- A session that exits on its own keeps its tab, flagged ---
        output.WriteLine();
        output.WriteLine("== check 6: a session that exits by itself leaves its tab visible and flagged as ended ==");
        sessionOne.WriteText("exit\r");
        var endedTab = await WaitForAsync(
            () => Task.FromResult(tabs.Tabs.Any(t => string.Equals(t.TabId, idOne, StringComparison.OrdinalIgnoreCase) && t.HasEnded)),
            TimeSpan.FromSeconds(15));
        var tabOne = tabs.Tabs.FirstOrDefault(t => string.Equals(t.TabId, idOne, StringComparison.OrdinalIgnoreCase));
        ok &= endedTab;
        output.WriteLine($"  [{Pf(endedTab)}] tab still present, HasEnded={tabOne?.HasEnded}, status suffix '{tabOne?.StatusSuffix}', automation text '{tabOne?.AutomationDescription}'");
        output.WriteLine($"      registry entries left: {registry.Count}; FocusedSessionId={selection.FocusedSessionId ?? "<null>"}");

        return ok;
    }

    /// <summary>
    /// The marker-echo proof for one tab: wait for the reattached WebSocket to be OPEN, make the REAL
    /// child echo a marker unique to it, and require xterm.js's accumulator to contain that marker while
    /// containing none of the other sessions' markers. <c>accelAttachPty</c> resets the accumulator per
    /// attach (see terminal.js), so "the other session's marker is absent" is exactly the assertion that
    /// distinguishes a genuine reattach from a stale, still-attached socket.
    /// </summary>
    private static async Task<bool> AttachedAndEchoesAsync(
        TextWriter output,
        MainWindow window,
        TabsViewModel tabs,
        PtySession session,
        string marker,
        IReadOnlyList<string> forbiddenMarkers)
    {
        if (tabs.LastAttach is { } attach)
        {
            await attach;
        }

        var open = await WaitForAsync(async () => await ReadSocketStateAsync(window) == 1, TimeSpan.FromSeconds(5));
        session.WriteText($"echo {marker}\r");
        var sawMarker = await WaitForAsync(
            async () => (await ReadReceivedTextAsync(window)).Contains(marker, StringComparison.Ordinal),
            TimeSpan.FromSeconds(15));

        var received = await ReadReceivedTextAsync(window);
        var noStale = forbiddenMarkers.All(m => !received.Contains(m, StringComparison.Ordinal));

        var ok = open && sawMarker && noStale;
        output.WriteLine($"  [{Pf(open && sawMarker)}] panel D's xterm.js received '{marker}' from the selected session (pid {session.ProcessId})");
        output.WriteLine($"  [{Pf(noStale)}] and contains NONE of the other session's markers ({string.Join(", ", forbiddenMarkers)}) - i.e. really reattached, not stale");
        output.WriteLine($"      accumulator tail: {Tail(received, 220)}");
        return ok;
    }

    private static PtyLaunchSpec CmdSpec() => new()
    {
        ExecutablePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
        WorkingDirectory = Path.GetTempPath(),
    };

    private static bool ProcessIsAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static RootsPanelNodeViewModel? FindNode(RootsPanelViewModel panel, string key) =>
        Flatten(panel.Roots).FirstOrDefault(n => string.Equals(n.Key, key, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<RootsPanelNodeViewModel> Flatten(IEnumerable<RootsPanelNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
            {
                yield return child;
            }
        }
    }

    /// <summary>A minimal <see cref="RootsTreeDto"/> containing exactly the two real session ids, so panel
    /// A has rows whose stable keys match the tabIds. Built by hand rather than scanning the real
    /// <c>~/.claude</c> tree: these sessions are <c>cmd.exe</c>, so no transcript exists for them.</summary>
    private static RootsTreeDto FixtureTree(params string[] sessionIds)
    {
        var now = DateTime.UtcNow;
        var sessions = sessionIds.Select(id => new SessionTreeDto(
            SessionId: id,
            Name: $"smoke {id[..8]}",
            NameSource: "explicit",
            Cwd: Path.GetTempPath(),
            ProjectDir: Path.GetTempPath(),
            IsLive: true,
            Status: "idle",
            ModelId: "claude-sonnet-5",
            ModelDisplayName: "Sonnet",
            EffortLevel: "medium",
            ContextWindowSize: 200_000,
            ContextWindowSizeAssumed: false,
            UsedTokens: 1_000,
            UsedPercentage: 0.5,
            Source: "smoke",
            AsOf: now,
            LastActivityUtc: now,
            Agents: Array.Empty<AgentTreeDto>())).ToArray();

        return new RootsTreeDto(
            new[] { new RootTreeDto(Path.GetTempPath(), true, sessions) },
            Array.Empty<SessionTreeDto>(),
            Array.Empty<AgentTreeDto>(),
            now,
            0);
    }

    /// <summary>Like <see cref="FixtureTree"/>, but <paramref name="sessionWithAgentId"/> carries one
    /// live sub-agent and <paramref name="otherSessionId"/> carries none - checks 7/8/9 need both a
    /// real parent+child graph to project/walk AND a second session present in the same cached
    /// snapshot, so switching focus to it (and back) exercises re-targeting without a second
    /// <see cref="AgentGraphViewModel.Rebuild"/> call.</summary>
    private static RootsTreeDto FixtureTreeWithAgent(string sessionWithAgentId, string agentId, string otherSessionId)
    {
        var now = DateTime.UtcNow;
        var agent = new AgentTreeDto(
            AgentId: agentId,
            Name: "check7 agent",
            AgentType: "general-purpose",
            ModelId: "claude-sonnet-5",
            EffortLevel: "medium",
            InputTokens: 100,
            OutputTokens: 20,
            CacheCreationInputTokens: 0,
            CacheReadInputTokens: 0,
            ContextWindowSize: 200_000,
            ContextWindowSizeAssumed: true,
            UsedPercentage: 0.1,
            Status: "live",
            Source: "smoke",
            AsOf: now);

        SessionTreeDto MakeSession(string id, AgentTreeDto[] agents) => new(
            SessionId: id,
            Name: $"smoke {id[..8]}",
            NameSource: "explicit",
            Cwd: Path.GetTempPath(),
            ProjectDir: Path.GetTempPath(),
            IsLive: true,
            Status: "idle",
            ModelId: "claude-sonnet-5",
            ModelDisplayName: "Sonnet",
            EffortLevel: "medium",
            ContextWindowSize: 200_000,
            ContextWindowSizeAssumed: false,
            UsedTokens: 1_000,
            UsedPercentage: 0.5,
            Source: "smoke",
            AsOf: now,
            LastActivityUtc: now,
            Agents: agents);

        var sessions = new[]
        {
            MakeSession(sessionWithAgentId, new[] { agent }),
            MakeSession(otherSessionId, Array.Empty<AgentTreeDto>()),
        };

        return new RootsTreeDto(
            new[] { new RootTreeDto(Path.GetTempPath(), true, sessions) },
            Array.Empty<SessionTreeDto>(),
            Array.Empty<AgentTreeDto>(),
            now,
            0);
    }

    /// <summary>Walks the real visual tree under <c>window.AgentGraph</c>'s card <c>ItemsControl</c>
    /// and counts realized <see cref="ContentPresenter"/>s - proof the cards actually rendered, not
    /// merely that the ViewModel's collection has the right count.</summary>
    private static int CountRealizedCardContainers(MainWindow window)
    {
        // Find the ItemsControl's own items-host Panel (IsItemsHost=true) and count its DIRECT
        // children only - each one is exactly one realized item container. Walking the whole visual
        // tree and matching "ContentPresenter with the item's DataContext" over-counts: a card's own
        // EffortBarsControl is a UserControl, whose default ControlTemplate is itself a
        // ContentPresenter that inherits the same ambient DataContext.
        Panel? itemsHost = null;
        void Walk(DependencyObject node)
        {
            if (itemsHost is not null)
            {
                return;
            }

            if (node is Panel { IsItemsHost: true } panel)
            {
                itemsHost = panel;
                return;
            }

            int childCount = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < childCount; i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i));
            }
        }

        Walk(window.AgentGraph);
        return itemsHost?.Children.Count ?? 0;
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return await condition();
    }

    private static async Task<int> ReadSocketStateAsync(MainWindow window)
    {
        string json = await window.Terminal.Browser.CoreWebView2.ExecuteScriptAsync("window.accelSocketState()");
        return JsonSerializer.Deserialize<int>(json);
    }

    private static async Task<string> ReadReceivedTextAsync(MainWindow window)
    {
        string json = await window.Terminal.Browser.CoreWebView2.ExecuteScriptAsync("window.accelReceivedText");
        return JsonSerializer.Deserialize<string>(json) ?? string.Empty;
    }

    private static string Tail(string text, int chars)
    {
        var tail = text.Length <= chars ? text : text[^chars..];
        return tail.Replace("", "<ESC>", StringComparison.Ordinal).Replace("\r\n", "\\r\\n", StringComparison.Ordinal);
    }

    private static string Pf(bool ok) => ok ? "PASS" : "FAIL";

    /// <summary>An <see cref="ITelemetryFeed"/> that never publishes anything: this verb drives panel A
    /// with a hand-built fixture snapshot (<see cref="FixtureTree"/>) instead of the real
    /// <c>~/.claude</c> scan, so a live feed would only add noise (and could overwrite the fixture rows
    /// mid-check).</summary>
    private sealed class InertTelemetryFeed : ITelemetryFeed
    {
        public event Action<RootsTreeDto>? SnapshotAvailable;

        public event Action<string>? SnapshotFailed;

        public RootsTreeDto? Latest => null;

        public void Start()
        {
            // Nothing to start - see the class doc.
            _ = SnapshotAvailable;
            _ = SnapshotFailed;
        }

        public void RequestRefresh()
        {
        }

        public void Dispose()
        {
        }
    }
}

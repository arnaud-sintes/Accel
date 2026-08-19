namespace Accel.App;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Accel.App.Controls;
using Accel.Orchestration;
using Accel.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// P2-T5b: hidden dev-only diagnostic, reachable via the undocumented <c>terminal-e2e-smoke-test</c>
/// verb - same rationale and placement rules as <c>pty-smoke-test</c>/<c>pty-session-smoke-test</c>/
/// <c>ui-preview</c> (see Program.cs). What this proves cannot be established by a unit test: a real
/// WebView2-hosted xterm.js page, talking over a real loopback WebSocket to a real
/// <see cref="EventServer"/>/Kestrel instance, driving a real <c>cmd.exe</c> child through a real
/// <see cref="PtySession"/> - end to end, matching how <see cref="MainWindow.CreateSession_Click"/>
/// wires panel D in the real app.
///
/// <para>Launches <c>cmd.exe</c>, never <c>claude.exe</c> - same reasoning as the other smoke tests:
/// predictable, controllable, no network, no auth, no side effects on a real session.</para>
///
/// <para>Checks, in order:
/// <list type="number">
/// <item>(a) marker echo: text written to the real child arrives at xterm.js's accumulator over the
/// real WebSocket text-frame path;</item>
/// <item>(b) resize-reaches-child: a resize driven from the JS side (as the real ResizeObserver path
/// would) is sent as a text control frame and the child observes the new size (<c>mode con</c>,
/// reused from <c>PtySessionSmokeTest</c>, but this time through the full WebSocket path rather than
/// calling <see cref="PtySession.Resize"/> directly);</item>
/// <item>(c) raw Ctrl+C byte: <c>window.accelSimulateInput</c> (the exact function xterm.js's real
/// <c>onData</c> is wired to) is invoked with the literal Ctrl+C character (U+0003), and the server
/// is proven to have received it as a <b>binary</b> frame containing exactly the byte <c>0x03</c>,
/// via a recording <see cref="IPtyEndpoint"/> test double (the same seam <c>PtyRoutesTests</c> uses),
/// not a temporary print statement;</item>
/// <item>integer cell metrics: <c>window.accelCellMetrics()</c> read back to confirm the pinned
/// Cascadia-Mono-or-fallback font configuration actually produces integer CSS cell width/height.</item>
/// </list></para>
/// </summary>
public static class TerminalE2ESmokeTest
{
    private const string Marker = "ACCEL_TERMINAL_E2E_OK";

    /// <summary>The literal Ctrl+C character xterm.js's onData would produce for that key - written
    /// as an escape sequence, not a raw embedded control byte, so the source stays legible.</summary>
    private const string CtrlC = "\u0003";

    /// <summary>The literal ESC character, used only to make raw control bytes visible in this
    /// verb's own console output.</summary>
    private const string Esc = "\u001b";

    /// <summary>Runs every check on a dedicated STA thread (WPF/WebView2 requirement) and returns 0 if
    /// all passed, 1 otherwise.</summary>
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
            ? "terminal-e2e-smoke-test: ALL CHECKS PASSED"
            : "terminal-e2e-smoke-test: AT LEAST ONE CHECK FAILED");
        return passed ? 0 : 1;
    }

    private static void RunOnStaThread(TextWriter output, Action<bool> reportResult)
    {
        var wpfApp = new App();
        var terminal = new TerminalView();
        var window = new Window
        {
            Content = terminal,
            Width = 900,
            Height = 500,
            ShowInTaskbar = false,
            Title = "accel-terminal-e2e-smoke-test",
        };

        window.ContentRendered += (_, _) =>
        {
            window.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var ok = false;
                try
                {
                    ok = await RunChecksAsync(output, terminal);
                }
                catch (Exception ex)
                {
                    output.WriteLine($"  [FAIL] unhandled exception during checks: {ex}");
                }
                finally
                {
                    reportResult(ok);
                    window.Close();
                }
            }), DispatcherPriority.ContextIdle);
        };

        window.Closed += (_, _) => terminal.Dispose();
        wpfApp.Run(window);
    }

    private static async Task<bool> RunChecksAsync(TextWriter output, TerminalView terminal)
    {
        await terminal.Initialization;

        var server = new EventServer();
        var webApp = server.BuildApp(0);
        await webApp.StartAsync();
        try
        {
            var addressesFeature = webApp.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>();
            var address = addressesFeature!.Addresses.First();
            var port = new Uri(address).Port;
            output.WriteLine($"== terminal-e2e-smoke-test: real EventServer bound to {address} ==");

            var ok = true;
            ok &= await CheckMarkerEchoAndResizeAsync(output, terminal, server, port);
            ok &= await CheckRawCtrlCArrivesAsBinaryFrameAsync(output, terminal, server, port);
            ok &= await CheckShiftEnterSendsExactlyOneEscCrAsync(output, terminal, server, port);
            ok &= await CheckIntegerCellMetricsAsync(output, terminal);
            return ok;
        }
        finally
        {
            await webApp.StopAsync();
        }
    }

    private static PtyLaunchSpec CmdSpec() => new()
    {
        ExecutablePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
        WorkingDirectory = Path.GetTempPath(),
    };

    /// <summary>Checks (a) and (b): a real cmd.exe child, driven end to end through the real
    /// WebSocket path in both directions.</summary>
    private static async Task<bool> CheckMarkerEchoAndResizeAsync(
        TextWriter output, TerminalView terminal, EventServer server, int port)
    {
        output.WriteLine();
        output.WriteLine("== check (a)+(b): marker echo + resize-reaches-child, over the real WebSocket path ==");

        using var session = PtySession.Start(CmdSpec(), new PtySessionOptions { Columns = 80, Rows = 25 });
        var tabId = Guid.NewGuid().ToString("N");
        server.PtySessions.RegisterSession(tabId, session);
        output.WriteLine($"  started cmd.exe pid={session.ProcessId}, registered tabId={tabId}");

        await terminal.AttachPtyAsync(tabId, port);
        var socketOpen = await WaitForAsync(async () => await ReadSocketStateAsync(terminal) == 1, TimeSpan.FromSeconds(5));
        output.WriteLine($"  [{(socketOpen ? "PASS" : "FAIL")}] WebSocket reached OPEN state after AttachPtyAsync");

        // (a) marker echo.
        session.WriteText($"echo {Marker}\r");
        var sawMarker = await WaitForAsync(
            async () => (await ReadReceivedTextAsync(terminal)).Contains(Marker, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
        var receivedTail = await ReceivedTailAsync(terminal, 300);
        output.WriteLine($"  [{(sawMarker ? "PASS" : "FAIL")}] (a) marker echo: xterm.js's accumulator saw '{Marker}' after a real child wrote it");
        output.WriteLine($"      last 300 chars received: {receivedTail}");

        // (b) resize - actually resizing the hosting WPF window (the real trigger: WebView2's own
        // size-changed notification fires the ResizeObserver observing #term in terminal.js), NOT
        // calling term.resize() directly - a raw term.resize() with no accompanying container size
        // change was tried first and found to be actively fought by the very same ResizeObserver
        // (it fires on the resulting reflow and calls FitAddon.fit(), which recomputes cols/rows
        // straight back from the - unchanged - container size, undoing the manual call). Resizing
        // the container is what the real app does on every panel-D layout change, so this is also
        // the more faithful check.
        var (initialCols, initialRows) = await ReadTermSizeAsync(terminal);
        output.WriteLine($"  initial fit-computed size: {initialCols}x{initialRows} (from the {900}x{500} smoke-test window)");

        var window = Window.GetWindow(terminal)!;
        window.Width += 400;
        window.Height += 300;

        var (newCols, newRows) = (0, 0);
        var resized = await WaitForAsync(async () =>
        {
            (newCols, newRows) = await ReadTermSizeAsync(terminal);
            return newCols != initialCols || newRows != initialRows;
        }, TimeSpan.FromSeconds(5));

        // A single WPF Width/Height change can drive multiple ResizeObserver callbacks as layout
        // settles in stages (measured here: an intermediate 141x28 before the final 141x47) -
        // each one re-fits AND re-sends {"resize":[...]}, so asserting against the FIRST change
        // observed above is a real race, not a timing nicety. Wait until the read-back size stops
        // changing for a short window before treating it as the target to assert against.
        var (settledCols, settledRows) = (newCols, newRows);
        await WaitForAsync(async () =>
        {
            await Task.Delay(200);
            var (cols, rows) = await ReadTermSizeAsync(terminal);
            var stable = cols == settledCols && rows == settledRows;
            (settledCols, settledRows) = (cols, rows);
            return stable;
        }, TimeSpan.FromSeconds(3));
        (newCols, newRows) = (settledCols, settledRows);

        output.WriteLine($"  [{(resized ? "PASS" : "FAIL")}] resizing the hosting window changed the fit-computed terminal size to a settled {newCols}x{newRows}");

        session.WriteText("mode con\r");
        var sawResize = resized && await WaitForAsync(async () =>
        {
            var text = await ReadReceivedTextAsync(terminal);
            return Regex.IsMatch(text, $@"Columns:\s*{newCols}\b") && Regex.IsMatch(text, $@"Lines:\s*{newRows}\b");
        }, TimeSpan.FromSeconds(10));
        output.WriteLine($"  [{(sawResize ? "PASS" : "FAIL")}] (b) resize sent from the JS side (ResizeObserver -> FitAddon.fit() -> {{\"resize\":[{newCols},{newRows}]}}) reached the child (mode con reports Columns:{newCols} / Lines:{newRows})");
        output.WriteLine($"      tail of decoded output after the resize attempt: {await ReceivedTailAsync(terminal, 500)}");

        server.PtySessions.Unregister(tabId);
        return socketOpen && sawMarker && resized && sawResize;
    }

    private static async Task<(int Cols, int Rows)> ReadTermSizeAsync(TerminalView terminal)
    {
        string json = await terminal.Browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.accelTermSize())");
        string? inner = JsonSerializer.Deserialize<string>(json);
        if (string.IsNullOrEmpty(inner) || inner == "null")
        {
            return (0, 0);
        }

        using var doc = JsonDocument.Parse(inner);
        return (doc.RootElement.GetProperty("cols").GetInt32(), doc.RootElement.GetProperty("rows").GetInt32());
    }

    /// <summary>Check (c): a raw Ctrl+C byte, sent through xterm.js's real onData handler (via
    /// window.accelSimulateInput, which calls that exact function - see terminal.js), arrives at the
    /// server as a WebSocket BINARY frame containing exactly the byte 0x03. Verified against a
    /// recording <see cref="IPtyEndpoint"/> double (the same seam PtyRoutesTests uses) rather than a
    /// temporary diagnostic print.</summary>
    private static async Task<bool> CheckRawCtrlCArrivesAsBinaryFrameAsync(
        TextWriter output, TerminalView terminal, EventServer server, int port)
    {
        output.WriteLine();
        output.WriteLine("== check (c): raw Ctrl+C byte (0x03) via xterm.js onData -> server binary frame ==");

        var recorder = new RecordingPtyEndpoint();
        var tabId = Guid.NewGuid().ToString("N");
        server.PtySessions.Register(tabId, recorder);

        await terminal.AttachPtyAsync(tabId, port);
        var socketOpen = await WaitForAsync(async () => await ReadSocketStateAsync(terminal) == 1, TimeSpan.FromSeconds(5));
        output.WriteLine($"  [{(socketOpen ? "PASS" : "FAIL")}] WebSocket reattached to the recording endpoint and reached OPEN");

        // The literal character U+0003 (Ctrl+C), passed through JSON encoding so it survives being
        // embedded in the generated script text unchanged.
        string script = $"window.accelSimulateInput({JsonSerializer.Serialize(CtrlC)});";
        await terminal.Browser.CoreWebView2.ExecuteScriptAsync(script);

        byte[]? written = null;
        try
        {
            written = await recorder.NextWriteAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // written stays null - reported as a failure below.
        }

        var ok = written is { Length: 1 } && written[0] == 0x03;
        output.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] server received exactly one byte, 0x03, as a BINARY frame (received: {FormatBytes(written)})");

        server.PtySessions.Unregister(tabId);
        return socketOpen && ok;
    }

    /// <summary>Check (d): a real synthetic Shift+Enter <c>KeyboardEvent</c>, dispatched on xterm.js's
    /// own hidden textarea exactly as a real physical keypress would land, must produce exactly one
    /// ESC-CR (0x1B 0x0D) write and nothing else - never a second write from the browser's native
    /// default action (newline insertion / xterm's own untouched keypress handling) sneaking through
    /// alongside terminal.js's explicit <c>handleTerminalData("\x1b\r")</c> call. Verified against a
    /// recording <see cref="IPtyEndpoint"/> double, same seam as check (c), because this is exactly
    /// the kind of "did it fire twice" bug a fake/unit test cannot see - it needs the real DOM event
    /// dispatch, xterm's real <c>attachCustomKeyEventHandler</c> wiring, and a real WebSocket frame
    /// count.</summary>
    private static async Task<bool> CheckShiftEnterSendsExactlyOneEscCrAsync(
        TextWriter output, TerminalView terminal, EventServer server, int port)
    {
        output.WriteLine();
        output.WriteLine("== check (d): synthetic Shift+Enter KeyboardEvent -> exactly one ESC-CR write, no duplicate ==");

        var recorder = new RecordingPtyEndpoint();
        var tabId = Guid.NewGuid().ToString("N");
        server.PtySessions.Register(tabId, recorder);

        await terminal.AttachPtyAsync(tabId, port);
        var socketOpen = await WaitForAsync(async () => await ReadSocketStateAsync(terminal) == 1, TimeSpan.FromSeconds(5));
        output.WriteLine($"  [{(socketOpen ? "PASS" : "FAIL")}] WebSocket reattached to the recording endpoint and reached OPEN");

        // Dispatched on the real hidden textarea xterm.js's own keydown/keypress listeners are bound
        // to (see xterm.css's .xterm-helper-textarea) - not window.accelSimulateInput, which bypasses
        // attachCustomKeyEventHandler entirely and so cannot see this bug.
        const string dispatchScript = """
            (function () {
              var ta = document.querySelector('.xterm-helper-textarea');
              var evt = new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', shiftKey: true, bubbles: true, cancelable: true });
              ta.dispatchEvent(evt);
            })();
            """;
        await terminal.Browser.CoreWebView2.ExecuteScriptAsync(dispatchScript);

        byte[]? firstWrite = null;
        try
        {
            firstWrite = await recorder.NextWriteAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // firstWrite stays null - reported as a failure below.
        }

        var expected = new byte[] { 0x1B, 0x0D };
        var firstOk = firstWrite is not null && firstWrite.SequenceEqual(expected);
        output.WriteLine($"  [{(firstOk ? "PASS" : "FAIL")}] server received exactly ESC-CR (0x1B 0x0D) as a single BINARY frame (received: {FormatBytes(firstWrite)})");

        // No second write should ever follow - a fixed window, not a race: any stray native-default
        // write (a bare "\r", a literal newline, or a duplicate ESC-CR) would arrive within
        // milliseconds of the first, well inside this margin.
        byte[]? secondWrite = null;
        try
        {
            secondWrite = await recorder.NextWriteAsync(TimeSpan.FromMilliseconds(500));
        }
        catch (OperationCanceledException)
        {
            // Expected: no second write.
        }

        var noDuplicate = secondWrite is null;
        output.WriteLine($"  [{(noDuplicate ? "PASS" : "FAIL")}] no second write followed (received: {FormatBytes(secondWrite)})");

        server.PtySessions.Unregister(tabId);
        return socketOpen && firstOk && noDuplicate;
    }

    private static async Task<bool> CheckIntegerCellMetricsAsync(TextWriter output, TerminalView terminal)
    {
        output.WriteLine();
        output.WriteLine("== check: pinned font produces INTEGER cell metrics (risk register item 4) ==");

        string outer = await terminal.Browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.accelCellMetrics())");
        string? inner = JsonSerializer.Deserialize<string>(outer);
        output.WriteLine($"  window.accelCellMetrics() raw: {inner}");

        if (string.IsNullOrEmpty(inner) || inner == "null")
        {
            output.WriteLine("  [FAIL] accelCellMetrics() returned null - xterm's internal render surface was unavailable");
            return false;
        }

        using var doc = JsonDocument.Parse(inner);
        double cssWidth = doc.RootElement.GetProperty("cssWidth").GetDouble();
        double cssHeight = doc.RootElement.GetProperty("cssHeight").GetDouble();
        double deviceWidth = doc.RootElement.GetProperty("deviceWidth").GetDouble();
        double deviceHeight = doc.RootElement.GetProperty("deviceHeight").GetDouble();

        static bool IsInteger(double value) => Math.Abs(value - Math.Round(value)) < 0.01;

        var integerCss = IsInteger(cssWidth) && IsInteger(cssHeight);
        output.WriteLine($"  css cell size: {cssWidth} x {cssHeight} px (device cell size: {deviceWidth} x {deviceHeight} px)");
        output.WriteLine($"  [{(integerCss ? "PASS" : "FAIL")}] css cell width/height are integer pixel sizes (fontSize=14, lineHeight=1, letterSpacing auto-corrected by snapCellWidthToIntegerPixels)");
        return integerCss;
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

    private static async Task<int> ReadSocketStateAsync(TerminalView terminal)
    {
        string json = await terminal.Browser.CoreWebView2.ExecuteScriptAsync("window.accelSocketState()");
        return JsonSerializer.Deserialize<int>(json);
    }

    private static async Task<string> ReadReceivedTextAsync(TerminalView terminal)
    {
        string json = await terminal.Browser.CoreWebView2.ExecuteScriptAsync("window.accelReceivedText");
        return JsonSerializer.Deserialize<string>(json) ?? string.Empty;
    }

    private static async Task<string> ReceivedTailAsync(TerminalView terminal, int chars)
    {
        var text = await ReadReceivedTextAsync(terminal);
        var tail = text.Length <= chars ? text : text[^chars..];
        return tail.Replace(Esc, "<ESC>", StringComparison.Ordinal).Replace("\r\n", "\\r\\n", StringComparison.Ordinal);
    }

    private static string FormatBytes(byte[]? bytes) =>
        bytes is null ? "<none>" : "[" + string.Join(",", bytes.Select(b => "0x" + b.ToString("X2"))) + "]";

    /// <summary>Minimal <see cref="IPtyEndpoint"/> test double that records every write it receives -
    /// same shape as <c>PtyRoutesTests.FakePtyEndpoint</c>, used here so check (c) can assert the exact
    /// bytes the server received instead of relying on a real ConPTY child's observable behaviour
    /// (which <c>PtySessionSmokeTest</c> already found unreliable for Ctrl+C specifically).</summary>
    private sealed class RecordingPtyEndpoint : IPtyEndpoint
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        private readonly Channel<byte[]> _writes = Channel.CreateUnbounded<byte[]>();

        public ChannelReader<string> Output => _output.Reader;

        public void Write(ReadOnlySpan<byte> bytes) => _writes.Writer.TryWrite(bytes.ToArray());

        public void Resize(int columns, int rows)
        {
            // Not exercised by check (c) - resize is covered by CheckMarkerEchoAndResizeAsync
            // against the real session.
        }

        public async Task<byte[]> NextWriteAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            return await _writes.Reader.ReadAsync(cts.Token);
        }
    }
}

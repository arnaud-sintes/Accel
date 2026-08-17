namespace Accel.App.Controls;

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

/// <summary>
/// P2-T5: WebView2-hosted xterm.js terminal shell. P2-T5b (this class's <see cref="AttachPtyAsync"/>)
/// wires the rendered xterm.js instance up to a live <c>PtySession</c> over the <c>/pty/{tabId}</c>
/// WebSocket route — see <c>wwwroot/xterm/terminal.js</c> for the actual attach/onData/resize glue
/// and <c>Server/PtyRoutes.cs</c> for the binary-vs-text framing convention it matches.
///
/// <para><b>Vendoring / serving choice:</b> xterm.js + the FitAddon are vendored on disk under
/// <c>App/Controls/wwwroot/xterm/</c> (Content-copied to the build/publish output — see
/// <c>Accel.csproj</c>'s explicit <c>ItemGroup</c>; exact versions/licenses recorded in that
/// folder's <c>THIRD_PARTY_NOTICES.txt</c>) rather than compiled in as true assembly
/// <c>EmbeddedResource</c> bytes extracted to a temp file at runtime. The on-disk choice was
/// picked as simpler and just as robust: no temp-file extraction/cleanup lifecycle to get wrong,
/// and these files already sit next to <c>Accel.exe</c> in both build and publish output
/// (<c>PublishSingleFile</c> only folds managed assemblies into the single-file payload — this
/// project does not set <c>IncludeAllContentForSelfExtract</c>, so <c>Content</c> items, these
/// included, remain ordinary loose files beside the exe). They're served to the WebView2
/// instance via <see cref="CoreWebView2.SetVirtualHostNameToFolderMapping"/>, mapping the virtual
/// host <see cref="VirtualHostName"/> onto that on-disk folder, then navigating to
/// <c>https://accel-terminal/index.html</c> — no CDN, no npm-at-build dependency, works fully
/// offline.</para>
///
/// <para><b>User-data folder:</b> WebView2's own default user-data-folder resolution is keyed off
/// the hosting exe's path/name, which collides across unrelated apps embedding WebView2 from the
/// same directory (and across separate dev/publish copies of this same exe). Accel's other
/// per-user state already lives under <c>%USERPROFILE%\.claude\</c> (see
/// <see cref="Accel.Cli.AccelPaths.DefaultSettingsPath"/>, <c>accel-state.json</c>,
/// <c>accel-folders.json</c>) — <see cref="WebView2UserDataFolder"/> reuses that convention with
/// its own subfolder, <c>accel-webview2\</c>, instead of a third location.</para>
/// </summary>
public partial class TerminalView : UserControl, IDisposable
{
    /// <summary>The virtual host name the vendored xterm.js assets are served under.</summary>
    public const string VirtualHostName = "accel-terminal";

    private readonly TaskCompletionSource initializationTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TerminalView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Completes once <c>CoreWebView2</c> is initialized and the vendored xterm.js page has been
    /// navigated to (or faults if initialization failed) — awaited by <see cref="AttachPtyAsync"/>
    /// before it pushes the tabId/port into the page, and by `terminal-e2e-smoke-test` (see
    /// <c>Program.cs</c>) so it can then query <c>document.title</c> via
    /// <c>CoreWebView2.ExecuteScriptAsync</c> for proof the page actually loaded and xterm.js
    /// initialized without a JS error, rather than assuming success once WPF measured a non-zero
    /// control size.
    /// </summary>
    public Task Initialization => initializationTcs.Task;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await InitializeAsync();
            initializationTcs.TrySetResult();
        }
        catch (Exception ex)
        {
            initializationTcs.TrySetException(ex);
        }
    }

    private async Task InitializeAsync()
    {
        var userDataFolder = WebView2UserDataFolder();
        Directory.CreateDirectory(userDataFolder);

        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        await Browser.EnsureCoreWebView2Async(environment);

        var assetsRoot = XtermAssetsFolder();
        Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
            VirtualHostName,
            assetsRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);

        // Navigate() only starts the navigation - it returns long before index.html's own
        // <script> has run, let alone set document.title. Wait for NavigationCompleted so
        // Initialization (and thus any caller's verification query of document.title) only
        // completes once the page - and therefore xterm.js/FitAddon - has actually finished
        // loading, instead of racing it.
        var navigationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            Browser.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            navigationCompletion.TrySetResult();
        }

        Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        Browser.CoreWebView2.Navigate($"https://{VirtualHostName}/index.html");
        await navigationCompletion.Task;
    }

    /// <summary>
    /// P2-T5b: attaches the already-rendered xterm.js instance to a live pty over
    /// <c>/pty/{tabId}</c>, by calling <c>window.accelAttachPty(tabId, port)</c> in
    /// <c>terminal.js</c> once <see cref="Initialization"/> has completed (i.e. xterm.js already
    /// exists in the page).
    ///
    /// <para><b>tabId/port-passing mechanism (this task's stopgap).</b> Rather than encoding the
    /// tabId/port in the navigation URL (which would force a fresh <c>Navigate</c>/page reload for
    /// every new session and lose the whole point of having a single hosted xterm.js instance),
    /// the values are pushed into the already-loaded page via
    /// <see cref="CoreWebView2.ExecuteScriptAsync(string)"/> — the same mechanism used to read
    /// <c>document.title</c> back out for verification. The caller
    /// (currently <c>MainWindow.CreateSession_Click</c>, itself explicitly marked as a stopgap
    /// pending Phase 3's real tab/registry) is responsible for actually registering the session
    /// under <paramref name="tabId"/> in the server's <c>PtyRouteRegistry</c> before calling this,
    /// and for the WebSocket port matching whatever <c>EventServer</c>/Kestrel instance owns that
    /// registry.</para>
    ///
    /// <para><b>Why <c>ws://</c> and a real loopback address, not <c>wss://accel-terminal</c>.</b>
    /// <see cref="CoreWebView2.SetVirtualHostNameToFolderMapping"/> only intercepts document/
    /// subresource GET requests for the folder it maps — it does not proxy a WebSocket upgrade to
    /// an arbitrary local TCP listener, and the virtual host name is not a real, resolvable
    /// address. See <c>terminal.js</c>'s <c>accelAttachPty</c> for the client-side connection
    /// logic and rationale in full, and this task's report for what was actually observed running
    /// it end to end (there is no real TLS listener on the loopback PTY port, so <c>ws://</c>, not
    /// <c>wss://</c>, is correct — not merely "likely", per the task's own framing).</para>
    /// </summary>
    /// <param name="tabId">The tabId the session is registered under in the server's
    /// <c>PtyRouteRegistry</c>. Passed through JSON string encoding so it is safe even if it ever
    /// contained characters that would otherwise break out of the generated script (it is expected
    /// to always be a GUID in practice).</param>
    /// <param name="webSocketPort">The port the owning <c>EventServer</c>'s Kestrel instance is
    /// bound to (loopback only — see <c>EventServer</c>'s class doc).</param>
    public async Task AttachPtyAsync(string tabId, int webSocketPort)
    {
        ArgumentException.ThrowIfNullOrEmpty(tabId);
        await Initialization;
        await Browser.CoreWebView2.ExecuteScriptAsync(BuildAttachScript(tabId, webSocketPort));
    }

    /// <summary>
    /// Builds the <c>window.accelAttachPty(tabId, port)</c> call, JSON-encoding
    /// <paramref name="tabId"/> so it cannot break out of the generated script regardless of its
    /// contents. Pure and side-effect-free — split out from <see cref="AttachPtyAsync"/> purely so
    /// it is unit-testable without a real WebView2 (the JS side itself is not unit-testable in
    /// this stack, per the task's own note — this is the closest C#-side seam to it).
    /// </summary>
    internal static string BuildAttachScript(string tabId, int webSocketPort)
    {
        ArgumentException.ThrowIfNullOrEmpty(tabId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(webSocketPort, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(webSocketPort, 65535);

        return $"window.accelAttachPty({JsonSerializer.Serialize(tabId)}, {webSocketPort.ToString(System.Globalization.CultureInfo.InvariantCulture)});";
    }

    /// <summary>
    /// <c>%USERPROFILE%\.claude\accel-webview2\</c> — the WebView2 user-data folder. See the
    /// class doc for why this location (never WebView2's own default resolution).
    /// </summary>
    public static string WebView2UserDataFolder() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "accel-webview2");

    /// <summary>
    /// The vendored xterm.js/FitAddon/index.html folder, Content-copied beside Accel.exe at
    /// <c>wwwroot\xterm\</c> (see <c>Accel.csproj</c>'s explicit <c>ItemGroup</c>), resolved
    /// relative to <see cref="AppContext.BaseDirectory"/> so it resolves identically for a plain
    /// build output and a <c>PublishSingleFile</c> publish — see the class doc's vendoring note.
    /// </summary>
    public static string XtermAssetsFolder() =>
        Path.Combine(AppContext.BaseDirectory, "wwwroot", "xterm");

    /// <summary>
    /// Disposes the underlying <c>WebView2</c> control (which tears down its
    /// <c>CoreWebView2Controller</c> and, in turn, closes its out-of-process browser/renderer/GPU
    /// processes). Found necessary empirically: a WPF <c>Window.Close()</c> alone is not enough
    /// - a dev-time repro process exited cleanly (exit code 0) while its
    /// <c>msedgewebview2.exe</c> child processes were still observed alive several seconds later
    /// (via <c>Get-Process msedgewebview2</c> filtered by <c>StartTime</c>) until this Dispose was
    /// added and wired into the window's Closed handler (see <c>Program.cs</c>'s
    /// <c>RunCombinedAsync</c>, the real startup path).
    /// </summary>
    public void Dispose()
    {
        Browser.Dispose();
    }
}

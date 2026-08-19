namespace Accel.App.Controls;

using System;
using System.Globalization;
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
/// <para><b>Vendoring / serving choice:</b> xterm.js + its addons (Fit, WebGL) are vendored on disk under
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

        // WebView2's own default fill (opaque white) is what the compositor paints into any area
        // of the control its surface hasn't caught up to yet - normally invisible, but during a
        // live window/panel resize (dragging the window border, or panel D's own splitters) the
        // WPF control's bounds update a frame or more ahead of the browser process's own resized
        // paint, so that gap - briefly - shows this default instead of whatever was already
        // rendered. Against xterm's near-black theme (#0a0a0a, matching index.html's body and
        // this control's own Grid background above) that reads as a jarring white flash exactly
        // while resizing, i.e. "the terminal doesn't resize well" - not a rendering bug in the
        // page itself, just the wrong fallback color for the gap. Must be set before
        // EnsureCoreWebView2Async - DefaultBackgroundColor only takes effect at controller
        // creation, not on an already-initialized one.
        Browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xFF, 0x0A, 0x0A, 0x0A);

        // Disables Chromium's overlay scrollbars (the auto-hiding, native-drawn Fluent style
        // Windows 11 ships by default) for this WebView2 instance only. Confirmed empirically
        // (screenshots of a live terminal with far more scrollback than fits the viewport): with
        // overlay scrollbars on, index.html's `.xterm-viewport::-webkit-scrollbar*` rules are
        // silently ignored by Chromium - not just unstyled, genuinely never painted, even while
        // actively scrolling - because an overlay-mode scrollbar is a native compositor overlay,
        // not part of the page's own paint/CSS box model the way a classic scrollbar is. Classic
        // scrollbars (this flag's effect) are the one scrollbar mode `::-webkit-scrollbar-*`
        // pseudo-elements actually apply to, which is what index.html was already written
        // assuming. See MicrosoftEdge/WebView2Feedback#2796 for the flag names.
        var environmentOptions = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments =
                "--disable-features=OverlayScrollbar,OverlayScrollbarWinStyle,OverlayScrollbarWinStyleAnimation",
        };
        var environment = await CoreWebView2Environment.CreateAsync(
            userDataFolder: userDataFolder,
            options: environmentOptions);
        await Browser.EnsureCoreWebView2Async(environment);

        // Paste (Ctrl+V, terminal.js's handlePaste) reads the clipboard via
        // navigator.clipboard.readText(), which Chromium/WebView2 gates behind an explicit
        // permission grant (CoreWebView2PermissionKind.ClipboardRead) - unlike writeText() (used
        // for copy), which is allowed for a user-gesture-triggered call with no prompt. Without
        // this handler, WebView2's default is to silently deny the request, so readText() rejects
        // and paste does nothing with no visible error. Auto-allow unconditionally: this is
        // Accel's own vendored page (accel-terminal), never third-party/remote content, so there
        // is no cross-origin clipboard-snooping risk to gate behind a user prompt.
        Browser.CoreWebView2.PermissionRequested += (_, e) =>
        {
            if (e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
            {
                e.State = CoreWebView2PermissionState.Allow;
            }
        };

        var assetsRoot = XtermAssetsFolder();
        Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
            VirtualHostName,
            assetsRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);

        // Reported bug: an edit to terminal.js (or index.html/xterm.css) did not take effect after a
        // rebuild and app relaunch. Root cause: WebView2UserDataFolder() is a PERSISTENT profile
        // (deliberately, so the clipboard permission grant above survives restarts) - not a fresh
        // temp folder per run - and SetVirtualHostNameToFolderMapping serves these local files with a
        // real Last-Modified header, which is enough for Chromium's HTTP disk cache to keep serving a
        // stale copy of index.html/terminal.js/xterm.css across app restarts, indefinitely, even after
        // the on-disk file changes. A manual cache-busting query string on one script tag (index.html's
        // own history) only ever fixes that one file for one edit; wiping the disk cache here, once
        // per launch, fixes every file in this mapping for every future edit instead. Scoped to only
        // this profile/user-data-folder's cache (Accel's own vendored page), never a stray path.
        await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);

        // terminal.js reads this to configure xterm's `windowsPty` option instead of the legacy
        // `windowsMode` - see the comment on that option for why the distinction matters and what it
        // fixes. AddScriptToExecuteOnDocumentCreatedAsync (not an ExecuteScriptAsync after
        // navigation) because terminal.js builds the Terminal during its own initial load: anything
        // pushed in afterwards would arrive too late to be part of the constructor's options.
        await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
            $"window.accelConPtyBuildNumber = {ConPtyBuildNumber().ToString(CultureInfo.InvariantCulture)};");

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

        // terminal.js's own accelAttachPty already calls term.focus(), but that only moves focus
        // within the page - inert unless the WebView2 control itself holds actual Win32/WPF
        // keyboard focus. Without this, a freshly created session left focus wherever it last was
        // in the host window (e.g. panel A's tree), so the user's first keystrokes went nowhere
        // until they clicked into panel D themselves.
        Browser.Focus();
    }

    /// <summary>
    /// Closes any live pty socket and wipes the xterm.js screen buffer, via
    /// <c>window.accelDetachPty()</c> - used when panel C has no tab left to show (the last open
    /// session's tab just closed), so panel D goes back to a blank black surface instead of
    /// freezing on the closed session's last rendered frame.
    /// </summary>
    public async Task DetachPtyAsync()
    {
        await Initialization;
        await Browser.CoreWebView2.ExecuteScriptAsync("window.accelDetachPty();");
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
    /// This machine's Windows build number, which is also the ConPTY version xterm.js's
    /// <c>windowsPty</c> option wants (there is no separate ConPTY version — the pseudoconsole ships
    /// with conhost, so the OS build <i>is</i> its version, and 21376 is the build xterm compares
    /// against for whether ConPTY can report line wrapping itself).
    /// </summary>
    private static int ConPtyBuildNumber() => Environment.OSVersion.Version.Build;

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

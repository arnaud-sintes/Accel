namespace Glaude.App.Controls;

using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

/// <summary>
/// P2-T5: WebView2-hosted xterm.js terminal SHELL — renders an inert (no PTY) xterm.js instance
/// with nothing behind it. Wiring this up to a live `claude` session over the future
/// <c>/pty/{tabId}</c> WebSocket route (attach, resize, Ctrl+C, paste) is the separate task
/// P2-T5b, deliberately NOT done here — see the project plan's split of P2-T5/P2-T5b.
///
/// <para><b>Vendoring / serving choice:</b> xterm.js + the FitAddon are vendored on disk under
/// <c>App/Controls/wwwroot/xterm/</c> (Content-copied to the build/publish output — see
/// <c>Glaude.csproj</c>'s explicit <c>ItemGroup</c>; exact versions/licenses recorded in that
/// folder's <c>THIRD_PARTY_NOTICES.txt</c>) rather than compiled in as true assembly
/// <c>EmbeddedResource</c> bytes extracted to a temp file at runtime. The on-disk choice was
/// picked as simpler and just as robust: no temp-file extraction/cleanup lifecycle to get wrong,
/// and these files already sit next to <c>Glaude.exe</c> in both build and publish output
/// (<c>PublishSingleFile</c> only folds managed assemblies into the single-file payload — this
/// project does not set <c>IncludeAllContentForSelfExtract</c>, so <c>Content</c> items, these
/// included, remain ordinary loose files beside the exe). They're served to the WebView2
/// instance via <see cref="CoreWebView2.SetVirtualHostNameToFolderMapping"/>, mapping the virtual
/// host <see cref="VirtualHostName"/> onto that on-disk folder, then navigating to
/// <c>https://glaude-terminal/index.html</c> — no CDN, no npm-at-build dependency, works fully
/// offline.</para>
///
/// <para><b>User-data folder:</b> WebView2's own default user-data-folder resolution is keyed off
/// the hosting exe's path/name, which collides across unrelated apps embedding WebView2 from the
/// same directory (and across separate dev/publish copies of this same exe). Glaude's other
/// per-user state already lives under <c>%USERPROFILE%\.claude\</c> (see
/// <see cref="Glaude.Cli.GlaudePaths.DefaultSettingsPath"/>, <c>glaude-state.json</c>,
/// <c>glaude-folders.json</c>) — <see cref="WebView2UserDataFolder"/> reuses that convention with
/// its own subfolder, <c>glaude-webview2\</c>, instead of a third location.</para>
/// </summary>
public partial class TerminalView : UserControl, IDisposable
{
    /// <summary>The virtual host name the vendored xterm.js assets are served under.</summary>
    public const string VirtualHostName = "glaude-terminal";

    private readonly TaskCompletionSource initializationTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TerminalView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Completes once <c>CoreWebView2</c> is initialized and the vendored xterm.js page has been
    /// navigated to (or faults if initialization failed) — awaited by `ui-preview --verify` (see
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
        // Initialization (and thus `ui-preview`'s verification query of document.title) only
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
    /// <c>%USERPROFILE%\.claude\glaude-webview2\</c> — the WebView2 user-data folder. See the
    /// class doc for why this location (never WebView2's own default resolution).
    /// </summary>
    public static string WebView2UserDataFolder() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "glaude-webview2");

    /// <summary>
    /// The vendored xterm.js/FitAddon/index.html folder, Content-copied beside Glaude.exe at
    /// <c>wwwroot\xterm\</c> (see <c>Glaude.csproj</c>'s explicit <c>ItemGroup</c>), resolved
    /// relative to <see cref="AppContext.BaseDirectory"/> so it resolves identically for a plain
    /// build output and a <c>PublishSingleFile</c> publish — see the class doc's vendoring note.
    /// </summary>
    public static string XtermAssetsFolder() =>
        Path.Combine(AppContext.BaseDirectory, "wwwroot", "xterm");

    /// <summary>
    /// Disposes the underlying <c>WebView2</c> control (which tears down its
    /// <c>CoreWebView2Controller</c> and, in turn, closes its out-of-process browser/renderer/GPU
    /// processes). Found necessary empirically: a WPF <c>Window.Close()</c> alone is not enough
    /// - `ui-preview --verify`'s process exited cleanly (exit code 0) while its
    /// <c>msedgewebview2.exe</c> child processes were still observed alive several seconds later
    /// (via <c>Get-Process msedgewebview2</c> filtered by <c>StartTime</c>) until this Dispose was
    /// added and wired into the window's Closed handler (see Program.cs's `ui-preview` verb).
    /// </summary>
    public void Dispose()
    {
        Browser.Dispose();
    }
}

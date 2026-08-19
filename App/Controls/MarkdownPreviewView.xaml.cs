namespace Accel.App.Controls;

using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

/// <summary>
/// WebView2-hosted, read-only rendered-HTML preview for the FILE/GIT panel markdown toggle (see
/// <c>TabViewModel.IsMarkdown</c>/<c>IsPreviewMode</c> and
/// <c>MainWindow.ShowMarkdownPreviewAsync</c>, its only caller). Deliberately much smaller than
/// <see cref="TerminalView"/>: no PTY to attach, no JS to vendor, no
/// <c>SetVirtualHostNameToFolderMapping</c> - <see cref="RenderAsync"/> wraps the already-rendered
/// markdown body HTML (produced by Markdig) in a small inline-styled shell and pushes it straight
/// in via <c>NavigateToString</c>, entirely in-memory.
///
/// <para><b>Own user-data folder.</b> Kept separate from <see cref="TerminalView.WebView2UserDataFolder"/>
/// so this control's <c>CoreWebView2Environment</c> never contends with the terminal's - both are
/// long-lived, panel-D-singleton controls (this one behind <c>MarkdownPreviewHost</c>, reused
/// across every markdown tab exactly like <c>TerminalView</c> is reused across every session tab),
/// so each gets its own profile rather than racing to create/open the same one.</para>
/// </summary>
public partial class MarkdownPreviewView : UserControl, IDisposable
{
    private Task? _initialization;

    public MarkdownPreviewView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Completes once <c>CoreWebView2</c> is initialized, starting that initialization on first
    /// access - awaited by <see cref="RenderAsync"/> before it navigates. The null-coalescing
    /// assignment below makes this idempotent: only the first access ever calls
    /// <see cref="InitializeAsync"/>, same as <see cref="TerminalView.Initialization"/>'s
    /// once-only <c>Loaded -= OnLoaded</c> pattern.
    ///
    /// <para><b>Caller must make this control visible/laid-out before awaiting this for the first
    /// time.</b> Unlike <see cref="TerminalView"/> (whose host starts <c>Visibility="Visible"</c> -
    /// it IS the initially-shown pane), <see cref="MarkdownPreviewHost"/> starts
    /// <c>Visibility="Collapsed"</c>. Confirmed empirically (via <c>markdown-preview-smoke-test</c>
    /// while building this feature): <c>WebView2.EnsureCoreWebView2Async</c> hangs forever - never
    /// throwing, never completing - if called while this control is still part of a
    /// <c>Collapsed</c>/zero-size subtree; it needs an actual laid-out HWND to parent the browser
    /// process's controller against. <c>MainWindow.ShowMarkdownPreviewAsync</c> (the only caller)
    /// calls <c>ShowMarkdownPreviewPane()</c> <b>before</b> this - the pane was going to be shown
    /// regardless, so there is no extra flicker, just the same "blank pane until content loads" gap
    /// <see cref="TerminalView"/>'s own <c>DefaultBackgroundColor</c> fix already accepts.</para>
    /// </summary>
    public Task Initialization => _initialization ??= InitializeAsync();

    private async Task InitializeAsync()
    {
        var userDataFolder = WebView2UserDataFolder();
        Directory.CreateDirectory(userDataFolder);

        // Same white-flash fix as TerminalView.InitializeAsync - must be set before
        // EnsureCoreWebView2Async, and must match this control's own HTML shell's body background
        // (BuildHtmlDocument below) rather than the terminal's.
        Browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xFF, 0x0A, 0x0A, 0x0A);

        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        await Browser.EnsureCoreWebView2Async(environment);
    }

    /// <summary>
    /// Wraps <paramref name="bodyHtml"/> (Markdig's own rendered output - already valid HTML, never
    /// escaped further here) in a small inline-styled document matching <c>Theme.xaml</c>'s dark
    /// palette, and navigates the preview to it. Awaits <see cref="Initialization"/> first, same
    /// posture as <see cref="TerminalView.AttachPtyAsync"/>.
    /// </summary>
    public async Task RenderAsync(string bodyHtml)
    {
        ArgumentNullException.ThrowIfNull(bodyHtml);
        await Initialization;
        Browser.CoreWebView2.NavigateToString(BuildHtmlDocument(bodyHtml));
    }

    /// <summary>
    /// The inline-styled HTML shell - colours copied from <c>Theme.xaml</c>'s own dark palette
    /// (<c>BackgroundBaseColor</c>, <c>TextPrimaryColor</c>, <c>TealColor</c> for links,
    /// <c>SurfaceElevatedColor</c> for code blocks, <c>StrokeColor</c> for borders/rules) so a
    /// rendered preview reads as part of the same app rather than a plain browser page. Kept as one
    /// literal string (not a separate vendored asset) since there is nothing here worth vendoring -
    /// nowhere near the size/complexity of xterm.js.
    /// </summary>
    internal static string BuildHtmlDocument(string bodyHtml)
    {
        // A plain (non-interpolated) raw string literal: the CSS below is dense with brace pairs,
        // which a $"""...""" interpolated raw string would force escaping to disambiguate from
        // interpolation syntax. A placeholder token substituted via Replace instead is simpler than
        // fighting that escaping.
        const string shell = """
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="utf-8" />
            <style>
                body {
                    background: #0A0A0A;
                    color: #F2F2F2;
                    font-family: "Segoe UI Variable Text", "Segoe UI", sans-serif;
                    font-size: 14px;
                    line-height: 1.6;
                    padding: 16px 24px;
                    margin: 0;
                }
                h1, h2, h3, h4, h5, h6 { color: #F2F2F2; border-bottom: 1px solid #2B2B2B; padding-bottom: 4px; }
                a { color: #6EC1D6; }
                code { background: #191919; padding: 2px 5px; border-radius: 4px; font-family: Consolas, monospace; }
                pre { background: #191919; padding: 12px; border-radius: 6px; overflow-x: auto; }
                pre code { background: none; padding: 0; }
                blockquote { border-left: 3px solid #2B2B2B; margin-left: 0; padding-left: 12px; color: #A9A9A9; }
                table { border-collapse: collapse; }
                th, td { border: 1px solid #2B2B2B; padding: 6px 10px; }
                hr { border: none; border-top: 1px solid #2B2B2B; }
                img { max-width: 100%; }
            </style>
            </head>
            <body>
            __ACCEL_MARKDOWN_BODY__
            </body>
            </html>
            """;

        return shell.Replace("__ACCEL_MARKDOWN_BODY__", bodyHtml);
    }

    /// <summary><c>%USERPROFILE%\.claude\accel-webview2-preview\</c> - see the class doc for why this
    /// is a separate folder from <see cref="TerminalView.WebView2UserDataFolder"/>.</summary>
    public static string WebView2UserDataFolder() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "accel-webview2-preview");

    /// <summary>Same reasoning/necessity as <see cref="TerminalView.Dispose"/> - a WPF window close
    /// alone does not reliably tear down the out-of-process WebView2 browser/renderer/GPU processes.</summary>
    public void Dispose()
    {
        Browser.Dispose();
    }
}

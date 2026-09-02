namespace Accel.App.Controls;

using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

/// <summary>
/// Shared by <see cref="TerminalView"/> and <see cref="MarkdownPreviewView"/> - both host a
/// long-lived, panel-singleton WebView2 control that can outlive many attach/render calls, so both
/// need the same two defenses against a post-resume GPU/display-driver reset killing the
/// out-of-process browser: recognizing which <see cref="CoreWebView2.ProcessFailed"/> kinds mean
/// <c>CoreWebView2</c> itself is no longer usable, and bounding an awaited WebView2 call so a
/// wedged (but not yet <c>ProcessFailed</c>-reported) process can't hang the caller forever.
/// </summary>
internal static class WebView2ProcessRecovery
{
    /// <summary>
    /// Whether <paramref name="kind"/> means the browser's own process, a render process, or the
    /// GPU process died or stopped responding - i.e. the existing <c>CoreWebView2</c> is no longer
    /// usable and the control needs to re-run its initialization (a fresh
    /// <c>CoreWebView2Environment</c>/<c>EnsureCoreWebView2Async</c>) rather than assume its
    /// one-shot "initialized" state still holds.
    /// </summary>
    public static bool RequiresReinitialization(CoreWebView2ProcessFailedKind kind) => kind switch
    {
        CoreWebView2ProcessFailedKind.BrowserProcessExited => true,
        CoreWebView2ProcessFailedKind.RenderProcessExited => true,
        CoreWebView2ProcessFailedKind.RenderProcessUnresponsive => true,
        CoreWebView2ProcessFailedKind.GpuProcessExited => true,
        _ => false,
    };

    /// <summary>
    /// Awaits <paramref name="task"/>, throwing <see cref="TimeoutException"/> instead of hanging
    /// forever if it doesn't complete within <paramref name="timeout"/>. Covers the gap between a
    /// process actually wedging and WebView2 reporting <see cref="CoreWebView2.ProcessFailed"/> for
    /// it (there is no guarantee that event fires promptly, or at all, for every hang).
    /// </summary>
    public static async Task WithTimeout(Task task, TimeSpan timeout, string description)
    {
        using var delayCts = new System.Threading.CancellationTokenSource();
        var delay = Task.Delay(timeout, delayCts.Token);
        var completed = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (completed == delay)
        {
            throw new TimeoutException($"{description} did not complete within {timeout}.");
        }

        delayCts.Cancel();
        await task.ConfigureAwait(false);
    }
}

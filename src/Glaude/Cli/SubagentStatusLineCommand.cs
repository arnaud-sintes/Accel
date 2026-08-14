using System.Text;

namespace Glaude.Cli;

/// <summary>
/// Phase 3c: the `subagentStatusLine` passthrough command. Claude Code invokes this once per
/// refresh tick, feeding the full `subagentStatusLine` payload (base hook fields, `columns`,
/// and a `tasks` array) on stdin, and expects either nothing on stdout (default row rendering
/// kept) or one `{"id":..., "content":...}` line per row it wants to override.
///
/// Glaude is a pure observer here: it forwards the payload to
/// <c>POST /events/subagent-status-line</c> and always prints nothing, so Claude Code's own
/// default rows are preserved. Per project.md, unlike the main `statusLine` command, there is
/// no "blank status bar" hazard for this hook - omitting output is always safe - so the POST
/// does not need to be fire-and-forget-detached the way Phase 5's statusLine command does, but
/// it still must not block for long if the server is slow or unreachable.
///
/// Not a real CLI verb yet (Phase 4 hasn't landed) - this is a plain static entry point that a
/// later `Program.cs` / arg parser can call directly once the `subagent-statusline` verb exists.
/// </summary>
public static class SubagentStatusLineCommand
{
    /// <summary>Short budget for the POST - long enough to reach a local server, short enough
    /// to never meaningfully delay a status-line refresh tick.</summary>
    private static readonly TimeSpan PostTimeout = TimeSpan.FromMilliseconds(500);

    private const string Route = "/events/subagent-status-line";

    /// <summary>
    /// Reads the `subagentStatusLine` payload from stdin to end, POSTs it to
    /// `http://127.0.0.1:{port}/events/subagent-status-line`, then always exits 0 having
    /// printed nothing. Every failure mode (server down, malformed/empty stdin, timeout, ...)
    /// is swallowed - this command must never throw and must never write to stdout.
    /// </summary>
    public static async Task<int> RunAsync(int port, CancellationToken cancellationToken = default)
    {
        return await RunAsync(port, Console.In, httpClient: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Test seam: allows supplying the stdin reader and/or an <see cref="HttpClient"/> so
    /// tests can feed arbitrary payloads and inspect/replace the transport without touching
    /// real stdin or spinning up sockets unnecessarily. Behaviour (never throw, never print,
    /// always return 0) is identical to <see cref="RunAsync(int, CancellationToken)"/>.
    /// </summary>
    public static async Task<int> RunAsync(
        int port,
        TextReader stdin,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string body = await ReadStdinAsync(stdin, cancellationToken).ConfigureAwait(false);
            await PostAsync(port, body, httpClient, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Intentionally swallowed - per project.md, all failures here must be swallowed:
            // never throw, never print, always exit 0.
        }

        return 0;
    }

    private static async Task<string> ReadStdinAsync(TextReader stdin, CancellationToken cancellationToken)
    {
        try
        {
            return await stdin.ReadToEndAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        }
        catch
        {
            // A stdin read failure just means "nothing to forward" - not a reason to throw.
            return string.Empty;
        }
    }

    private static async Task PostAsync(int port, string body, HttpClient? httpClient, CancellationToken outerToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
            cts.CancelAfter(PostTimeout);

            bool ownsClient = httpClient is null;
            HttpClient client = httpClient ?? new HttpClient();
            try
            {
                string url = $"http://127.0.0.1:{port}{Route}";
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await client
                    .PostAsync(url, content, cts.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (ownsClient)
                {
                    client.Dispose();
                }
            }
        }
        catch
        {
            // Server down, connection refused, timeout, cancelled, ... - all irrelevant to a
            // pure-observer command that must never surface a failure.
        }
    }
}

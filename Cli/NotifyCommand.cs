using System.Text;

namespace Accel.Cli;

/// <summary>
/// The `notify` verb: Accel's own replacement for the old `curl.exe`-based event hooks
/// (SessionStart/SessionEnd/SubagentStart/SubagentStop). Claude Code invokes this once per
/// event, feeding the hook payload on stdin; this command forwards it to
/// `POST http://127.0.0.1:{port}{route}` and always exits 0, having printed nothing.
///
/// Unlike `curl.exe -s`, which exits non-zero (with no stderr, since `-s` silences it) whenever
/// Accel isn't listening yet, this command swallows every failure mode itself — a connection
/// refusal is exactly as expected as a success from Claude Code's point of view. This is what
/// stops "SessionStart:startup hook error" from surfacing at every session start when the Accel
/// app hasn't been launched yet.
/// </summary>
public static class NotifyCommand
{
    private static readonly TimeSpan PostTimeout = TimeSpan.FromSeconds(2);

    public static async Task<int> RunAsync(int port, string route, CancellationToken cancellationToken = default)
    {
        return await RunAsync(port, route, Console.In, httpClient: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Test seam: supply the stdin reader and/or an <see cref="HttpClient"/> directly.</summary>
    public static async Task<int> RunAsync(
        int port,
        string route,
        TextReader stdin,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!string.IsNullOrEmpty(route))
            {
                string body = await ReadStdinAsync(stdin, cancellationToken).ConfigureAwait(false);
                await PostAsync(port, route, body, httpClient, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Intentionally swallowed - a hook notification must never surface a failure to
            // Claude Code, whether that's a malformed payload, a closed socket, or Accel simply
            // not running yet.
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
            return string.Empty;
        }
    }

    private static async Task PostAsync(int port, string route, string body, HttpClient? httpClient, CancellationToken outerToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
            cts.CancelAfter(PostTimeout);

            bool ownsClient = httpClient is null;
            HttpClient client = httpClient ?? new HttpClient();
            try
            {
                string url = $"http://127.0.0.1:{port}{route}";
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
            // Server down, connection refused, timeout, cancelled, ... - all expected, none are
            // reasons to make Claude Code think the hook itself failed.
        }
    }
}

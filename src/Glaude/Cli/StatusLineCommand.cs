namespace Glaude.Cli;

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Glaude.Settings;

/// <summary>Knobs for <see cref="StatusLineCommand"/>; every one has a safe default.</summary>
public sealed class StatusLineCommandOptions
{
    /// <summary>Port of the local Glaude server that receives the fire-and-forget POST.</summary>
    public int Port { get; init; } = GlaudeHookSpec.DefaultPort;

    /// <summary>Where the captured original <c>statusLine</c> object lives. Null = no chain.</summary>
    public IStatusLineChainStore? ChainStore { get; init; }

    /// <summary>Payload source. Null = the process's real stdin.</summary>
    public Stream? Input { get; init; }

    /// <summary>Where the status bar text goes. Null = the process's real stdout.</summary>
    public Stream? Output { get; init; }

    /// <summary>
    /// Budget for the chained original command. Claude Code debounces status-line updates by
    /// 300 ms and kills the in-flight script when a new update triggers, so total latency is a
    /// correctness property, not a nicety.
    /// </summary>
    public TimeSpan ChainedCommandTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Guard against a stdin that is never closed (e.g. run interactively by hand).</summary>
    public TimeSpan StdinReadTimeout { get; init; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// Bounded grace given to the in-flight POST <b>after</b> stdout has been written and
    /// flushed. This never delays the status bar — it only avoids killing a loopback request
    /// that is already on the wire when the process would otherwise exit. Set to
    /// <see cref="TimeSpan.Zero"/> to disable.
    /// </summary>
    public TimeSpan PostCompletionGrace { get; init; } = TimeSpan.FromMilliseconds(200);
}

/// <summary>
/// <c>glaude statusline</c> — the command Claude Code runs as its top-level <c>statusLine</c>.
///
/// <para><b>The one invariant that matters:</b> this command's stdout <i>is</i> the rendered
/// status bar. Printing nothing blanks the user's status bar on every refresh. So it always
/// prints something non-empty and always exits 0, whatever fails internally.</para>
///
/// <para>Ordering (project.md, "statusLine — must chain, not clobber"): read stdin fully →
/// spawn the POST <b>fire-and-forget, never awaited</b> → re-invoke the captured original
/// command with the same stdin buffer → relay its stdout <b>byte-for-byte</b>. The chained
/// stdout is opaque display text and is never parsed for metrics; metrics come exclusively
/// from the stdin JSON, which the server extracts from the POSTed body.</para>
/// </summary>
public static class StatusLineCommand
{
    /// <summary>Absolute last-resort bar text. Never empty, never formatted from user input.</summary>
    public const string HardcodedFallbackLine = "Claude Code";

    private const string StatusLineRoute = "/events/status-line";

    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>Convenience entry point for the CLI: real stdin/stdout, default budgets.</summary>
    public static Task<int> RunAsync(int port, IStatusLineChainStore? chainStore, CancellationToken cancellationToken = default) =>
        RunAsync(new StatusLineCommandOptions { Port = port, ChainStore = chainStore }, cancellationToken);

    /// <summary>
    /// Runs the status-line passthrough. <b>Always returns 0</b> and always writes non-empty
    /// output; no exception is allowed to escape.
    /// </summary>
    public static async Task<int> RunAsync(StatusLineCommandOptions options, CancellationToken cancellationToken = default)
    {
        options ??= new StatusLineCommandOptions();

        byte[]? output = null;
        byte[] payload = Array.Empty<byte>();
        Task? postTask = null;

        try
        {
            // 1. Read stdin to the end, first and completely: it is both the POST body and the
            //    buffer the chained command must be re-fed (the original process is long gone
            //    and cannot re-read it).
            payload = await ReadStdinAsync(options, cancellationToken).ConfigureAwait(false);

            // 2. Fire-and-forget the POST. Deliberately not awaited anywhere before stdout is
            //    written — a dead/hung server must never delay or blank the status bar.
            postTask = SpawnPost(options.Port, payload);

            // 3. Re-invoke the captured original status line, if there was one.
            output = await RunChainedAsync(options, payload, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Top-level catch-all: anything at all going wrong degrades to the default line.
            output = null;
        }

        if (output is null || output.Length == 0)
        {
            output = Encode(SynthesizeDefaultLine(payload));
        }

        WriteOutput(options, output);

        await AwaitPostGraceAsync(postTask, options.PostCompletionGrace).ConfigureAwait(false);

        return 0;
    }

    // ---- stdin ----------------------------------------------------------------------

    private static async Task<byte[]> ReadStdinAsync(StatusLineCommandOptions options, CancellationToken cancellationToken)
    {
        Stream input;
        try
        {
            input = options.Input ?? Console.OpenStandardInput();
        }
        catch
        {
            return Array.Empty<byte>();
        }

        var buffer = new MemoryStream();
        var copy = Task.Run(
            async () =>
            {
                try
                {
                    await input.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Closed/absent/unreadable stdin — treated as an empty payload.
                }
            },
            CancellationToken.None);

        var finished = await Task.WhenAny(copy, Task.Delay(options.StdinReadTimeout, cancellationToken)).ConfigureAwait(false);

        // On timeout the copy is still running and owns `buffer`; reading it would race, so the
        // payload is simply dropped. The command still prints (the hardcoded fallback).
        return finished == copy ? buffer.ToArray() : Array.Empty<byte>();
    }

    // ---- POST (fire-and-forget) -----------------------------------------------------

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(500),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
    }

    /// <summary>
    /// Starts the POST and returns immediately. The returned task is exposed only so the caller
    /// can optionally give it a bounded grace period *after* stdout is flushed — it is never a
    /// precondition of producing output.
    /// </summary>
    private static Task? SpawnPost(int port, byte[] payload)
    {
        if (payload.Length == 0)
        {
            return null;
        }

        try
        {
            return Task.Run(
                async () =>
                {
                    try
                    {
                        using var content = new ByteArrayContent(payload);
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                        using var request = new HttpRequestMessage(
                            HttpMethod.Post,
                            GlaudeHookSpec.BuildUrl(port, StatusLineRoute))
                        {
                            Content = content,
                        };
                        request.Headers.TryAddWithoutValidation(
                            HookEntry.MarkerHeaderPrefix.TrimEnd(':'),
                            "StatusLine");

                        using var response = await Http
                            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // Server down, port closed, timeout — all irrelevant to the status bar.
                    }
                },
                CancellationToken.None);
        }
        catch
        {
            return null;
        }
    }

    private static async Task AwaitPostGraceAsync(Task? postTask, TimeSpan grace)
    {
        if (postTask is null || grace <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            await Task.WhenAny(postTask, Task.Delay(grace)).ConfigureAwait(false);
        }
        catch
        {
            // Never surfaces.
        }
    }

    // ---- chained original -----------------------------------------------------------

    private static async Task<byte[]?> RunChainedAsync(
        StatusLineCommandOptions options,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var command = TryGetChainedCommand(options.ChainStore);
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var result = await ShellCommandRunner
            .RunAsync(command, payload, options.ChainedCommandTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Started || result.TimedOut)
        {
            return null;
        }

        // Non-zero exit with real output is still relayed: the third party's bar text is more
        // valuable to the user than its exit code, and a broken script normally prints nothing
        // anyway — which is caught by the emptiness check below.
        return IsBlank(result.StandardOutput) ? null : result.StandardOutput;
    }

    private static string? TryGetChainedCommand(IStatusLineChainStore? store)
    {
        if (store is null)
        {
            return null;
        }

        try
        {
            if (!store.TryGet(StatusLineField.StatusLine, out var capture) || capture is null || !capture.HadOriginal)
            {
                return null;
            }

            return GlaudeHookSpec.GetCommand(capture.Original);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsBlank(byte[] bytes)
    {
        foreach (var b in bytes)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0))
            {
                return false;
            }
        }

        return true;
    }

    // ---- default line ---------------------------------------------------------------

    /// <summary>
    /// Claude Code's rough default equivalent — model display name + current directory —
    /// synthesized <b>tolerantly</b> from the status-line stdin JSON. Every field is optional;
    /// an unparseable payload still yields <see cref="HardcodedFallbackLine"/>.
    /// </summary>
    public static string SynthesizeDefaultLine(byte[]? payload)
    {
        if (payload is null || payload.Length == 0)
        {
            return HardcodedFallbackLine;
        }

        try
        {
            return SynthesizeDefaultLine(new UTF8Encoding(false).GetString(payload));
        }
        catch
        {
            return HardcodedFallbackLine;
        }
    }

    /// <summary>Text overload of <see cref="SynthesizeDefaultLine(byte[])"/>.</summary>
    public static string SynthesizeDefaultLine(string? json)
    {
        string? model = null;
        string? dir = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(json) && JsonNode.Parse(json!) is JsonObject root)
            {
                model = ReadString(root["model"], "display_name")
                        ?? ReadString(root["model"], "id")
                        ?? AsString(root["model"]);

                dir = ReadString(root["workspace"], "current_dir")
                      ?? ReadString(root["workspace"], "project_dir")
                      ?? AsString(root["cwd"]);
            }
        }
        catch
        {
            // Malformed JSON is expected in the wild (schemas evolve); fall through.
        }

        var line = string.Join(" · ", new[] { model, dir }.Where(static s => !string.IsNullOrWhiteSpace(s)));
        return string.IsNullOrWhiteSpace(line) ? HardcodedFallbackLine : line;
    }

    private static string? ReadString(JsonNode? parent, string property)
    {
        if (parent is not JsonObject obj)
        {
            return null;
        }

        return AsString(obj[property]);
    }

    private static string? AsString(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var s))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    // ---- output ---------------------------------------------------------------------

    private static byte[] Encode(string line)
    {
        try
        {
            var text = string.IsNullOrWhiteSpace(line) ? HardcodedFallbackLine : line;
            return new UTF8Encoding(false).GetBytes(text + "\n");
        }
        catch
        {
            return new byte[] { (byte)'C', (byte)'l', (byte)'a', (byte)'u', (byte)'d', (byte)'e', (byte)'\n' };
        }
    }

    private static void WriteOutput(StatusLineCommandOptions options, byte[] output)
    {
        try
        {
            // Raw byte stream, never Console.Write: the chained command's stdout must reach
            // Claude Code exactly as produced, with no re-encoding or newline translation.
            var stream = options.Output ?? Console.OpenStandardOutput();
            stream.Write(output, 0, output.Length);
            stream.Flush();
        }
        catch
        {
            // Even stdout being gone must not turn into a non-zero exit.
        }
    }
}

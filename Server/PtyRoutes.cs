using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Accel.Orchestration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Accel.Server;

/// <summary>
/// P2-T4: the seam <see cref="PtyRoutes"/> pumps bytes through, kept minimal on purpose so tests
/// can fake a live session without a real <see cref="PtySession"/> (which needs an actual ConPTY
/// + child process). <see cref="PtySessionEndpoint"/> is the production adapter over the real
/// thing; a test double just implements this interface directly.
/// </summary>
public interface IPtyEndpoint
{
    /// <summary>Decoded terminal output - see <see cref="PtySession.Output"/>.</summary>
    ChannelReader<string> Output { get; }

    /// <summary>Raw bytes to the child's stdin - see <see cref="PtySession.Write(ReadOnlySpan{byte})"/>.</summary>
    void Write(ReadOnlySpan<byte> bytes);

    /// <summary>Resizes the pseudoconsole - see <see cref="PtySession.Resize"/>.</summary>
    void Resize(int columns, int rows);
}

/// <summary>Production <see cref="IPtyEndpoint"/>: a thin forwarding wrapper over a real <see cref="PtySession"/>.</summary>
public sealed class PtySessionEndpoint : IPtyEndpoint
{
    private readonly PtySession _session;

    public PtySessionEndpoint(PtySession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public ChannelReader<string> Output => _session.Output;

    public void Write(ReadOnlySpan<byte> bytes) => _session.Write(bytes);

    public void Resize(int columns, int rows) => _session.Resize(columns, rows);
}

/// <summary>
/// <c>tabId -&gt; IPtyEndpoint</c> lookup backing <c>/pty/{tabId}</c>.
///
/// <para><b>Provisional scope (deliberate, see P2-T4's task note).</b> Real session creation
/// (P2-T6) and the tab lifecycle (P3-T1/P3-T2's <c>PtyRegistry</c>) do not exist yet at the time
/// this route was written, so this class is a minimal, self-owned registry rather than a
/// dependency on either of those - whoever lands P2-T6/P3-T2 should either replace this with (or
/// have it delegate to) the real tab registry, so a session's lifetime is owned in exactly one
/// place. Until then, callers (production wiring or tests) register/unregister endpoints
/// directly.</para>
///
/// <para><b>Why lookups must be uniform.</b> <c>tabId</c> is meant to be unguessable (a
/// server-minted GUID, per the plan's security requirement), so a wrong value - malformed,
/// well-formed-but-unknown, or anything else - must come back as a plain 404 with no
/// distinguishing detail and no side effect. A single dictionary lookup already gives that for
/// free: there is no separate "is this a GUID" parse step that could branch differently (and
/// therefore leak format information) from the "is it registered" check.</para>
/// </summary>
public sealed class PtyRouteRegistry
{
    private readonly ConcurrentDictionary<string, IPtyEndpoint> _endpoints = new(StringComparer.Ordinal);

    /// <summary>Registers an endpoint under <paramref name="tabId"/>, replacing any prior registration.</summary>
    public void Register(string tabId, IPtyEndpoint endpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(tabId);
        ArgumentNullException.ThrowIfNull(endpoint);
        _endpoints[tabId] = endpoint;
    }

    /// <summary>Convenience over <see cref="Register(string, IPtyEndpoint)"/> for a real <see cref="PtySession"/>.</summary>
    public void RegisterSession(string tabId, PtySession session) => Register(tabId, new PtySessionEndpoint(session));

    /// <summary>Removes the registration for <paramref name="tabId"/>, if any. Does not dispose the endpoint.</summary>
    public bool Unregister(string tabId) => _endpoints.TryRemove(tabId, out _);

    /// <summary>Looks up <paramref name="tabId"/>. False for anything not registered, with no further detail.</summary>
    public bool TryGet(string tabId, out IPtyEndpoint? endpoint) => _endpoints.TryGetValue(tabId, out endpoint);
}

/// <summary>
/// P2-T4: <c>/pty/{tabId}</c>, the loopback WebSocket route that pumps bytes both ways between a
/// browser-hosted xterm.js client (P2-T5b) and a live <see cref="PtySession"/>, plus an in-band
/// JSON control frame for resize.
///
/// <para><b>Security posture (read this before touching the route).</b> <see cref="EventServer"/>
/// binds only to <c>127.0.0.1</c> (see its class doc), so this endpoint is not reachable from the
/// network - but loopback is not nothing: any local process (a browser tab navigated to the
/// wrong URL, another user's process on a shared machine, malware) can still open a TCP
/// connection to it, and what is on the other end of that connection is raw stdin to a live
/// `claude` process - equivalent to arbitrary code execution if anything can attach. Three
/// independent mitigations, in the order the handler applies them:
/// <list type="number">
/// <item><b>Origin check.</b> The only legitimate caller is the WebView2 control's embedded
/// xterm.js page, served from the virtual host <see cref="ExpectedOrigin"/> (P2-T5's
/// <c>TerminalView.VirtualHostName</c>, <c>"accel-terminal"</c>, navigated to as
/// <c>https://accel-terminal/index.html</c>). A browser sends that exact value as the
/// <c>Origin</c> header on the WebSocket upgrade; anything else (a missing header, a different
/// origin, a hand-rolled non-browser client that does not set it) is rejected before the tabId is
/// even looked at, with <see cref="StatusCodes.Status403Forbidden"/> and no body.</item>
/// <item><b>Unguessable, uniformly-checked tabId.</b> <see cref="PtyRouteRegistry.TryGet"/> is one
/// dictionary lookup - format-invalid and well-formed-but-unknown values fail identically, with
/// <see cref="StatusCodes.Status404NotFound"/> and no body, and no session is created, touched, or
/// otherwise affected by a lookup miss.</item>
/// <item><b>Deferred, documented.</b> Neither check above is a substitute for a real per-session
/// capability token. The plan's own execution hints call the Origin/tabId pair a partial mitigation
/// pending later phase work; a hand-rolled client on the same machine that already knows (or brute
/// forces, though a GUID makes that infeasible) a live tabId and can spoof an Origin header (easy
/// outside a real browser) still gets in. Real auth - e.g. a per-session token minted alongside the
/// tabId and required in a query string or header - is out of scope for P2-T4 and is not implemented
/// here; this comment exists so it is not mistaken for "solved".</item>
/// </list>
/// </para>
///
/// <para><b>Frame discipline (binary = raw input, text = JSON control - P2-T5b must match this).</b>
/// Server-to-client frames are always <see cref="WebSocketMessageType.Text"/>, carrying the decoded
/// UTF-8 text <see cref="PtySession.Output"/> already produced (P2-T3's stateful decoder has already
/// done the one-time, correct-by-construction UTF-8 decode; re-encoding it as UTF-8 bytes for the
/// text frame is lossless, unlike trying to decode it a second time). Client-to-server frames are
/// split by WebSocket frame type, not by inspecting payload bytes:
/// <list type="bullet">
/// <item><see cref="WebSocketMessageType.Binary"/> is raw input bytes, written verbatim to
/// <see cref="IPtyEndpoint.Write"/> with no text-encoding round trip. This matters for control bytes
/// - Ctrl+C is the single byte <c>0x03</c>, arrow keys and bracketed paste are ESC-prefixed
/// sequences - which must reach the child exactly as typed. Routing them through a
/// UTF-8-string-then-back-to-bytes conversion is exactly the kind of "round trip corruption" the
/// task called out: <c>0x03</c> alone is not valid UTF-8, so decoding-then-reencoding it as a
/// .NET <c>string</c> first would either throw or replace it with U+FFFD.</item>
/// <item><see cref="WebSocketMessageType.Text"/> is the control channel: currently exactly one
/// message shape, <c>{"resize":[cols,rows]}</c>, parsed as JSON and forwarded to
/// <see cref="IPtyEndpoint.Resize"/>. Anything text-framed that is not that shape is parsed
/// best-effort and ignored on failure (never crashes the connection) - there is room to add more
/// control messages under the same text-frame convention later without a protocol version bump.</item>
/// </list>
/// The two conventions can never collide because WebSocket frame type is protocol-level metadata,
/// not something a client can spoof by choosing payload content.</para>
/// </summary>
public static class PtyRoutes
{
    /// <summary>
    /// The only <c>Origin</c> value accepted on the WebSocket upgrade. Deliberately a literal, not
    /// a reference to <c>TerminalView.VirtualHostName</c>: <c>Server/</c> is the backend layer the
    /// plan keeps UI-agnostic through Phase 6, and this route should not gain a compile-time
    /// dependency on a WPF/WebView2 control just to read one constant. If P2-T5's virtual host name
    /// ever changes, this literal must be updated to match - <c>"https://" + "accel-terminal"</c>,
    /// i.e. the scheme WebView2 uses for <c>SetVirtualHostNameToFolderMapping</c> plus
    /// <c>TerminalView.VirtualHostName</c> verbatim, no port (virtual hosts are not real network
    /// addresses, so there is none).
    /// </summary>
    public const string ExpectedOrigin = "https://accel-terminal";

    private const int ReceiveBufferSize = 8192;

    /// <summary>Maps <c>/pty/{tabId}</c> onto <paramref name="app"/>, using <paramref name="registry"/>
    /// for the tabId lookup. Also registers the ASP.NET Core WebSocket middleware
    /// (<c>UseWebSockets</c>) - safe to call even if nothing else in this app needs it.</summary>
    public static void Map(WebApplication app, PtyRouteRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(registry);

        app.UseWebSockets();

        app.MapGet("/pty/{tabId}", async (HttpContext context, string tabId) =>
        {
            await HandleAsync(context, tabId, registry);
        });
    }

    private static async Task HandleAsync(HttpContext context, string tabId, PtyRouteRegistry registry)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Origin check first: it is a property of the connection, not of the tabId, so checking it
        // first means a caller with a bad Origin learns nothing about whether any tabId - let alone
        // this one - is valid.
        string? origin = context.Request.Headers.Origin;
        if (!string.Equals(origin, ExpectedOrigin, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Uniform miss: no distinction between "not a well-formed id" and "well-formed but
        // unregistered", no allocation beyond the lookup itself, and nothing touched on a miss.
        if (!registry.TryGet(tabId, out var endpoint) || endpoint is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await PumpAsync(socket, endpoint, context.RequestAborted);
    }

    /// <summary>
    /// Runs the two byte pumps concurrently until either one ends (child gone / socket closed /
    /// cancelled), then cancels the other and closes the socket. A <see cref="WebSocket"/> supports
    /// exactly one concurrent send and one concurrent receive, which is exactly the shape of the two
    /// loops below - never call <see cref="WebSocket.SendAsync"/> or <see cref="WebSocket.ReceiveAsync"/>
    /// from more than one place at a time on the same socket.
    /// </summary>
    private static async Task PumpAsync(WebSocket socket, IPtyEndpoint endpoint, CancellationToken connectionAborted)
    {
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(connectionAborted);

        Task outputTask = PumpOutputAsync(socket, endpoint, pumpCts.Token);
        Task inputTask = PumpInputAsync(socket, endpoint, pumpCts.Token);

        try
        {
            await Task.WhenAny(outputTask, inputTask);
        }
        finally
        {
            // Whichever pump ended first, the other one is now pumping into/out of a connection
            // that is going away - cancel it rather than leaving it to block forever on the next
            // read/write.
            try
            {
                pumpCts.Cancel();
            }
            catch
            {
                // Cancelling an already-cancelled/disposed source is a no-op we do not care about.
            }

            try
            {
                await Task.WhenAll(outputTask, inputTask);
            }
            catch
            {
                // Both loops are documented to swallow their own expected teardown exceptions; this
                // catch is only a backstop so a genuinely unexpected one cannot escape and crash the
                // request pipeline.
            }

            await TryCloseAsync(socket);
        }
    }

    /// <summary><see cref="PtySession.Output"/> (or a fake's equivalent) to the client, one text frame
    /// per decoded chunk. Ends when the channel completes (session gone) or is cancelled.</summary>
    private static async Task PumpOutputAsync(WebSocket socket, IPtyEndpoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (string chunk in endpoint.Output.ReadAllAsync(cancellationToken))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(chunk);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal teardown path (connection closing, or the other pump ended first).
        }
        catch (WebSocketException)
        {
            // The socket is gone from the other side; PumpAsync's WhenAny/Cancel handles the rest.
        }
        catch (ObjectDisposedException)
        {
            // Raced the socket being disposed during teardown - same handling as above.
        }
    }

    /// <summary>Client frames to <paramref name="endpoint"/>: binary frames are raw input bytes,
    /// text frames are parsed as the <c>{"resize":[cols,rows]}</c> control message (see the class
    /// doc's framing convention). Ends on a Close frame, cancellation, or the socket going away.</summary>
    private static async Task PumpInputAsync(WebSocket socket, IPtyEndpoint endpoint, CancellationToken cancellationToken)
    {
        byte[] receiveBuffer = new byte[ReceiveBufferSize];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(receiveBuffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(receiveBuffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (message.Length == 0)
                {
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    WriteInput(endpoint, message);
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    HandleControlMessage(endpoint, message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal teardown path.
        }
        catch (WebSocketException)
        {
            // Client vanished mid-read.
        }
        catch (ObjectDisposedException)
        {
            // Raced the socket being disposed during teardown.
        }
    }

    private static void WriteInput(IPtyEndpoint endpoint, MemoryStream message)
    {
        try
        {
            endpoint.Write(message.GetBuffer().AsSpan(0, (int)message.Length));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The child's stdin pipe is already gone (session ended concurrently) - dropping one
            // keystroke into a dead session is not worth tearing down the pump loop harder than the
            // outer PumpAsync already will once the output side observes the same thing.
        }
    }

    private static void HandleControlMessage(IPtyEndpoint endpoint, MemoryStream message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (root.TryGetProperty("resize", out JsonElement resize)
                && resize.ValueKind == JsonValueKind.Array
                && resize.GetArrayLength() == 2)
            {
                int columns = resize[0].GetInt32();
                int rows = resize[1].GetInt32();
                endpoint.Resize(columns, rows);
            }

            // Unrecognized-but-well-formed control messages are ignored, not rejected: this is the
            // one text-framed shape implemented today, but the convention (text = control JSON)
            // leaves room to add more without a protocol version bump.
        }
        catch (JsonException)
        {
            // Malformed control frame: ignore rather than tearing down the connection over it.
        }
    }

    private static async Task TryCloseAsync(WebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "pty session ended", CancellationToken.None);
            }
        }
        catch
        {
            // Best-effort close - the client may already be gone.
        }
    }
}

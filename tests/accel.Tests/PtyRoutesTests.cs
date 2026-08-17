using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Accel.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Accel.Tests;

/// <summary>
/// Integration tests for P2-T4's <c>/pty/{tabId}</c> WebSocket route, against a real
/// <see cref="EventServer"/> bound to an ephemeral port - mirrors <c>RootsRouteTests</c>'s /
/// <c>StateQueryRoutesTests</c>'s pattern. Uses a <see cref="FakePtyEndpoint"/> in place of a real
/// <see cref="Accel.Orchestration.PtySession"/> (which would need a live ConPTY/child process) -
/// see <see cref="Accel.Server.IPtyEndpoint"/>, the seam introduced for exactly this purpose.
/// </summary>
public class PtyRoutesTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HttpClient? _client;
    private Uri? _wsBaseUri;
    private PtyRouteRegistry? _registry;

    public async Task InitializeAsync()
    {
        _registry = new PtyRouteRegistry();
        _app = EventServer.Build(0, ptySessions: _registry);
        await _app.StartAsync();

        var addressesFeature = _app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();

        string address = addressesFeature!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
        _wsBaseUri = new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal));
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnknownTabId_ReturnsNotFound_WithNoSideEffects()
    {
        using var request = NewUpgradeRequest("/pty/" + Guid.NewGuid().ToString("N"));

        var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(_registry!.TryGet("anything", out _));
        Assert.Equal(0, response.Content.Headers.ContentLength ?? 0);
    }

    [Fact]
    public async Task MalformedTabId_ReturnsSameNotFoundAsUnknownTabId_NoFormatDistinction()
    {
        using var wellFormedButUnknown = NewUpgradeRequest("/pty/" + Guid.NewGuid().ToString("N"));
        using var malformed = NewUpgradeRequest("/pty/not-a-guid-at-all");

        var responseA = await _client!.SendAsync(wellFormedButUnknown);
        var responseB = await _client!.SendAsync(malformed);

        Assert.Equal(HttpStatusCode.NotFound, responseA.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, responseB.StatusCode);
    }

    [Fact]
    public async Task WrongOrigin_IsRejected_BeforeTabIdIsConsidered()
    {
        string tabId = Guid.NewGuid().ToString("N");
        _registry!.Register(tabId, new FakePtyEndpoint());

        using var request = NewUpgradeRequest("/pty/" + tabId, origin: "https://not-the-terminal-host");

        var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MissingOrigin_IsRejected()
    {
        string tabId = Guid.NewGuid().ToString("N");
        _registry!.Register(tabId, new FakePtyEndpoint());

        using var request = NewUpgradeRequest("/pty/" + tabId, origin: null);

        var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ResizeControlFrame_ReachesTheTargetEndpoint()
    {
        string tabId = Guid.NewGuid().ToString("N");
        var fake = new FakePtyEndpoint();
        _registry!.Register(tabId, fake);

        using var socket = await ConnectAsync(tabId);

        byte[] payload = Encoding.UTF8.GetBytes("{\"resize\":[120,40]}");
        await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        var resize = await fake.NextResizeAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((120, 40), resize);

        await CloseAsync(socket);
    }

    [Fact]
    public async Task BinaryInputFrame_IsWrittenVerbatimToTheEndpoint_IncludingControlBytes()
    {
        string tabId = Guid.NewGuid().ToString("N");
        var fake = new FakePtyEndpoint();
        _registry!.Register(tabId, fake);

        using var socket = await ConnectAsync(tabId);

        byte[] ctrlC = { 0x03 };
        await socket.SendAsync(ctrlC, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);

        byte[] written = await fake.NextWriteAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ctrlC, written);

        await CloseAsync(socket);
    }

    [Fact]
    public async Task OutputPublishedByTheSession_ArrivesAsATextFrame()
    {
        string tabId = Guid.NewGuid().ToString("N");
        var fake = new FakePtyEndpoint();
        _registry!.Register(tabId, fake);

        using var socket = await ConnectAsync(tabId);

        fake.Publish("hello from claude\r\n");

        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.Equal("hello from claude\r\n", Encoding.UTF8.GetString(buffer, 0, result.Count));

        await CloseAsync(socket);
    }

    private HttpRequestMessage NewUpgradeRequest(string path, string? origin = PtyRoutes.ExpectedOrigin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Connection.Add("Upgrade");
        request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", Convert.ToBase64String(Guid.NewGuid().ToByteArray()));
        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        return request;
    }

    private async Task<ClientWebSocket> ConnectAsync(string tabId)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", PtyRoutes.ExpectedOrigin);
        await socket.ConnectAsync(new Uri(_wsBaseUri!, "/pty/" + tabId), CancellationToken.None);
        return socket;
    }

    private static async Task CloseAsync(ClientWebSocket socket)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
        }
        catch
        {
            // Best-effort - the point of the test is already proven by the time this runs.
        }
        finally
        {
            socket.Dispose();
        }
    }

    /// <summary>
    /// Test double for <see cref="IPtyEndpoint"/>: records every write/resize and lets a test
    /// publish output chunks on demand, with no real ConPTY/child process involved.
    /// </summary>
    private sealed class FakePtyEndpoint : IPtyEndpoint
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        private readonly Channel<byte[]> _writes = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<(int Columns, int Rows)> _resizes = Channel.CreateUnbounded<(int, int)>();

        public ChannelReader<string> Output => _output.Reader;

        public void Write(ReadOnlySpan<byte> bytes) => _writes.Writer.TryWrite(bytes.ToArray());

        public void Resize(int columns, int rows) => _resizes.Writer.TryWrite((columns, rows));

        public void Publish(string text) => _output.Writer.TryWrite(text);

        public async Task<byte[]> NextWriteAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            return await _writes.Reader.ReadAsync(cts.Token);
        }

        public async Task<(int Columns, int Rows)> NextResizeAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            return await _resizes.Reader.ReadAsync(cts.Token);
        }
    }
}

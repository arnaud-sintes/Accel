using System.Net;
using System.Text;
using Accel.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Accel.Tests;

/// <summary>
/// Integration-style tests against a locally started EventServer instance bound to an
/// ephemeral port (0), so these never clash with a real running Accel instance on 40010.
/// </summary>
public class EventServerTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _app = EventServer.Build(0);
        await _app.StartAsync();

        var addressesFeature = _app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();

        string address = addressesFeature!.Addresses.First();

        _client = new HttpClient { BaseAddress = new Uri(address) };
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

    [Theory]
    [InlineData("/events/session-start")]
    [InlineData("/events/session-end")]
    [InlineData("/events/subagent-start")]
    [InlineData("/events/subagent-stop")]
    [InlineData("/events/status-line")]
    [InlineData("/events/subagent-status-line")]
    public async Task PostValidJson_ReturnsNoContent(string route)
    {
        const string json = """{"session_id":"abc-123","hook_event_name":"Test"}""";

        var response = await _client!.PostAsync(
            route,
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("/events/subagent-start")]
    [InlineData("/events/session-start")]
    public async Task PostMalformedBody_StillReturnsNoContent(string route)
    {
        var response = await _client!.PostAsync(
            route,
            new StringContent("this is not { json at all", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task PostEmptyBody_StillReturnsNoContent()
    {
        var response = await _client!.PostAsync(
            "/events/status-line",
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task StatusLine_NeverWritesToConsole()
    {
        // Status-line events fire on every UI refresh tick - console output must stay minimal
        // during normal execution, so this route must never print anything.
        string sessionA = $"session-A-{Guid.NewGuid():N}";

        var originalOut = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            string jsonA = $$"""{"session_id":"{{sessionA}}"}""";

            await _client!.PostAsync("/events/status-line", new StringContent(jsonA, Encoding.UTF8, "application/json"));
            await _client!.PostAsync("/events/status-line", new StringContent(jsonA, Encoding.UTF8, "application/json"));

            Assert.Equal(string.Empty, captured.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}

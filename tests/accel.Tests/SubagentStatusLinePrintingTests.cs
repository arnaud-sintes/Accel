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
/// Phase 3c: richer handling of the `/events/subagent-status-line` route - a realistic
/// `tasks` array (mixing fully-populated entries, entries missing the version-gated
/// model/effort/contextWindowSize fields, and one entry with a malformed/wrong-typed field)
/// must always return 204 and must never crash the printer.
/// </summary>
public class SubagentStatusLinePrintingTests : IAsyncLifetime
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

    private const string RealisticPayload = """
        {
          "hook_event_name": "SubagentStatusLine",
          "session_id": "sess-1",
          "columns": 120,
          "tasks": [
            {
              "id": "task-1",
              "name": "explorer",
              "type": "general-purpose",
              "status": "running",
              "description": "Find the config file",
              "label": "Explorer",
              "startTime": "2026-08-13T10:00:00Z",
              "model": { "id": "claude-sonnet-5" },
              "effort": "medium",
              "contextWindowSize": 200000,
              "tokenCount": 4321,
              "tokenSamples": [1, 2, 3],
              "cwd": "C:\\projects\\Accel"
            },
            {
              "id": "task-2",
              "name": "builder",
              "type": "general-purpose",
              "status": "running",
              "description": "Write the fix",
              "label": "Builder",
              "startTime": "2026-08-13T10:01:00Z"
            },
            {
              "id": "task-3",
              "name": "malformed",
              "contextWindowSize": { "unexpected": "object-instead-of-number" },
              "effort": ["also", "unexpected"],
              "tokenSamples": "not-an-array"
            }
          ]
        }
        """;

    [Fact]
    public async Task PostRealisticTasksArray_Returns204AndDoesNotThrow()
    {
        var response = await _client!.PostAsync(
            "/events/subagent-status-line",
            new StringContent(RealisticPayload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task PostRealisticTasksArray_NeverWritesToConsole()
    {
        // Subagent-status-line events fire on every UI refresh tick, per task - console output
        // must stay minimal during normal execution, so this route must never print anything.
        var originalOut = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            var response = await _client!.PostAsync(
                "/events/subagent-status-line",
                new StringContent(RealisticPayload, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(string.Empty, captured.ToString());
    }

    [Fact]
    public async Task PostMalformedBody_Returns204AndDoesNotThrow()
    {
        var response = await _client!.PostAsync(
            "/events/subagent-status-line",
            new StringContent("not json at all {{{", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task PostTasksAsWrongType_Returns204AndDoesNotThrow()
    {
        // "tasks" present but not an array at all.
        const string payload = """{"hook_event_name":"SubagentStatusLine","tasks":"not-an-array"}""";

        var response = await _client!.PostAsync(
            "/events/subagent-status-line",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}

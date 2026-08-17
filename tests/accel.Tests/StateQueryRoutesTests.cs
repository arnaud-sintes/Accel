using System.Net;
using System.Text;
using System.Text.Json;
using Accel.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Accel.Tests;

/// <summary>
/// Integration tests for Phase 3d's read-only aggregation routes (`/sessions`,
/// `/sessions/{id}`, `/agents`, `/state`), against a real <see cref="EventServer"/> bound to
/// an ephemeral port - mirrors <c>MetricsPipelineTests</c>'s pattern.
/// </summary>
public class StateQueryRoutesTests : IAsyncLifetime
{
    private readonly List<string> _tempFiles = new();
    private EventServer? _server;
    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _server = new EventServer();
        _app = _server.BuildApp(0);
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

        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch { /* best effort cleanup */ }
            try { File.Delete(SiblingMetaPath(path)); } catch { /* best effort cleanup */ }
        }
    }

    private string NewSubagentTranscriptPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"agent-{Guid.NewGuid():N}.jsonl");
        _tempFiles.Add(path);
        return path;
    }

    private static string SiblingMetaPath(string transcriptPath)
    {
        string dir = Path.GetDirectoryName(transcriptPath)!;
        string baseName = Path.GetFileNameWithoutExtension(transcriptPath);
        return Path.Combine(dir, baseName + ".meta.json");
    }

    private async Task PostStatusLineAsync(string sessionId)
    {
        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["version"] = "2.1.224",
            ["model"] = new Dictionary<string, object?> { ["id"] = "claude-opus-5", ["display_name"] = "Opus" },
            ["effort"] = new Dictionary<string, object?> { ["level"] = "high" },
            ["context_window"] = new Dictionary<string, object?>
            {
                ["context_window_size"] = 200_000,
                ["used_percentage"] = 12.5,
                ["remaining_percentage"] = 87.5,
                ["current_usage"] = new Dictionary<string, object?> { ["input_tokens"] = 1000 },
            },
            ["cost"] = new Dictionary<string, object?> { ["total_cost_usd"] = 0.42 },
        });

        var response = await _client!.PostAsync(
            "/events/status-line",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<string> PostSubagentStopAsync(string parentSessionId)
    {
        string transcriptPath = NewSubagentTranscriptPath();
        string agentId = $"agent-{Guid.NewGuid():N}";

        string assistantLine = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "assistant",
            ["message"] = new Dictionary<string, object?>
            {
                ["model"] = "claude-sonnet-5",
                ["usage"] = new Dictionary<string, object?>
                {
                    ["input_tokens"] = 123,
                    ["output_tokens"] = 45,
                    ["cache_creation_input_tokens"] = 6,
                    ["cache_read_input_tokens"] = 7,
                },
            },
            ["effort"] = new Dictionary<string, object?> { ["level"] = "medium" },
        });
        File.WriteAllText(transcriptPath, assistantLine + "\n");
        File.WriteAllText(SiblingMetaPath(transcriptPath), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["agentType"] = "code-reviewer",
        }));

        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "SubagentStop",
            ["session_id"] = parentSessionId,
            ["agent_id"] = agentId,
            ["agent_type"] = "code-reviewer",
            ["agent_transcript_path"] = transcriptPath,
        });

        var response = await _client!.PostAsync(
            "/events/subagent-stop",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        return agentId;
    }

    private async Task PostSubagentStatusLineAsync(params string[] visibleAgentIds)
    {
        var tasks = visibleAgentIds.Select(id => new Dictionary<string, object?>
        {
            ["id"] = id,
            ["model"] = "claude-haiku-4-5-20251001",
            ["effort"] = "low",
            ["contextWindowSize"] = 200_000,
            ["tokenCount"] = 42,
        }).ToArray();

        string payload = JsonSerializer.Serialize(new Dictionary<string, object?> { ["tasks"] = tasks });

        var response = await _client!.PostAsync(
            "/events/subagent-status-line",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task GetSessions_AfterStatusLine_ReturnsSessionWithAsOf()
    {
        string sessionId = $"session-{Guid.NewGuid():N}";
        await PostStatusLineAsync(sessionId);

        var response = await _client!.GetAsync("/sessions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = doc.RootElement.EnumerateArray()
            .First(e => e.GetProperty("session_id").GetString() == sessionId);

        Assert.Equal("claude-opus-5", entry.GetProperty("model_id").GetString());
        Assert.Equal("Opus", entry.GetProperty("model_display_name").GetString());
        Assert.Equal("high", entry.GetProperty("effort_level").GetString());
        Assert.Equal("live", entry.GetProperty("status").GetString());
        Assert.True(entry.TryGetProperty("as_of", out _));
    }

    [Fact]
    public async Task GetSessions_AfterStatusLineWithSessionName_ReturnsSessionName()
    {
        string sessionId = $"session-{Guid.NewGuid():N}";

        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["session_name"] = "Build session monitoring application",
        });
        var postResponse = await _client!.PostAsync(
            "/events/status-line",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, postResponse.StatusCode);

        var response = await _client!.GetAsync("/sessions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = doc.RootElement.EnumerateArray()
            .First(e => e.GetProperty("session_id").GetString() == sessionId);

        Assert.Equal("Build session monitoring application", entry.GetProperty("session_name").GetString());
    }

    [Fact]
    public async Task GetAgents_AfterSubagentStatusLineWithNameAndParent_ReturnsBoth()
    {
        string agentId = $"agent-{Guid.NewGuid():N}";
        string parentSessionId = $"session-{Guid.NewGuid():N}";

        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = parentSessionId,
            ["tasks"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = agentId,
                    ["type"] = "general-purpose",
                    ["name"] = "Audit project-ui.md",
                },
            },
        });
        var postResponse = await _client!.PostAsync(
            "/events/subagent-status-line",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, postResponse.StatusCode);

        var response = await _client!.GetAsync("/agents");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = doc.RootElement.EnumerateArray()
            .First(e => e.GetProperty("agent_id").GetString() == agentId);

        Assert.Equal("Audit project-ui.md", entry.GetProperty("name").GetString());
        Assert.Equal(parentSessionId, entry.GetProperty("session_id").GetString());
    }

    [Fact]
    public async Task GetAgents_AfterSubagentStop_ReturnsEndedAgent()
    {
        string agentId = await PostSubagentStopAsync("parent-1");

        var response = await _client!.GetAsync("/agents");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = doc.RootElement.EnumerateArray()
            .First(e => e.GetProperty("agent_id").GetString() == agentId);

        Assert.Equal("ended", entry.GetProperty("status").GetString());
        Assert.Equal("parent-1", entry.GetProperty("session_id").GetString());
    }

    [Fact]
    public async Task GetSessionById_ExistingId_ReturnsSessionWithAgents()
    {
        string sessionId = $"session-{Guid.NewGuid():N}";
        await PostStatusLineAsync(sessionId);
        string agentId = await PostSubagentStopAsync(sessionId);

        var response = await _client!.GetAsync($"/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(sessionId, doc.RootElement.GetProperty("session").GetProperty("session_id").GetString());

        var agents = doc.RootElement.GetProperty("agents").EnumerateArray().ToList();
        Assert.Contains(agents, a => a.GetProperty("agent_id").GetString() == agentId);
    }

    [Fact]
    public async Task GetSessionById_UnknownId_Returns404WithValidJson()
    {
        var response = await _client!.GetAsync("/sessions/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body); // must not throw: valid JSON
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task GetState_ReturnsSessionsAndAgentsCombined()
    {
        string sessionId = $"session-{Guid.NewGuid():N}";
        await PostStatusLineAsync(sessionId);
        string agentId = await PostSubagentStopAsync("parent-2");

        var response = await _client!.GetAsync("/state");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var sessions = doc.RootElement.GetProperty("sessions").EnumerateArray().ToList();
        var agents = doc.RootElement.GetProperty("agents").EnumerateArray().ToList();

        Assert.Contains(sessions, s => s.GetProperty("session_id").GetString() == sessionId);
        Assert.Contains(agents, a => a.GetProperty("agent_id").GetString() == agentId);
    }

    [Fact]
    public async Task SessionEnd_MarksSessionEnded()
    {
        string sessionId = $"session-{Guid.NewGuid():N}";
        await PostStatusLineAsync(sessionId);

        string endPayload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "SessionEnd",
            ["session_id"] = sessionId,
        });
        var endResponse = await _client!.PostAsync(
            "/events/session-end",
            new StringContent(endPayload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, endResponse.StatusCode);

        var response = await _client!.GetAsync($"/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ended", doc.RootElement.GetProperty("session").GetProperty("status").GetString());
    }

    [Fact]
    public async Task SubagentVanishesFromTasks_WithoutSubagentStop_BecomesStale()
    {
        string agentX = $"agent-{Guid.NewGuid():N}";
        string agentY = $"agent-{Guid.NewGuid():N}";

        await PostSubagentStatusLineAsync(agentX, agentY);
        await PostSubagentStatusLineAsync(agentY); // agentX vanishes, no SubagentStop for it.

        var response = await _client!.GetAsync("/agents");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var agents = doc.RootElement.EnumerateArray().ToList();

        var xEntry = agents.First(a => a.GetProperty("agent_id").GetString() == agentX);
        var yEntry = agents.First(a => a.GetProperty("agent_id").GetString() == agentY);

        Assert.Equal("stale", xEntry.GetProperty("status").GetString());
        Assert.Equal("live", yEntry.GetProperty("status").GetString());
    }

    [Fact]
    public async Task EmptyState_AllFourRoutes_ReturnValidEmptyJson()
    {
        var sessionsResponse = await _client!.GetAsync("/sessions");
        Assert.Equal(HttpStatusCode.OK, sessionsResponse.StatusCode);
        using (var doc = JsonDocument.Parse(await sessionsResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        }

        var agentsResponse = await _client!.GetAsync("/agents");
        Assert.Equal(HttpStatusCode.OK, agentsResponse.StatusCode);
        using (var doc = JsonDocument.Parse(await agentsResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        }

        var stateResponse = await _client!.GetAsync("/state");
        Assert.Equal(HttpStatusCode.OK, stateResponse.StatusCode);
        using (var doc = JsonDocument.Parse(await stateResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("sessions").ValueKind);
            Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("agents").ValueKind);
        }

        var missingResponse = await _client!.GetAsync("/sessions/nope");
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        using (var doc = JsonDocument.Parse(await missingResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }
    }
}

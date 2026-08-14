using System.Net;
using System.Text;
using System.Text.Json;
using Glaude.Metrics;
using Glaude.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glaude.Tests;

/// <summary>
/// Integration tests wiring Phase 3's HTTP routes to Phase 3b-ii's SessionState, via a real
/// EventServer instance bound to an ephemeral port - mirrors EventServerTests's pattern, but
/// asserts on <see cref="EventServer.State"/> rather than just the 204/console output.
/// </summary>
public class MetricsPipelineTests : IAsyncLifetime
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
            try
            {
                string metaPath = SiblingMetaPath(path);
                File.Delete(metaPath);
            }
            catch { /* best effort cleanup */ }
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

    [Fact]
    public async Task SubagentStop_WithAgentTranscriptPath_PopulatesEndedAgentRecord()
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
            ["spawnDepth"] = 1,
        }));

        string subagentStopPayload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "SubagentStop",
            ["session_id"] = "parent-session-1",
            ["agent_id"] = agentId,
            ["agent_type"] = "code-reviewer",
            // Per project.md's resolved caveat: transcript_path is the PARENT's transcript,
            // and must be ignored in favor of agent_transcript_path.
            ["transcript_path"] = @"C:\some\parent\session.jsonl",
            ["agent_transcript_path"] = transcriptPath,
        });

        var response = await _client!.PostAsync(
            "/events/subagent-stop",
            new StringContent(subagentStopPayload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        bool found = _server!.State.TryGetAgent(agentId, out var record);
        Assert.True(found);
        Assert.NotNull(record);
        Assert.Equal(AgentStatus.Ended, record!.Status);
        Assert.Equal("claude-sonnet-5", record.ModelId);
        Assert.Equal("medium", record.EffortLevel);
        Assert.Equal(123, record.InputTokens);
        Assert.Equal(45, record.OutputTokens);
        Assert.Equal(6, record.CacheCreationInputTokens);
        Assert.Equal(7, record.CacheReadInputTokens);
        Assert.Equal("code-reviewer", record.AgentType);
        Assert.Equal("parent-session-1", record.ParentSessionId);
        // claude-sonnet-5 now resolves to the verified 1,000,000 window (bug-fix pass UI-H -
        // see ModelWindowTable.cs), not the old 200,000 placeholder default.
        Assert.Equal(1_000_000, record.ContextWindowSize);
    }

    [Fact]
    public async Task StatusLine_PopulatesSessionSnapshot()
    {
        string sessionId = $"session-{Guid.NewGuid():N}";

        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["version"] = "2.1.224",
            ["model"] = new Dictionary<string, object?>
            {
                ["id"] = "claude-opus-5",
                ["display_name"] = "Opus",
            },
            ["effort"] = new Dictionary<string, object?> { ["level"] = "high" },
            ["context_window"] = new Dictionary<string, object?>
            {
                ["context_window_size"] = 200_000,
                ["used_percentage"] = 12.5,
                ["remaining_percentage"] = 87.5,
                ["current_usage"] = new Dictionary<string, object?>
                {
                    ["input_tokens"] = 1000,
                    ["cache_creation_input_tokens"] = 10,
                    ["cache_read_input_tokens"] = 20,
                },
            },
            ["cost"] = new Dictionary<string, object?> { ["total_cost_usd"] = 0.42 },
        });

        var response = await _client!.PostAsync(
            "/events/status-line",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        bool found = _server!.State.TryGetSession(sessionId, out var snapshot);
        Assert.True(found);
        Assert.NotNull(snapshot);
        Assert.Equal("claude-opus-5", snapshot!.ModelId);
        Assert.Equal("Opus", snapshot.ModelDisplayName);
        Assert.Equal("high", snapshot.EffortLevel);
        Assert.Equal(200_000, snapshot.ContextWindowSize);
        Assert.Equal(1030, snapshot.UsedTokens);
        Assert.Equal(12.5, snapshot.UsedPercentage);
        Assert.Equal(87.5, snapshot.RemainingPercentage);
        Assert.Equal(0.42m, snapshot.CostUsd);
        Assert.Equal("2.1.224", snapshot.PayloadVersion);
    }

    [Fact]
    public async Task StatusLine_WithSessionName_PopulatesSessionSnapshotSessionName()
    {
        string sessionId = $"session-{Guid.NewGuid():N}";

        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["session_name"] = "Build session monitoring application",
        });

        var response = await _client!.PostAsync(
            "/events/status-line",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        bool found = _server!.State.TryGetSession(sessionId, out var snapshot);
        Assert.True(found);
        Assert.Equal("Build session monitoring application", snapshot!.SessionName);
    }

    [Fact]
    public async Task StatusLine_WithoutSessionName_SessionNameIsNull_NoThrow()
    {
        string sessionId = $"session-{Guid.NewGuid():N}";

        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
        });

        var response = await _client!.PostAsync(
            "/events/status-line",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        bool found = _server!.State.TryGetSession(sessionId, out var snapshot);
        Assert.True(found);
        Assert.Null(snapshot!.SessionName);
    }

    [Fact]
    public async Task SubagentStatusLine_WithTopLevelSessionId_SetsParentSessionIdOnAgent()
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

        var response = await _client!.PostAsync(
            "/events/subagent-status-line",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        bool found = _server!.State.TryGetAgent(agentId, out var record);
        Assert.True(found);
        Assert.Equal(parentSessionId, record!.ParentSessionId);
        Assert.Equal("Audit project-ui.md", record.Name);
    }

    [Fact]
    public async Task SubagentStatusLine_SecondTickMissingSessionId_PreservesPreviousParentSessionId()
    {
        string agentId = $"agent-{Guid.NewGuid():N}";
        string parentSessionId = $"session-{Guid.NewGuid():N}";

        string firstPayload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = parentSessionId,
            ["tasks"] = new[]
            {
                new Dictionary<string, object?> { ["id"] = agentId, ["type"] = "general-purpose" },
            },
        });
        var firstResponse = await _client!.PostAsync(
            "/events/subagent-status-line",
            new StringContent(firstPayload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);

        // Second tick has no top-level session_id at all: must not overwrite the known parent.
        string secondPayload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["tasks"] = new[]
            {
                new Dictionary<string, object?> { ["id"] = agentId, ["type"] = "general-purpose" },
            },
        });
        var secondResponse = await _client!.PostAsync(
            "/events/subagent-status-line",
            new StringContent(secondPayload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, secondResponse.StatusCode);

        bool found = _server!.State.TryGetAgent(agentId, out var record);
        Assert.True(found);
        Assert.Equal(parentSessionId, record!.ParentSessionId);
    }

    [Fact]
    public async Task SubagentStatusLine_TaskWithoutName_AgentNameIsNull_NoThrow()
    {
        string agentId = $"agent-{Guid.NewGuid():N}";

        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = $"session-{Guid.NewGuid():N}",
            ["tasks"] = new[]
            {
                new Dictionary<string, object?> { ["id"] = agentId, ["type"] = "general-purpose" },
            },
        });

        var response = await _client!.PostAsync(
            "/events/subagent-status-line",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        bool found = _server!.State.TryGetAgent(agentId, out var record);
        Assert.True(found);
        Assert.Null(record!.Name);
    }

    [Fact]
    public async Task SubagentStop_MissingAgentTranscriptPath_StillEndsAgent_NoThrow()
    {
        string agentId = $"agent-{Guid.NewGuid():N}";

        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "SubagentStop",
            ["session_id"] = "parent-session-2",
            ["agent_id"] = agentId,
        });

        var response = await _client!.PostAsync(
            "/events/subagent-stop",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        bool found = _server!.State.TryGetAgent(agentId, out var record);
        Assert.True(found);
        Assert.Equal(AgentStatus.Ended, record!.Status);
        Assert.Null(record.ModelId);
    }
}

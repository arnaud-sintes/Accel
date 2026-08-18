using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Accel.Metrics;
using Xunit;

namespace Accel.Tests;

/// <summary>
/// Unit tests for the tier-1 agent-start resolution machinery added to
/// <see cref="RootsTreeBuilder"/> by claude-agentgraph.md section 6.3/6.6: the convention
/// subagents path (<c>&lt;projectsDir&gt;\&lt;ProjectDir&gt;\&lt;SessionId&gt;\subagents\agent-&lt;AgentId&gt;.jsonl</c>),
/// its precedence rules against <see cref="AgentRecord.TranscriptPath"/>, and the
/// <c>_agentStartCache</c> retry-floor-on-miss behaviour (<see cref="RootsTreeBuilder.AgentStartCacheCount"/>).
/// Drives <see cref="RootsTreeBuilder"/> directly against a fixture <c>projectsDirOverride</c>
/// tree, in the style of <c>RootsTreeRouteTests</c>' fixture writers - no HTTP involved.
/// </summary>
public class RootsTreeBuilderStartTimeTests : IDisposable
{
    private readonly string _fixtureRoot = Path.Combine(Path.GetTempPath(), $"accel-agent-start-{Guid.NewGuid():N}");

    public RootsTreeBuilderStartTimeTests()
    {
        Directory.CreateDirectory(_fixtureRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_fixtureRoot, recursive: true); } catch { /* best effort cleanup */ }
    }

    private static string UserLine(string text, string cwd, string? timestamp = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["cwd"] = cwd,
            ["message"] = new Dictionary<string, object?> { ["content"] = text },
        };
        if (timestamp is not null)
        {
            payload["timestamp"] = timestamp;
        }

        return JsonSerializer.Serialize(payload);
    }

    private string WriteSessionFile(string slug, string sessionId, IEnumerable<string> lines)
    {
        string slugDir = Path.Combine(_fixtureRoot, slug);
        Directory.CreateDirectory(slugDir);
        string path = Path.Combine(slugDir, $"{sessionId}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private string WriteAgentTranscript(string slug, string sessionId, string agentId, IEnumerable<string> lines)
    {
        string subagentsDir = Path.Combine(_fixtureRoot, slug, sessionId, "subagents");
        Directory.CreateDirectory(subagentsDir);
        string path = Path.Combine(subagentsDir, $"agent-{agentId}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void AgentStart_ResolvedFromConventionSubagentsPath()
    {
        string sessionId = $"session-{Guid.NewGuid():N}";
        string agentId = $"agent-{Guid.NewGuid():N}";

        WriteSessionFile("C--projects", sessionId, new[] { UserLine("hi", @"C:\projects") });
        WriteAgentTranscript("C--projects", sessionId, agentId, new[]
        {
            UserLine("go", @"C:\projects", timestamp: "2026-08-13T10:00:00Z"),
        });

        var state = new SessionState();
        state.UpdateSessionSnapshot(new SessionSnapshot(
            SessionId: sessionId, ModelId: "claude-opus-5", ModelDisplayName: "Opus", EffortLevel: "high",
            ContextWindowSize: 200_000, UsedTokens: 1000, UsedPercentage: 0.5, RemainingPercentage: 99.5,
            CostUsd: null, PayloadVersion: null, ReceivedAtUtc: DateTime.UtcNow));
        state.UpdateAgentRecord(new AgentRecord(
            AgentId: agentId, AgentType: "general-purpose", ParentSessionId: sessionId,
            ModelId: "claude-sonnet-5", EffortLevel: "medium", InputTokens: 10, OutputTokens: 5,
            CacheCreationInputTokens: 0, CacheReadInputTokens: 0, ContextWindowSize: 200_000,
            Status: AgentStatus.Live, ReceivedAtUtc: DateTime.UtcNow, Source: "subagentStatusLine"));

        var builder = new RootsTreeBuilder();
        var result = builder.Build(new[] { @"C:\projects" }, state, _fixtureRoot);

        var session = result.Roots.Single(r => r.Path == @"C:\projects").Sessions.Single(s => s.SessionId == sessionId);
        var agent = session.Agents.Single(a => a.AgentId == agentId);

        Assert.Equal(new DateTime(2026, 8, 13, 10, 0, 0, DateTimeKind.Utc), agent.StartedAtUtc);
        Assert.Equal("transcript", agent.StartedAtSource);
        Assert.Equal(1, builder.AgentStartCacheCount);
    }

    [Fact]
    public void AgentStart_PrefersRecordTranscriptPathOverConventionPath()
    {
        string sessionId = $"session-{Guid.NewGuid():N}";
        string agentId = $"agent-{Guid.NewGuid():N}";

        WriteSessionFile("C--projects", sessionId, new[] { UserLine("hi", @"C:\projects") });

        // The convention path has a LATER timestamp than the explicit TranscriptPath, so if the
        // convention path won incorrectly, this test would observe the wrong (later) value.
        WriteAgentTranscript("C--projects", sessionId, agentId, new[]
        {
            UserLine("wrong path content", @"C:\projects", timestamp: "2026-08-13T12:00:00Z"),
        });

        string explicitPath = Path.Combine(_fixtureRoot, "explicit-agent-transcript.jsonl");
        File.WriteAllLines(explicitPath, new[]
        {
            UserLine("real path content", @"C:\projects", timestamp: "2026-08-13T09:00:00Z"),
        });

        var state = new SessionState();
        state.UpdateSessionSnapshot(new SessionSnapshot(
            SessionId: sessionId, ModelId: "claude-opus-5", ModelDisplayName: "Opus", EffortLevel: "high",
            ContextWindowSize: 200_000, UsedTokens: 1000, UsedPercentage: 0.5, RemainingPercentage: 99.5,
            CostUsd: null, PayloadVersion: null, ReceivedAtUtc: DateTime.UtcNow));
        state.UpdateAgentRecord(new AgentRecord(
            AgentId: agentId, AgentType: "general-purpose", ParentSessionId: sessionId,
            ModelId: "claude-sonnet-5", EffortLevel: "medium", InputTokens: 10, OutputTokens: 5,
            CacheCreationInputTokens: 0, CacheReadInputTokens: 0, ContextWindowSize: 200_000,
            Status: AgentStatus.Live, ReceivedAtUtc: DateTime.UtcNow, Source: "transcript",
            TranscriptPath: explicitPath));

        var builder = new RootsTreeBuilder();
        var result = builder.Build(new[] { @"C:\projects" }, state, _fixtureRoot);

        var session = result.Roots.Single(r => r.Path == @"C:\projects").Sessions.Single(s => s.SessionId == sessionId);
        var agent = session.Agents.Single(a => a.AgentId == agentId);

        Assert.Equal(new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc), agent.StartedAtUtc);
        Assert.Equal("transcript", agent.StartedAtSource);
    }

    [Fact]
    public void AgentStart_MissIsRetriedNoMoreThanOncePerTenSeconds()
    {
        string sessionId = $"session-{Guid.NewGuid():N}";
        string agentId = $"agent-{Guid.NewGuid():N}";

        WriteSessionFile("C--projects", sessionId, new[] { UserLine("hi", @"C:\projects") });
        // No agent transcript file at all yet - simulates the race where the agent appears in
        // tasks[] before its transcript is written.

        var state = new SessionState();
        state.UpdateSessionSnapshot(new SessionSnapshot(
            SessionId: sessionId, ModelId: "claude-opus-5", ModelDisplayName: "Opus", EffortLevel: "high",
            ContextWindowSize: 200_000, UsedTokens: 1000, UsedPercentage: 0.5, RemainingPercentage: 99.5,
            CostUsd: null, PayloadVersion: null, ReceivedAtUtc: DateTime.UtcNow));
        state.UpdateAgentRecord(new AgentRecord(
            AgentId: agentId, AgentType: "general-purpose", ParentSessionId: sessionId,
            ModelId: "claude-sonnet-5", EffortLevel: "medium", InputTokens: 10, OutputTokens: 5,
            CacheCreationInputTokens: 0, CacheReadInputTokens: 0, ContextWindowSize: 200_000,
            Status: AgentStatus.Live, ReceivedAtUtc: DateTime.UtcNow, Source: "subagentStatusLine"));

        var builder = new RootsTreeBuilder();
        var first = builder.Build(new[] { @"C:\projects" }, state, _fixtureRoot);
        var firstAgent = first.Roots.Single(r => r.Path == @"C:\projects").Sessions.Single(s => s.SessionId == sessionId)
            .Agents.Single(a => a.AgentId == agentId);

        // Tier 1 missed (no file); falls back to tier 3 (first_seen) from the record itself.
        Assert.Equal("first_seen", firstAgent.StartedAtSource);
        Assert.Equal(1, builder.AgentStartCacheCount);

        // Now create the transcript file - but immediately rebuilding must NOT pick it up yet,
        // because the miss is only retried once per 10 seconds.
        WriteAgentTranscript("C--projects", sessionId, agentId, new[]
        {
            UserLine("go", @"C:\projects", timestamp: "2026-08-13T10:00:00Z"),
        });

        var second = builder.Build(new[] { @"C:\projects" }, state, _fixtureRoot);
        var secondAgent = second.Roots.Single(r => r.Path == @"C:\projects").Sessions.Single(s => s.SessionId == sessionId)
            .Agents.Single(a => a.AgentId == agentId);

        Assert.Equal("first_seen", secondAgent.StartedAtSource);
        Assert.Equal(1, builder.AgentStartCacheCount); // still exactly one cache entry, not re-grown
    }

    [Fact]
    public void LiveSession_WithRecordedToolUsage_PopulatesSortedMcpAndSkillUsageArrays()
    {
        string sessionId = $"session-{Guid.NewGuid():N}";

        WriteSessionFile("C--projects", sessionId, new[] { UserLine("hi", @"C:\projects") });

        var state = new SessionState();
        state.UpdateSessionSnapshot(new SessionSnapshot(
            SessionId: sessionId, ModelId: "claude-opus-5", ModelDisplayName: "Opus", EffortLevel: "high",
            ContextWindowSize: 200_000, UsedTokens: 1000, UsedPercentage: 0.5, RemainingPercentage: 99.5,
            CostUsd: null, PayloadVersion: null, ReceivedAtUtc: DateTime.UtcNow));

        state.IncrementToolUsage(sessionId, ToolUsageKind.Mcp, "serena__find_symbol");
        state.IncrementToolUsage(sessionId, ToolUsageKind.Mcp, "serena__find_symbol");
        state.IncrementToolUsage(sessionId, ToolUsageKind.Mcp, "jira__jira_search");
        state.IncrementToolUsage(sessionId, ToolUsageKind.Skill, "code-review");

        var builder = new RootsTreeBuilder();
        var result = builder.Build(new[] { @"C:\projects" }, state, _fixtureRoot);

        var session = result.Roots.Single(r => r.Path == @"C:\projects").Sessions.Single(s => s.SessionId == sessionId);

        Assert.Equal(
            new[] { new ToolHitCountDto("serena__find_symbol", 2), new ToolHitCountDto("jira__jira_search", 1) },
            session.McpUsage);
        Assert.Equal(new[] { new ToolHitCountDto("code-review", 1) }, session.SkillUsage);
    }

    [Fact]
    public void HistoricalSession_WithNoLiveState_GetsEmptyMcpAndSkillUsageArrays()
    {
        string sessionId = $"session-{Guid.NewGuid():N}";

        WriteSessionFile("C--projects", sessionId, new[] { UserLine("hi", @"C:\projects") });

        var state = new SessionState();

        var builder = new RootsTreeBuilder();
        var result = builder.Build(new[] { @"C:\projects" }, state, _fixtureRoot);

        var session = result.Roots.Single(r => r.Path == @"C:\projects").Sessions.Single(s => s.SessionId == sessionId);

        Assert.False(session.IsLive);
        Assert.Empty(session.McpUsage!);
        Assert.Empty(session.SkillUsage!);
    }

    [Fact]
    public void AgentStart_UnattributedAgentWithNoSessionDir_DegradesToNull()
    {
        string agentId = $"agent-{Guid.NewGuid():N}";

        var state = new SessionState();
        // An agent with a parent_session_id that never actually appears as a session on disk -
        // it lands in UnattributedAgents, with no session directory to derive a convention path
        // from at all (subagentsDir is passed as null for those - section 6.3).
        state.UpdateAgentRecord(new AgentRecord(
            AgentId: agentId, AgentType: "general-purpose", ParentSessionId: "session-that-does-not-exist",
            ModelId: "claude-sonnet-5", EffortLevel: "medium", InputTokens: 10, OutputTokens: 5,
            CacheCreationInputTokens: 0, CacheReadInputTokens: 0, ContextWindowSize: 200_000,
            Status: AgentStatus.Live, ReceivedAtUtc: DateTime.UtcNow, Source: "subagentStatusLine"));

        var builder = new RootsTreeBuilder();
        var result = builder.Build(new[] { @"C:\projects" }, state, _fixtureRoot);

        var agent = result.UnattributedAgents.Single(a => a.AgentId == agentId);

        // No tier-1 hit possible; falls through to tier 3 (first_seen) from the record.
        Assert.Equal("first_seen", agent.StartedAtSource);
        Assert.NotNull(agent.StartedAtUtc);
    }
}

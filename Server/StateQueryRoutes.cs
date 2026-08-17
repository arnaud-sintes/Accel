using System.Text.Json.Serialization;
using Accel.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Accel.Server;

/// <summary>
/// Phase 3d: read-only GET routes exposing the Phase 3b-ii <see cref="SessionState"/>
/// in-memory map over the same loopback-only server used for the Phase 3 event routes.
///
/// Nothing here is persisted, and nothing here mutates <see cref="SessionState"/> except the
/// small liveness reconciliation triggered from the subagent-status-line route (see
/// <see cref="MetricsPipeline.HandleSubagentStatusLine"/>, wired from
/// <see cref="EventServer"/>). Every handler is fully tolerant of an empty/partial store and
/// must never throw - a missing/malformed field renders as JSON <c>null</c>, never a 500.
/// </summary>
public static class StateQueryRoutes
{
    public static void Map(WebApplication app, SessionState state)
    {
        app.MapGet("/sessions", () => Results.Json(GetAllSessionDtos(state)));

        app.MapGet("/sessions/{sessionId}", (string sessionId) =>
        {
            if (!state.TryGetSession(sessionId, out var snapshot) || snapshot is null)
            {
                return Results.NotFound(new { error = "not found" });
            }

            var agents = state.GetAllAgents()
                .Where(a => string.Equals(a.ParentSessionId, sessionId, StringComparison.Ordinal))
                .Select(ToDto)
                .ToArray();

            return Results.Json(new SessionWithAgentsDto(ToDto(snapshot), agents));
        });

        app.MapGet("/agents", () => Results.Json(GetAllAgentDtos(state)));

        app.MapGet("/state", () => Results.Json(new StateDto(GetAllSessionDtos(state), GetAllAgentDtos(state))));
    }

    private static SessionDto[] GetAllSessionDtos(SessionState state) =>
        state.GetAllSessions().Select(ToDto).ToArray();

    private static AgentDto[] GetAllAgentDtos(SessionState state) =>
        state.GetAllAgents().Select(ToDto).ToArray();

    private static SessionDto ToDto(SessionSnapshot snapshot) => new(
        SessionId: snapshot.SessionId,
        ModelId: snapshot.ModelId,
        ModelDisplayName: snapshot.ModelDisplayName,
        EffortLevel: snapshot.EffortLevel,
        ContextWindowSize: snapshot.ContextWindowSize,
        UsedTokens: snapshot.UsedTokens,
        UsedPercentage: snapshot.UsedPercentage,
        RemainingPercentage: snapshot.RemainingPercentage,
        CostUsd: snapshot.CostUsd,
        Version: snapshot.PayloadVersion,
        Source: snapshot.Source,
        AsOf: snapshot.ReceivedAtUtc,
        Status: snapshot.Ended ? "ended" : "live",
        SessionName: snapshot.SessionName);

    private static AgentDto ToDto(AgentRecord record) => new(
        AgentId: record.AgentId,
        AgentType: record.AgentType,
        SessionId: record.ParentSessionId,
        ModelId: record.ModelId,
        EffortLevel: record.EffortLevel,
        InputTokens: record.InputTokens,
        OutputTokens: record.OutputTokens,
        CacheCreationInputTokens: record.CacheCreationInputTokens,
        CacheReadInputTokens: record.CacheReadInputTokens,
        ContextWindowSize: record.ContextWindowSize,
        Status: StatusToString(record.Status),
        Source: record.Source,
        AsOf: record.ReceivedAtUtc,
        Name: record.Name);

    private static string StatusToString(AgentStatus status) => status switch
    {
        AgentStatus.Live => "live",
        AgentStatus.Ended => "ended",
        AgentStatus.Stale => "stale",
        _ => "live",
    };

    /// <summary>One row of <c>GET /sessions</c> / the <c>session</c> half of <c>GET /sessions/{id}</c>.</summary>
    public sealed record SessionDto(
        [property: JsonPropertyName("session_id")] string SessionId,
        [property: JsonPropertyName("model_id")] string? ModelId,
        [property: JsonPropertyName("model_display_name")] string? ModelDisplayName,
        [property: JsonPropertyName("effort_level")] string? EffortLevel,
        [property: JsonPropertyName("context_window_size")] long? ContextWindowSize,
        [property: JsonPropertyName("used_tokens")] long? UsedTokens,
        [property: JsonPropertyName("used_percentage")] double? UsedPercentage,
        [property: JsonPropertyName("remaining_percentage")] double? RemainingPercentage,
        [property: JsonPropertyName("cost_usd")] decimal? CostUsd,
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("as_of")] DateTime AsOf,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("session_name")] string? SessionName = null);

    /// <summary>One row of <c>GET /agents</c>.</summary>
    public sealed record AgentDto(
        [property: JsonPropertyName("agent_id")] string AgentId,
        [property: JsonPropertyName("agent_type")] string? AgentType,
        [property: JsonPropertyName("session_id")] string? SessionId,
        [property: JsonPropertyName("model_id")] string? ModelId,
        [property: JsonPropertyName("effort_level")] string? EffortLevel,
        [property: JsonPropertyName("input_tokens")] int InputTokens,
        [property: JsonPropertyName("output_tokens")] int OutputTokens,
        [property: JsonPropertyName("cache_creation_input_tokens")] int CacheCreationInputTokens,
        [property: JsonPropertyName("cache_read_input_tokens")] int CacheReadInputTokens,
        [property: JsonPropertyName("context_window_size")] int ContextWindowSize,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("as_of")] DateTime AsOf,
        [property: JsonPropertyName("name")] string? Name = null);

    /// <summary><c>GET /sessions/{session_id}</c>'s body: the session plus its agents.</summary>
    public sealed record SessionWithAgentsDto(
        [property: JsonPropertyName("session")] SessionDto Session,
        [property: JsonPropertyName("agents")] AgentDto[] Agents);

    /// <summary><c>GET /state</c>'s body: everything, one document.</summary>
    public sealed record StateDto(
        [property: JsonPropertyName("sessions")] SessionDto[] Sessions,
        [property: JsonPropertyName("agents")] AgentDto[] Agents);
}

using System.Text.Json;

namespace Glaude.Metrics;

/// <summary>
/// Wires the Phase 3b-ii metrics building blocks (<see cref="TranscriptReader"/>,
/// <see cref="MetaJsonReader"/>, <see cref="ModelWindowTable"/>) into the Phase 3
/// event-server payloads, writing results into a shared <see cref="SessionState"/>.
///
/// Both entry points are best-effort and must never throw: they run after the existing
/// Phase 3 printing/throttling on the request path, and a metrics-pipeline failure must
/// never turn an event POST into anything other than the existing 204 response.
/// </summary>
public static class MetricsPipeline
{
    /// <summary>
    /// Handles a SubagentStop payload: if it carries an <c>agent_transcript_path</c>, tails
    /// that transcript for the last assistant entry, reads the sibling ".meta.json", and
    /// records the result in <paramref name="state"/> as an Ended agent record. Per
    /// project.md's resolved path-derivation caveat, <c>transcript_path</c> on this payload
    /// is the PARENT session's transcript, not the subagent's, and must not be used here.
    /// </summary>
    public static void HandleSubagentStop(string rawBody, SessionState state)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            string? agentId = GetString(root, "agent_id");
            if (string.IsNullOrEmpty(agentId))
            {
                // Without an agent_id there is nothing to key the record on.
                return;
            }

            string? agentType = GetString(root, "agent_type");
            string? parentSessionId = GetString(root, "session_id");
            string? agentTranscriptPath = GetString(root, "agent_transcript_path");
            string? topLevelEffortLevel = GetEffortLevel(root);

            TranscriptAssistantEntry? entry = TranscriptReader.TryReadLastAssistantEntry(agentTranscriptPath);
            SubagentMetaInfo? meta = MetaJsonReader.TryRead(agentTranscriptPath);

            string? modelId = entry?.Model ?? meta?.Model;
            string? effortLevel = entry?.EffortLevel ?? topLevelEffortLevel;
            int windowSize = ModelWindowTable.Resolve(modelId);

            var record = new AgentRecord(
                AgentId: agentId,
                AgentType: meta?.AgentType ?? agentType,
                ParentSessionId: parentSessionId,
                ModelId: modelId,
                EffortLevel: effortLevel,
                InputTokens: entry?.InputTokens ?? 0,
                OutputTokens: entry?.OutputTokens ?? 0,
                CacheCreationInputTokens: entry?.CacheCreationInputTokens ?? 0,
                CacheReadInputTokens: entry?.CacheReadInputTokens ?? 0,
                ContextWindowSize: windowSize,
                Status: AgentStatus.Live,
                ReceivedAtUtc: DateTime.UtcNow,
                Source: "transcript");

            state.UpdateAgentRecord(record);
            state.MarkAgentEnded(agentId);
        }
        catch
        {
            // Best-effort: a malformed SubagentStop body must never break the event route.
        }
    }

    /// <summary>
    /// Handles a status-line payload: extracts the main-session model/effort/context/cost
    /// fields (all optional/nullable per project.md) and records them as the latest
    /// snapshot for that session_id.
    /// </summary>
    public static void HandleStatusLine(string rawBody, SessionState state)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            string? sessionId = GetString(root, "session_id");
            if (string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            string? modelId = null;
            string? modelDisplayName = null;
            if (root.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.Object)
            {
                modelId = GetString(modelProp, "id");
                modelDisplayName = GetString(modelProp, "display_name");
            }

            string? effortLevel = GetEffortLevel(root);

            long? contextWindowSize = null;
            long? usedTokens = null;
            double? usedPercentage = null;
            double? remainingPercentage = null;

            if (root.TryGetProperty("context_window", out var contextWindow) && contextWindow.ValueKind == JsonValueKind.Object)
            {
                contextWindowSize = GetLongOrNull(contextWindow, "context_window_size");
                usedPercentage = GetDoubleOrNull(contextWindow, "used_percentage");
                remainingPercentage = GetDoubleOrNull(contextWindow, "remaining_percentage");

                if (contextWindow.TryGetProperty("current_usage", out var currentUsage)
                    && currentUsage.ValueKind == JsonValueKind.Object)
                {
                    long? input = GetLongOrNull(currentUsage, "input_tokens");
                    long? cacheCreation = GetLongOrNull(currentUsage, "cache_creation_input_tokens");
                    long? cacheRead = GetLongOrNull(currentUsage, "cache_read_input_tokens");

                    if (input is not null || cacheCreation is not null || cacheRead is not null)
                    {
                        usedTokens = (input ?? 0) + (cacheCreation ?? 0) + (cacheRead ?? 0);
                    }
                }
            }

            decimal? costUsd = null;
            if (root.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object)
            {
                costUsd = GetDecimalOrNull(cost, "total_cost_usd");
            }

            string? version = GetString(root, "version");
            string? sessionName = GetString(root, "session_name");

            var snapshot = new SessionSnapshot(
                SessionId: sessionId,
                ModelId: modelId,
                ModelDisplayName: modelDisplayName,
                EffortLevel: effortLevel,
                ContextWindowSize: contextWindowSize,
                UsedTokens: usedTokens,
                UsedPercentage: usedPercentage,
                RemainingPercentage: remainingPercentage,
                CostUsd: costUsd,
                PayloadVersion: version,
                ReceivedAtUtc: DateTime.UtcNow,
                SessionName: sessionName);

            state.UpdateSessionSnapshot(snapshot);
        }
        catch
        {
            // Best-effort: a malformed status-line body must never break the event route.
        }
    }

    /// <summary>
    /// Handles a <c>subagentStatusLine</c> payload for Phase 3d: for every task in the
    /// payload's <c>tasks</c> array, upserts a Live agent record (covers the case where a
    /// task appears before any SubagentStart/SubagentStop was ever observed - e.g. Glaude
    /// started after the subagent did), then reconciles the store so any agent previously
    /// marked Live that is no longer present in this payload's tasks transitions to Stale
    /// (see <see cref="SessionState.ReconcileLiveAgents"/>). Never touches an agent already
    /// Ended - a subagent that finished must not be resurrected to Live by a stale/overlapping
    /// status-line tick.
    /// </summary>
    public static void HandleSubagentStatusLine(string rawBody, SessionState state)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tasks", out var tasksProp)
                || tasksProp.ValueKind != JsonValueKind.Array)
            {
                // No tasks array at all: nothing to upsert, and nothing to reconcile against
                // (an empty/absent tasks array on a version that doesn't send one yet must
                // not be interpreted as "every live agent just vanished").
                return;
            }

            // The subagentStatusLine payload carries the base hook fields including a
            // top-level session_id - the PARENT session's id, same field present on every
            // other hook event (SubagentStart/SubagentStop). Use it as ParentSessionId for
            // every task in this payload; fall back to the existing value only when this
            // payload itself doesn't carry one, so a known parent is never overwritten with
            // null (GAP B).
            string? topLevelSessionId = GetString(root, "session_id");

            var visibleAgentIds = new HashSet<string>();

            foreach (var task in tasksProp.EnumerateArray())
            {
                try
                {
                    if (task.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string? agentId = GetString(task, "id");
                    if (string.IsNullOrEmpty(agentId))
                    {
                        continue;
                    }

                    visibleAgentIds.Add(agentId);

                    if (state.TryGetAgent(agentId, out var existing) && existing is not null
                        && existing.Status == AgentStatus.Ended)
                    {
                        // Already finished via SubagentStop - do not resurrect to Live.
                        continue;
                    }

                    string? agentType = GetString(task, "type") ?? existing?.AgentType;
                    string? agentName = GetString(task, "name") ?? existing?.Name;
                    string? modelId = GetTaskModelId(task) ?? existing?.ModelId;
                    string? effortLevel = GetTaskEffort(task) ?? existing?.EffortLevel;
                    int contextWindowSize = GetTaskInt(task, "contextWindowSize") ?? existing?.ContextWindowSize
                        ?? ModelWindowTable.Resolve(modelId);
                    int tokenCount = GetTaskInt(task, "tokenCount") ?? 0;
                    string? parentSessionId = topLevelSessionId ?? existing?.ParentSessionId;

                    var record = new AgentRecord(
                        AgentId: agentId,
                        AgentType: agentType,
                        ParentSessionId: parentSessionId,
                        ModelId: modelId,
                        EffortLevel: effortLevel,
                        InputTokens: tokenCount,
                        OutputTokens: existing?.OutputTokens ?? 0,
                        CacheCreationInputTokens: existing?.CacheCreationInputTokens ?? 0,
                        CacheReadInputTokens: existing?.CacheReadInputTokens ?? 0,
                        ContextWindowSize: contextWindowSize,
                        Status: AgentStatus.Live,
                        ReceivedAtUtc: DateTime.UtcNow,
                        Source: "subagentStatusLine",
                        Name: agentName);

                    state.UpdateAgentRecord(record);
                }
                catch
                {
                    // A single malformed task entry must never abort the rest of the batch.
                }
            }

            state.ReconcileLiveAgents(visibleAgentIds);
        }
        catch
        {
            // Best-effort: a malformed subagentStatusLine body must never break the event route.
        }
    }

    // model can show up as either a "model" object with an "id", or a bare "model" string,
    // per project.md's tolerant-shape notes for this payload.
    private static string? GetTaskModelId(JsonElement task)
    {
        if (task.ValueKind != JsonValueKind.Object || !task.TryGetProperty("model", out var model))
        {
            return null;
        }

        return model.ValueKind switch
        {
            JsonValueKind.String => model.GetString(),
            JsonValueKind.Object when model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String => id.GetString(),
            _ => null,
        };
    }

    // effort can be a level string, or a {"level": "..."} object, per the same tolerant-shape
    // notes used elsewhere in this payload (EventPrinter.FormatTask also tolerates both).
    private static string? GetTaskEffort(JsonElement task)
    {
        if (task.ValueKind != JsonValueKind.Object || !task.TryGetProperty("effort", out var effort))
        {
            return null;
        }

        return effort.ValueKind switch
        {
            JsonValueKind.String => effort.GetString(),
            JsonValueKind.Object when effort.TryGetProperty("level", out var level) && level.ValueKind == JsonValueKind.String => level.GetString(),
            _ => null,
        };
    }

    private static int? GetTaskInt(JsonElement task, string propertyName)
    {
        if (task.ValueKind != JsonValueKind.Object || !task.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int value))
        {
            return value;
        }

        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out int parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? GetEffortLevel(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("effort", out var effort)
            && effort.ValueKind == JsonValueKind.Object
            && effort.TryGetProperty("level", out var level)
            && level.ValueKind == JsonValueKind.String)
        {
            return level.GetString();
        }

        return null;
    }

    private static string? GetString(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }

        return null;
    }

    private static long? GetLongOrNull(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt64(out long value))
        {
            return value;
        }

        return null;
    }

    private static double? GetDoubleOrNull(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetDouble(out double value))
        {
            return value;
        }

        return null;
    }

    private static decimal? GetDecimalOrNull(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetDecimal(out decimal value))
        {
            return value;
        }

        return null;
    }
}

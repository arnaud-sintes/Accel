using System.Collections.Concurrent;

namespace Accel.Metrics;

/// <summary>
/// Liveness of a subagent record. "Live" means the record was populated by a source that
/// implies the subagent is still running (e.g. subagentStatusLine, a later phase); "Ended"
/// means a SubagentStop was observed and the transcript tail was read; "Stale" is reserved
/// for a later eviction phase (Phase 3d) that hasn't landed yet.
/// </summary>
public enum AgentStatus
{
    Live,
    Ended,
    Stale,
}

/// <summary>Which per-session tool-usage counter a <see cref="SessionState.IncrementToolUsage"/>
/// call targets - MCP tool calls (<c>mcp__*</c>) vs. Skill invocations (<c>tool_name == "Skill"</c>).</summary>
public enum ToolUsageKind
{
    Mcp,
    Skill,
}

/// <summary>
/// Point-in-time snapshot of a session's MCP/Skill hit counts, keyed by display name -&gt;
/// hit count. Returned by <see cref="SessionState.GetToolUsage"/>; empty dictionaries (never
/// null) when the session is unknown - matches this file's other tolerant readers.
/// </summary>
public sealed record ToolUsageSnapshot(
    IReadOnlyDictionary<string, int> McpHits,
    IReadOnlyDictionary<string, int> SkillHits);

/// <summary>
/// A snapshot of a main session's model/effort/context/cost metrics, as reported by one
/// <c>/events/status-line</c> POST. Per project.md, every snapshot is stamped with its
/// receipt time and the payload's own <c>version</c> field and must be rendered "as of T",
/// not "current" - statusLine updates are not a timer and can go quiet for a long time.
/// </summary>
public sealed record SessionSnapshot(
    string SessionId,
    string? ModelId,
    string? ModelDisplayName,
    string? EffortLevel,
    long? ContextWindowSize,
    long? UsedTokens,
    double? UsedPercentage,
    double? RemainingPercentage,
    decimal? CostUsd,
    string? PayloadVersion,
    DateTime ReceivedAtUtc,
    string Source = "statusLine",
    bool Ended = false,
    string? SessionName = null);

/// <summary>
/// A record for one subagent, keyed by <c>agent_id</c>. <see cref="Source"/> tags where the
/// data came from ("transcript" for a SubagentStop-triggered tail-read, or
/// "subagentStatusLine" for a later phase's live feed) so callers can judge freshness.
/// </summary>
public sealed record AgentRecord(
    string AgentId,
    string? AgentType,
    string? ParentSessionId,
    string? ModelId,
    string? EffortLevel,
    int InputTokens,
    int OutputTokens,
    int CacheCreationInputTokens,
    int CacheReadInputTokens,
    int ContextWindowSize,
    AgentStatus Status,
    DateTime ReceivedAtUtc,
    string Source,
    string? Name = null,
    // Section 6.1 of claude-agentgraph.md: "first writer wins, earliest wins" - see
    // SessionState.UpdateAgentRecord's merge for the invariant that protects these three once
    // set. StartedAtSource records which tier of the three-tier ladder (section 6.3) produced
    // StartedAtUtc: "transcript" | "task_start_time" | "first_seen".
    DateTime? StartedAtUtc = null,
    string? StartedAtSource = null,
    string? TranscriptPath = null);

/// <summary>
/// In-memory, non-persisted, thread-safe store of the "current state" map described in
/// project.md's "Out of scope (v1) - Clarification": one row per session, one row per
/// subagent, overwritten on each new snapshot, nothing ever written to disk, empty again on
/// process restart. This class only owns the store and the write/read paths into it - a
/// later phase (3d) exposes it over HTTP.
///
/// Must be constructed once per running server (see <c>EventServer.State</c>) and shared by
/// every route handler that touches it - never re-created per request.
/// </summary>
public sealed class SessionState
{
    private readonly ConcurrentDictionary<string, SessionSnapshot> _sessions = new();
    private readonly ConcurrentDictionary<string, AgentRecord> _agents = new();

    // PostToolUse hit-count tracking: sessionId -> toolName -> count, one dictionary per
    // ToolUsageKind. Never persisted, same lifetime/contract as _sessions/_agents above.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _mcpHits = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _skillHits = new();

    /// <summary>
    /// Raised at the end of every mutating method below, whenever this store's data actually
    /// changed - the primary "back-end changed" push signal the WinForms monitor window
    /// subscribes to directly (in-process, no HTTP) instead of polling on a timer. May be raised
    /// from whatever thread called the mutator (typically a Kestrel request-handling thread), so
    /// subscribers must marshal back to their own thread (e.g. a WinForms
    /// <c>Control.BeginInvoke</c>) before touching any UI.
    /// </summary>
    public event Action? Changed;

    private void RaiseChanged() => Changed?.Invoke();

    /// <summary>Overwrites (or inserts) the latest snapshot for a main session.</summary>
    public void UpdateSessionSnapshot(SessionSnapshot snapshot)
    {
        if (snapshot is null || string.IsNullOrEmpty(snapshot.SessionId))
        {
            return;
        }

        _sessions[snapshot.SessionId] = snapshot;
        RaiseChanged();
    }

    /// <summary>
    /// Overwrites (or inserts) the latest record for a subagent. Every field is replaced
    /// wholesale from <paramref name="record"/> EXCEPT three - <c>StartedAtUtc</c>,
    /// <c>StartedAtSource</c>, and <c>TranscriptPath</c> - which are merged rather than
    /// replaced, per section 6.1 of claude-agentgraph.md:
    ///
    /// <list type="bullet">
    /// <item><c>StartedAtUtc</c>/<c>StartedAtSource</c> are "first writer wins, earliest wins":
    /// a later update carrying <c>null</c> (e.g. a <c>subagentStatusLine</c> tick with no
    /// tier-1/tier-2 hit) must never erase a previously-known start time, and a later update
    /// carrying a LATER timestamp must never push an earlier one forward. If this is the very
    /// first record ever seen for this <c>agent_id</c> and it carries no start time of its own
    /// (tiers 1/2 both missed), it falls back to tier 3 - its own <c>ReceivedAtUtc</c>, tagged
    /// <c>"first_seen"</c> - so every agent record has SOME start time once it exists at all.</item>
    /// <item><c>TranscriptPath</c> is "known value never overwritten with null" - the same rule
    /// already applied to <c>ParentSessionId</c> in <c>MetricsPipeline.HandleSubagentStatusLine</c>.</item>
    /// </list>
    /// </summary>
    public void UpdateAgentRecord(AgentRecord record)
    {
        if (record is null || string.IsNullOrEmpty(record.AgentId))
        {
            return;
        }

        _agents.AddOrUpdate(
            record.AgentId,
            _ => record.StartedAtUtc is null
                ? record with { StartedAtUtc = record.ReceivedAtUtc, StartedAtSource = "first_seen" }
                : record,
            (_, existing) =>
            {
                var (startedAtUtc, startedAtSource) = EarliestStartedAt(
                    existing.StartedAtUtc, existing.StartedAtSource,
                    record.StartedAtUtc, record.StartedAtSource);

                return record with
                {
                    StartedAtUtc = startedAtUtc,
                    StartedAtSource = startedAtSource,
                    TranscriptPath = record.TranscriptPath ?? existing.TranscriptPath,
                };
            });

        RaiseChanged();
    }

    /// <summary>"Earliest wins" merge for the pair of (StartedAtUtc, StartedAtSource) fields - a
    /// null side never wins over a non-null side, and between two non-null sides the earlier
    /// timestamp (and its matching source tag) wins.</summary>
    private static (DateTime? StartedAtUtc, string? StartedAtSource) EarliestStartedAt(
        DateTime? existingUtc, string? existingSource, DateTime? incomingUtc, string? incomingSource)
    {
        if (existingUtc is null)
        {
            return (incomingUtc, incomingSource);
        }

        if (incomingUtc is null)
        {
            return (existingUtc, existingSource);
        }

        return incomingUtc.Value < existingUtc.Value
            ? (incomingUtc, incomingSource)
            : (existingUtc, existingSource);
    }

    /// <summary>
    /// Transitions an existing agent record's status to <see cref="AgentStatus.Ended"/>. If
    /// no record exists yet for <paramref name="agentId"/> (e.g. the transcript tail-read
    /// failed to produce anything), inserts a minimal placeholder record already marked
    /// Ended, so a SubagentStop is never silently dropped from the store.
    /// </summary>
    public void MarkAgentEnded(string agentId)
    {
        if (string.IsNullOrEmpty(agentId))
        {
            return;
        }

        _agents.AddOrUpdate(
            agentId,
            _ => new AgentRecord(
                agentId,
                AgentType: null,
                ParentSessionId: null,
                ModelId: null,
                EffortLevel: null,
                InputTokens: 0,
                OutputTokens: 0,
                CacheCreationInputTokens: 0,
                CacheReadInputTokens: 0,
                ContextWindowSize: ModelWindowTable.DefaultWindow,
                Status: AgentStatus.Ended,
                ReceivedAtUtc: DateTime.UtcNow,
                Source: "transcript"),
            (_, existing) => existing with { Status = AgentStatus.Ended, ReceivedAtUtc = DateTime.UtcNow });
        RaiseChanged();
    }

    /// <summary>
    /// Transitions a session's status to ended (Phase 3d eviction: <c>SessionEnd</c> -&gt;
    /// session <c>ended</c>). If no snapshot exists yet for <paramref name="sessionId"/> (a
    /// SessionEnd for a session Accel never saw a status-line for), inserts a minimal
    /// placeholder snapshot already marked ended, mirroring <see cref="MarkAgentEnded"/>'s
    /// "never silently dropped" behavior.
    /// </summary>
    public void MarkSessionEnded(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        _sessions.AddOrUpdate(
            sessionId,
            _ => new SessionSnapshot(
                sessionId,
                ModelId: null,
                ModelDisplayName: null,
                EffortLevel: null,
                ContextWindowSize: null,
                UsedTokens: null,
                UsedPercentage: null,
                RemainingPercentage: null,
                CostUsd: null,
                PayloadVersion: null,
                ReceivedAtUtc: DateTime.UtcNow,
                Ended: true),
            (_, existing) => existing with { Ended = true });
        RaiseChanged();
    }

    /// <summary>
    /// Phase 3d liveness reconciliation for the <c>subagentStatusLine</c> feed: any agent
    /// currently marked <see cref="AgentStatus.Live"/> whose id is NOT present in
    /// <paramref name="currentlyVisibleAgentIds"/> (i.e. it vanished from the live
    /// <c>tasks</c> array without an explicit <c>SubagentStop</c>) transitions to
    /// <see cref="AgentStatus.Stale"/> - never silently dropped from the store. Agents already
    /// <see cref="AgentStatus.Ended"/> or <see cref="AgentStatus.Stale"/> are left untouched.
    /// </summary>
    public void ReconcileLiveAgents(IReadOnlySet<string> currentlyVisibleAgentIds)
    {
        if (currentlyVisibleAgentIds is null)
        {
            return;
        }

        bool changed = false;
        foreach (var pair in _agents)
        {
            AgentRecord record = pair.Value;
            if (record.Status == AgentStatus.Live && !currentlyVisibleAgentIds.Contains(record.AgentId))
            {
                if (_agents.TryUpdate(
                    pair.Key,
                    record with { Status = AgentStatus.Stale, ReceivedAtUtc = DateTime.UtcNow },
                    record))
                {
                    changed = true;
                }
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    /// <summary>Looks up a session snapshot by id. Returns false (not throw) if absent.</summary>
    public bool TryGetSession(string sessionId, out SessionSnapshot? snapshot)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            snapshot = null;
            return false;
        }

        return _sessions.TryGetValue(sessionId, out snapshot);
    }

    /// <summary>Looks up an agent record by id. Returns false (not throw) if absent.</summary>
    public bool TryGetAgent(string agentId, out AgentRecord? record)
    {
        if (string.IsNullOrEmpty(agentId))
        {
            record = null;
            return false;
        }

        return _agents.TryGetValue(agentId, out record);
    }

    /// <summary>Returns a point-in-time snapshot of every known session.</summary>
    public IReadOnlyCollection<SessionSnapshot> GetAllSessions() => _sessions.Values.ToList();

    /// <summary>Returns a point-in-time snapshot of every known agent record.</summary>
    public IReadOnlyCollection<AgentRecord> GetAllAgents() => _agents.Values.ToList();

    /// <summary>
    /// Atomically increments the hit count for one tool <paramref name="name"/> under one
    /// session/kind bucket. No-op (never throws) for an empty <paramref name="sessionId"/> or
    /// <paramref name="name"/>, same guard style as the mutators above.
    /// </summary>
    public void IncrementToolUsage(string sessionId, ToolUsageKind kind, string name)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(name))
        {
            return;
        }

        ConcurrentDictionary<string, ConcurrentDictionary<string, int>> store =
            kind == ToolUsageKind.Skill ? _skillHits : _mcpHits;

        ConcurrentDictionary<string, int> perTool = store.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, int>());
        perTool.AddOrUpdate(name, 1, (_, existing) => existing + 1);

        RaiseChanged();
    }

    /// <summary>Looks up a session's MCP/Skill hit counts. Returns empty dictionaries (never
    /// throws) if the session is unknown - see <see cref="ToolUsageSnapshot"/>.</summary>
    public ToolUsageSnapshot GetToolUsage(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return new ToolUsageSnapshot(EmptyToolCounts, EmptyToolCounts);
        }

        IReadOnlyDictionary<string, int> mcp = _mcpHits.TryGetValue(sessionId, out var mcpCounts)
            ? mcpCounts
            : EmptyToolCounts;

        IReadOnlyDictionary<string, int> skill = _skillHits.TryGetValue(sessionId, out var skillCounts)
            ? skillCounts
            : EmptyToolCounts;

        return new ToolUsageSnapshot(mcp, skill);
    }

    private static readonly IReadOnlyDictionary<string, int> EmptyToolCounts = new Dictionary<string, int>();
}

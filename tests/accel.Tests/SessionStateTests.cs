using Accel.Metrics;
using Xunit;

namespace Accel.Tests;

public class SessionStateTests
{
    [Fact]
    public void TryGetSession_MissingKey_ReturnsFalse_NoThrow()
    {
        var state = new SessionState();

        bool found = state.TryGetSession("does-not-exist", out var snapshot);

        Assert.False(found);
        Assert.Null(snapshot);
    }

    [Fact]
    public void TryGetAgent_MissingKey_ReturnsFalse_NoThrow()
    {
        var state = new SessionState();

        bool found = state.TryGetAgent("does-not-exist", out var record);

        Assert.False(found);
        Assert.Null(record);
    }

    [Fact]
    public void UpdateSessionSnapshot_ThenTryGet_ReturnsIt()
    {
        var state = new SessionState();
        var snapshot = new SessionSnapshot(
            "session-1", "claude-sonnet-5", "Sonnet", "medium",
            200_000, 1000, 0.5, 0.5, 0.01m, "2.1.224", DateTime.UtcNow);

        state.UpdateSessionSnapshot(snapshot);

        bool found = state.TryGetSession("session-1", out var readBack);

        Assert.True(found);
        Assert.Equal("claude-sonnet-5", readBack!.ModelId);
    }

    [Fact]
    public void UpdateSessionSnapshot_WithSessionName_ThenTryGet_ReturnsSessionName()
    {
        var state = new SessionState();
        var snapshot = new SessionSnapshot(
            "session-named", "claude-sonnet-5", "Sonnet", "medium",
            200_000, 1000, 0.5, 0.5, 0.01m, "2.1.224", DateTime.UtcNow,
            SessionName: "Build session monitoring application");

        state.UpdateSessionSnapshot(snapshot);

        bool found = state.TryGetSession("session-named", out var readBack);

        Assert.True(found);
        Assert.Equal("Build session monitoring application", readBack!.SessionName);
    }

    [Fact]
    public void UpdateSessionSnapshot_WithoutSessionName_SessionNameIsNull()
    {
        var state = new SessionState();
        var snapshot = new SessionSnapshot(
            "session-unnamed", "claude-sonnet-5", "Sonnet", "medium",
            200_000, 1000, 0.5, 0.5, 0.01m, "2.1.224", DateTime.UtcNow);

        state.UpdateSessionSnapshot(snapshot);

        bool found = state.TryGetSession("session-unnamed", out var readBack);

        Assert.True(found);
        Assert.Null(readBack!.SessionName);
    }

    [Fact]
    public void UpdateAgentRecord_WithName_ThenTryGet_ReturnsName()
    {
        var state = new SessionState();
        var record = new AgentRecord(
            "agent-named", "code-reviewer", "session-1", "claude-opus-5", "high",
            10, 20, 0, 0, 200_000, AgentStatus.Live, DateTime.UtcNow, "subagentStatusLine",
            Name: "Audit project-ui.md");

        state.UpdateAgentRecord(record);

        bool found = state.TryGetAgent("agent-named", out var readBack);

        Assert.True(found);
        Assert.Equal("Audit project-ui.md", readBack!.Name);
    }

    [Fact]
    public void UpdateAgentRecord_ThenMarkAgentEnded_TransitionsStatus()
    {
        var state = new SessionState();
        var record = new AgentRecord(
            "agent-1", "code-reviewer", "session-1", "claude-opus-5", "high",
            10, 20, 0, 0, 200_000, AgentStatus.Live, DateTime.UtcNow, "transcript");

        state.UpdateAgentRecord(record);
        state.TryGetAgent("agent-1", out var beforeEnd);
        Assert.Equal(AgentStatus.Live, beforeEnd!.Status);

        state.MarkAgentEnded("agent-1");

        state.TryGetAgent("agent-1", out var afterEnd);
        Assert.Equal(AgentStatus.Ended, afterEnd!.Status);
        // Non-status fields must survive the transition.
        Assert.Equal("claude-opus-5", afterEnd.ModelId);
    }

    [Fact]
    public void MarkAgentEnded_NoExistingRecord_InsertsMinimalEndedRecord()
    {
        var state = new SessionState();

        state.MarkAgentEnded("agent-never-seen");

        bool found = state.TryGetAgent("agent-never-seen", out var record);

        Assert.True(found);
        Assert.Equal(AgentStatus.Ended, record!.Status);
    }

    [Fact]
    public async Task ConcurrentUpdates_FromMultipleThreads_DoNotCorruptState()
    {
        var state = new SessionState();
        const int threadCount = 16;
        const int perThread = 200;

        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int threadIndex = t;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    string sessionId = $"session-{threadIndex}";
                    string agentId = $"agent-{threadIndex}-{i % 5}";

                    state.UpdateSessionSnapshot(new SessionSnapshot(
                        sessionId, "claude-sonnet-5", "Sonnet", "medium",
                        200_000, i, null, null, null, null, DateTime.UtcNow));

                    state.UpdateAgentRecord(new AgentRecord(
                        agentId, "general-purpose", sessionId, "claude-haiku-4-5-20251001", "low",
                        i, i, 0, 0, 200_000, AgentStatus.Live, DateTime.UtcNow, "transcript"));

                    if (i % 7 == 0)
                    {
                        state.MarkAgentEnded(agentId);
                    }
                }
            });
        }

        await Task.WhenAll(tasks);

        // Every session thread wrote exactly one distinct session id -> exactly threadCount sessions.
        Assert.Equal(threadCount, state.GetAllSessions().Count);

        // Each thread cycles through 5 agent ids -> threadCount * 5 distinct agents, no corruption.
        Assert.Equal(threadCount * 5, state.GetAllAgents().Count);

        for (int t = 0; t < threadCount; t++)
        {
            Assert.True(state.TryGetSession($"session-{t}", out _));
        }
    }

    // ---- AgentRecord.StartedAtUtc/StartedAtSource/TranscriptPath merge (claude-agentgraph.md
    // section 6.1) ------------------------------------------------------------------------

    [Fact]
    public void UpdateAgentRecord_FirstRecordWithNoStartedAt_FallsBackToFirstSeenTier()
    {
        var state = new SessionState();
        var receivedAt = new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc);
        var record = new AgentRecord(
            "agent-1", "general-purpose", "session-1", "claude-sonnet-5", "medium",
            10, 20, 0, 0, 200_000, AgentStatus.Live, receivedAt, "subagentStatusLine");

        state.UpdateAgentRecord(record);

        state.TryGetAgent("agent-1", out var readBack);
        Assert.Equal(receivedAt, readBack!.StartedAtUtc);
        Assert.Equal("first_seen", readBack.StartedAtSource);
    }

    [Fact]
    public void UpdateAgentRecord_FirstRecordWithTierOneStartedAt_KeepsIt()
    {
        var state = new SessionState();
        var startedAt = new DateTime(2026, 8, 13, 8, 0, 0, DateTimeKind.Utc);
        var record = new AgentRecord(
            "agent-1", "general-purpose", "session-1", "claude-sonnet-5", "medium",
            10, 20, 0, 0, 200_000, AgentStatus.Live, DateTime.UtcNow, "transcript",
            StartedAtUtc: startedAt, StartedAtSource: "transcript");

        state.UpdateAgentRecord(record);

        state.TryGetAgent("agent-1", out var readBack);
        Assert.Equal(startedAt, readBack!.StartedAtUtc);
        Assert.Equal("transcript", readBack.StartedAtSource);
    }

    [Fact]
    public void UpdateAgentRecord_LaterUpdateWithNullStartedAt_KeepsTheOriginal()
    {
        var state = new SessionState();
        var startedAt = new DateTime(2026, 8, 13, 8, 0, 0, DateTimeKind.Utc);
        state.UpdateAgentRecord(new AgentRecord(
            "agent-1", "general-purpose", "session-1", "claude-sonnet-5", "medium",
            10, 20, 0, 0, 200_000, AgentStatus.Live, DateTime.UtcNow, "task_start_time",
            StartedAtUtc: startedAt, StartedAtSource: "task_start_time"));

        // A later subagentStatusLine tick that this time didn't carry a task startTime.
        state.UpdateAgentRecord(new AgentRecord(
            "agent-1", "general-purpose", "session-1", "claude-sonnet-5", "medium",
            50, 20, 0, 0, 200_000, AgentStatus.Live, DateTime.UtcNow, "subagentStatusLine",
            StartedAtUtc: null, StartedAtSource: null));

        state.TryGetAgent("agent-1", out var readBack);
        Assert.Equal(startedAt, readBack!.StartedAtUtc);
        Assert.Equal("task_start_time", readBack.StartedAtSource);
        // Non-start-time fields still update normally.
        Assert.Equal(50, readBack.InputTokens);
    }

    [Fact]
    public void UpdateAgentRecord_LaterUpdateWithLaterStartedAt_KeepsTheEarlier()
    {
        var state = new SessionState();
        var earlier = new DateTime(2026, 8, 13, 8, 0, 0, DateTimeKind.Utc);
        var later = new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc);

        state.UpdateAgentRecord(new AgentRecord(
            "agent-1", "general-purpose", "session-1", "claude-sonnet-5", "medium",
            10, 20, 0, 0, 200_000, AgentStatus.Live, DateTime.UtcNow, "transcript",
            StartedAtUtc: earlier, StartedAtSource: "transcript"));

        state.UpdateAgentRecord(new AgentRecord(
            "agent-1", "general-purpose", "session-1", "claude-sonnet-5", "medium",
            10, 20, 0, 0, 200_000, AgentStatus.Live, DateTime.UtcNow, "task_start_time",
            StartedAtUtc: later, StartedAtSource: "task_start_time"));

        state.TryGetAgent("agent-1", out var readBack);
        Assert.Equal(earlier, readBack!.StartedAtUtc);
        Assert.Equal("transcript", readBack.StartedAtSource);
    }

    [Fact]
    public void UpdateAgentRecord_LaterUpdateWithEarlierStartedAt_AdoptsTheEarlierOne()
    {
        var state = new SessionState();
        var later = new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc);
        var earlier = new DateTime(2026, 8, 13, 8, 0, 0, DateTimeKind.Utc);

        state.UpdateAgentRecord(new AgentRecord(
            "agent-1", "general-purpose", "session-1", "claude-sonnet-5", "medium",
            10, 20, 0, 0, 200_000, AgentStatus.Live, DateTime.UtcNow, "task_start_time",
            StartedAtUtc: later, StartedAtSource: "task_start_time"));

        state.UpdateAgentRecord(new AgentRecord(
            "agent-1", "general-purpose", "session-1", "claude-sonnet-5", "medium",
            10, 20, 0, 0, 200_000, AgentStatus.Live, DateTime.UtcNow, "transcript",
            StartedAtUtc: earlier, StartedAtSource: "transcript"));

        state.TryGetAgent("agent-1", out var readBack);
        Assert.Equal(earlier, readBack!.StartedAtUtc);
        Assert.Equal("transcript", readBack.StartedAtSource);
    }

    [Fact]
    public void UpdateAgentRecord_LaterUpdateWithNullTranscriptPath_KeepsTheKnownPath()
    {
        var state = new SessionState();
        state.UpdateAgentRecord(new AgentRecord(
            "agent-1", "general-purpose", "session-1", "claude-sonnet-5", "medium",
            10, 20, 0, 0, 200_000, AgentStatus.Live, DateTime.UtcNow, "transcript",
            TranscriptPath: @"C:\some\agent-1.jsonl"));

        state.UpdateAgentRecord(new AgentRecord(
            "agent-1", "general-purpose", "session-1", "claude-sonnet-5", "medium",
            50, 20, 0, 0, 200_000, AgentStatus.Live, DateTime.UtcNow, "subagentStatusLine",
            TranscriptPath: null));

        state.TryGetAgent("agent-1", out var readBack);
        Assert.Equal(@"C:\some\agent-1.jsonl", readBack!.TranscriptPath);
    }

    [Fact]
    public void MarkAgentEnded_PreservesStartedAtUtc()
    {
        var state = new SessionState();
        var startedAt = new DateTime(2026, 8, 13, 8, 0, 0, DateTimeKind.Utc);
        state.UpdateAgentRecord(new AgentRecord(
            "agent-1", "general-purpose", "session-1", "claude-sonnet-5", "medium",
            10, 20, 0, 0, 200_000, AgentStatus.Live, DateTime.UtcNow, "transcript",
            StartedAtUtc: startedAt, StartedAtSource: "transcript"));

        state.MarkAgentEnded("agent-1");

        state.TryGetAgent("agent-1", out var readBack);
        Assert.Equal(AgentStatus.Ended, readBack!.Status);
        Assert.Equal(startedAt, readBack.StartedAtUtc);
        Assert.Equal("transcript", readBack.StartedAtSource);
    }

    [Fact]
    public void ReconcileLiveAgents_StalePathPreservesStartedAtUtc()
    {
        var state = new SessionState();
        var startedAt = new DateTime(2026, 8, 13, 8, 0, 0, DateTimeKind.Utc);
        state.UpdateAgentRecord(new AgentRecord(
            "agent-1", "general-purpose", "session-1", "claude-sonnet-5", "medium",
            10, 20, 0, 0, 200_000, AgentStatus.Live, DateTime.UtcNow, "subagentStatusLine",
            StartedAtUtc: startedAt, StartedAtSource: "task_start_time"));

        state.ReconcileLiveAgents(new HashSet<string>()); // agent-1 no longer visible -> Stale

        state.TryGetAgent("agent-1", out var readBack);
        Assert.Equal(AgentStatus.Stale, readBack!.Status);
        Assert.Equal(startedAt, readBack.StartedAtUtc);
        Assert.Equal("task_start_time", readBack.StartedAtSource);
    }

    // ---- Changed event: the primary push signal MonitorForm subscribes to directly ------

    [Fact]
    public void UpdateSessionSnapshot_RaisesChanged()
    {
        var state = new SessionState();
        int raised = 0;
        state.Changed += () => raised++;

        state.UpdateSessionSnapshot(new SessionSnapshot(
            "session-1", "claude-sonnet-5", "Sonnet", "medium",
            200_000, 1000, 0.5, 0.5, 0.01m, "2.1.224", DateTime.UtcNow));

        Assert.Equal(1, raised);
    }

    [Fact]
    public void UpdateSessionSnapshot_WithNullOrEmptySessionId_DoesNotRaiseChanged()
    {
        var state = new SessionState();
        int raised = 0;
        state.Changed += () => raised++;

        state.UpdateSessionSnapshot(new SessionSnapshot(
            string.Empty, "claude-sonnet-5", "Sonnet", "medium",
            200_000, 1000, 0.5, 0.5, 0.01m, "2.1.224", DateTime.UtcNow));

        Assert.Equal(0, raised);
    }

    [Fact]
    public void UpdateAgentRecord_RaisesChanged()
    {
        var state = new SessionState();
        int raised = 0;
        state.Changed += () => raised++;

        state.UpdateAgentRecord(new AgentRecord(
            "agent-1", "code-reviewer", "session-1", "claude-opus-5", "high",
            10, 20, 0, 0, 200_000, AgentStatus.Live, DateTime.UtcNow, "transcript"));

        Assert.Equal(1, raised);
    }

    [Fact]
    public void MarkAgentEnded_RaisesChanged()
    {
        var state = new SessionState();
        int raised = 0;
        state.Changed += () => raised++;

        state.MarkAgentEnded("agent-never-seen");

        Assert.Equal(1, raised);
    }

    [Fact]
    public void MarkSessionEnded_RaisesChanged()
    {
        var state = new SessionState();
        int raised = 0;
        state.Changed += () => raised++;

        state.MarkSessionEnded("session-never-seen");

        Assert.Equal(1, raised);
    }

    [Fact]
    public void ReconcileLiveAgents_TransitioningAnAgentToStale_RaisesChanged()
    {
        var state = new SessionState();
        state.UpdateAgentRecord(new AgentRecord(
            "agent-1", "code-reviewer", "session-1", "claude-opus-5", "high",
            10, 20, 0, 0, 200_000, AgentStatus.Live, DateTime.UtcNow, "subagentStatusLine"));

        int raised = 0;
        state.Changed += () => raised++;

        state.ReconcileLiveAgents(new HashSet<string>()); // agent-1 is no longer visible -> Stale

        Assert.Equal(1, raised);
    }

    [Fact]
    public void ReconcileLiveAgents_NoAgentsTransition_DoesNotRaiseChanged()
    {
        var state = new SessionState();
        state.UpdateAgentRecord(new AgentRecord(
            "agent-1", "code-reviewer", "session-1", "claude-opus-5", "high",
            10, 20, 0, 0, 200_000, AgentStatus.Live, DateTime.UtcNow, "subagentStatusLine"));

        int raised = 0;
        state.Changed += () => raised++;

        // agent-1 is still visible -> no transition, no Changed.
        state.ReconcileLiveAgents(new HashSet<string> { "agent-1" });

        Assert.Equal(0, raised);
    }
}

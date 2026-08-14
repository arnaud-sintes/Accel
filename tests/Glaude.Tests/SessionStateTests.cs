using Glaude.Metrics;
using Xunit;

namespace Glaude.Tests;

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

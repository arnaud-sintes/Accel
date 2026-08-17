namespace Accel.Tests;

using System;
using System.Collections.Generic;
using Accel.Cli;
using Accel.Metrics;
using Xunit;

/// <summary>
/// Unit tests for the pure, non-UI <see cref="MonitorTreeBuilder"/> logic that backs the `ui`
/// verb's <see cref="MonitorForm"/> - fixture <see cref="RootsTreeDto"/>-shaped objects in,
/// node-description text/styling out, with no WinForms control ever instantiated.
/// </summary>
public class MonitorTreeBuilderTests
{
    private static SessionTreeDto LiveSession(string id = "22b04584-99e9-4343-b36d-8937b69321da", AgentTreeDto[]? agents = null) => new(
        SessionId: id,
        Name: "Build session monitoring application",
        NameSource: "status_line",
        Cwd: @"C:\projects",
        ProjectDir: "C--projects",
        IsLive: true,
        Status: "live",
        ModelId: "claude-opus-5[1m]",
        ModelDisplayName: "Opus 5",
        EffortLevel: "high",
        ContextWindowSize: 1_000_000,
        ContextWindowSizeAssumed: false,
        UsedTokens: 148_223,
        UsedPercentage: 14.8,
        Source: "statusLine",
        AsOf: DateTime.UtcNow,
        LastActivityUtc: DateTime.UtcNow,
        Agents: agents ?? Array.Empty<AgentTreeDto>());

    private static SessionTreeDto HistoricalSession(string id = "hist-session-000000000000") => new(
        SessionId: id,
        Name: "Audit the UI design doc",
        NameSource: "first_message",
        Cwd: @"C:\projects",
        ProjectDir: "C--projects",
        IsLive: false,
        Status: "ended",
        ModelId: "claude-sonnet-5",
        ModelDisplayName: null,
        EffortLevel: "medium",
        ContextWindowSize: 200_000,
        ContextWindowSizeAssumed: true,
        UsedTokens: 62_000,
        UsedPercentage: 31.0,
        Source: "transcript",
        AsOf: DateTime.UtcNow,
        LastActivityUtc: DateTime.UtcNow,
        Agents: Array.Empty<AgentTreeDto>());

    private static AgentTreeDto LiveAgent(string id = "abc123") => new(
        AgentId: id,
        Name: "Audit project-ui.md",
        AgentType: "general-purpose",
        ModelId: "claude-sonnet-5",
        EffortLevel: "medium",
        InputTokens: 41200,
        OutputTokens: 3100,
        CacheCreationInputTokens: 0,
        CacheReadInputTokens: 0,
        ContextWindowSize: 200_000,
        ContextWindowSizeAssumed: true,
        UsedPercentage: 20.6,
        Status: "live",
        Source: "subagentStatusLine",
        AsOf: DateTime.UtcNow);

    private static AgentTreeDto StaleAgent(string id = "stale-agent") => new(
        AgentId: id,
        Name: "Old sub-task",
        AgentType: "general-purpose",
        ModelId: "claude-sonnet-5",
        EffortLevel: "low",
        InputTokens: 100,
        OutputTokens: 10,
        CacheCreationInputTokens: 0,
        CacheReadInputTokens: 0,
        ContextWindowSize: 200_000,
        ContextWindowSizeAssumed: true,
        UsedPercentage: 5.0,
        Status: "stale",
        Source: "subagentStatusLine",
        AsOf: DateTime.UtcNow);

    private static AgentTreeDto HistoricalAgent(string id = "historical-agent") => new(
        AgentId: id,
        Name: "Finished sub-task",
        AgentType: "general-purpose",
        ModelId: "claude-sonnet-5",
        EffortLevel: "low",
        InputTokens: 100,
        OutputTokens: 10,
        CacheCreationInputTokens: 0,
        CacheReadInputTokens: 0,
        ContextWindowSize: 200_000,
        ContextWindowSizeAssumed: true,
        UsedPercentage: 5.0,
        Status: "ended",
        Source: "subagentStatusLine",
        AsOf: DateTime.UtcNow);

    // ---- root with a live session with a live agent -------------------------------------

    [Fact]
    public void Build_RootWithLiveSessionAndLiveAgent_ProducesExpectedShapeAndStyling()
    {
        var dto = new RootsTreeDto(
            Roots: new[]
            {
                new RootTreeDto(@"C:\projects", true, new[] { LiveSession(agents: new[] { LiveAgent() }) }),
            },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);

        var tree = MonitorTreeBuilder.Build(dto);

        Assert.Single(tree.Roots);
        var root = tree.Roots[0];
        Assert.Equal(@"C:\projects", root.Path);
        Assert.Equal(@"C:\projects (1 sessions, 1 running)", root.Text);

        Assert.Single(root.Sessions);
        var session = root.Sessions[0];
        Assert.Equal(MonitorNodeState.Live, session.State);
        Assert.Equal(
            "● Build session monitoring application — 22b04584-99e… — Opus 5 — effort=high — 14.8% of 1M",
            session.Text);
        // P4-T4/T3: ProjectDir is carried through purely for SessionRemover.Plan's projectDir parameter -
        // nothing else in this file's own output depends on it.
        Assert.Equal("C--projects", session.ProjectDir);

        Assert.Single(session.Agents);
        var agent = session.Agents[0];
        Assert.Equal(MonitorNodeState.Live, agent.State);
        Assert.Equal(
            "● general-purpose · Audit project-ui.md — claude-sonnet-5 — effort=medium — 20.6% (assumed)",
            agent.Text);

        Assert.Null(tree.Unattributed);
    }

    // ---- root with a historical session (no agents) -------------------------------------

    [Fact]
    public void Build_RootWithHistoricalSession_HasNoAgentsAndHistoricalStyling()
    {
        var dto = new RootsTreeDto(
            Roots: new[] { new RootTreeDto(@"C:\projects", true, new[] { HistoricalSession() }) },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);

        var tree = MonitorTreeBuilder.Build(dto);

        var session = tree.Roots[0].Sessions[0];
        Assert.Equal(MonitorNodeState.Historical, session.State);
        Assert.Empty(session.Agents);
        Assert.DoesNotContain("●", session.Text);
        Assert.StartsWith("Audit the UI design doc", session.Text);
        Assert.Contains("(assumed)", session.Text);
    }

    // ---- empty root -----------------------------------------------------------------------

    [Fact]
    public void Build_EmptyRoot_HasZeroSessionsAndNoRunningSuffix()
    {
        var dto = new RootsTreeDto(
            Roots: new[] { new RootTreeDto(@"C:\projects", true, Array.Empty<SessionTreeDto>()) },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);

        var tree = MonitorTreeBuilder.Build(dto);

        var root = tree.Roots[0];
        Assert.Empty(root.Sessions);
        Assert.Equal(@"C:\projects", root.Text); // no "(0 sessions, ...)" suffix when empty
    }

    // ---- unattributed sessions/agents present vs absent ----------------------------------

    [Fact]
    public void Build_NoUnattributedData_UnattributedNodeIsNull()
    {
        var dto = new RootsTreeDto(
            Roots: Array.Empty<RootTreeDto>(),
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);

        var tree = MonitorTreeBuilder.Build(dto);

        Assert.Null(tree.Unattributed);
    }

    [Fact]
    public void Build_UnattributedSessionsAndAgentsPresent_ProducesUnattributedNode()
    {
        var dto = new RootsTreeDto(
            Roots: Array.Empty<RootTreeDto>(),
            UnattributedSessions: new[] { HistoricalSession("unattr-session") },
            UnattributedAgents: new[] { LiveAgent("orphan-agent") },
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);

        var tree = MonitorTreeBuilder.Build(dto);

        Assert.NotNull(tree.Unattributed);
        Assert.Equal("(unattributed)", tree.Unattributed!.Text);
        Assert.Single(tree.Unattributed.Sessions);
        Assert.Single(tree.Unattributed.OrphanAgents);
        Assert.Equal("orphan-agent", tree.Unattributed.OrphanAgents[0].AgentId);
    }

    // ---- context_window_size_assumed=true shows the "(assumed)" marker ------------------

    [Fact]
    public void Build_SessionWithAssumedWindow_ShowsAssumedMarker()
    {
        var session = LiveSession() with { ContextWindowSizeAssumed = true };
        var dto = new RootsTreeDto(
            Roots: new[] { new RootTreeDto(@"C:\projects", true, new[] { session }) },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);

        var tree = MonitorTreeBuilder.Build(dto);

        Assert.EndsWith("(assumed)", tree.Roots[0].Sessions[0].Text);
    }

    [Fact]
    public void Build_SessionWithObservedWindow_HasNoAssumedMarker()
    {
        var dto = new RootsTreeDto(
            Roots: new[] { new RootTreeDto(@"C:\projects", true, new[] { LiveSession() }) },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);

        var tree = MonitorTreeBuilder.Build(dto);

        Assert.DoesNotContain("assumed", tree.Roots[0].Sessions[0].Text);
    }

    // ---- stale agent gets the stale styling flag, not live or plain-historical ----------

    [Fact]
    public void Build_StaleAgent_GetsStaleState_NotLiveOrHistorical()
    {
        var session = LiveSession(agents: new[] { StaleAgent() });
        var dto = new RootsTreeDto(
            Roots: new[] { new RootTreeDto(@"C:\projects", true, new[] { session }) },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);

        var tree = MonitorTreeBuilder.Build(dto);

        var agent = tree.Roots[0].Sessions[0].Agents[0];
        Assert.Equal(MonitorNodeState.Stale, agent.State);
        Assert.NotEqual(MonitorNodeState.Live, agent.State);
        Assert.NotEqual(MonitorNodeState.Historical, agent.State);
        Assert.StartsWith("? ", agent.Text);
    }

    // ---- null dto degrades cleanly --------------------------------------------------------

    [Fact]
    public void Build_NullDto_ProducesEmptyTreeWithoutThrowing()
    {
        var tree = MonitorTreeBuilder.Build(null);

        Assert.Empty(tree.Roots);
        Assert.Null(tree.Unattributed);
    }

    // ---- MonitorTreeExpansion: preserving TreeView expand state across a rebuild --------
    // See project-ui.md's "Rendering" section: expand state must be keyed on the stable ids
    // (path / session_id / agent_id), never on node index/position.

    [Fact]
    public void ComputeKeysToExpand_NodeStillPresentAndPreviouslyExpanded_StaysExpanded()
    {
        var dto = new RootsTreeDto(
            Roots: new[] { new RootTreeDto(@"C:\projects", true, new[] { LiveSession() }) },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);
        var newTree = MonitorTreeBuilder.Build(dto);

        const string sessionId = "22b04584-99e9-4343-b36d-8937b69321da"; // matches LiveSession()'s default
        var previouslyExpanded = new HashSet<string> { @"C:\projects", sessionId };

        var toExpand = MonitorTreeExpansion.ComputeKeysToExpand(newTree, previouslyExpanded);

        Assert.Contains(@"C:\projects", toExpand);
        Assert.Contains(sessionId, toExpand);
    }

    [Fact]
    public void ComputeKeysToExpand_BrandNewNodeNotPreviouslyExpanded_IsNotExpanded()
    {
        var dto = new RootsTreeDto(
            Roots: new[] { new RootTreeDto(@"C:\projects", true, new[] { LiveSession() }) },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);
        var newTree = MonitorTreeBuilder.Build(dto);

        // Nothing was previously expanded (e.g. this session just appeared this tick).
        var previouslyExpanded = new HashSet<string>();

        var toExpand = MonitorTreeExpansion.ComputeKeysToExpand(newTree, previouslyExpanded);

        Assert.Empty(toExpand);
    }

    [Fact]
    public void ComputeKeysToExpand_PreviouslyExpandedNodeNowGone_IsSimplyAbsentNoError()
    {
        // The new tree no longer has the session that used to be expanded (e.g. it ended and got
        // filtered out this tick) - this must not throw, and the stale key must not leak into the
        // result (there's nothing in the new tree to apply it to).
        var dto = new RootsTreeDto(
            Roots: new[] { new RootTreeDto(@"C:\projects", true, Array.Empty<SessionTreeDto>()) },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);
        var newTree = MonitorTreeBuilder.Build(dto);

        var previouslyExpanded = new HashSet<string> { @"C:\projects", "ended-session-id", "some-agent-id" };

        var toExpand = MonitorTreeExpansion.ComputeKeysToExpand(newTree, previouslyExpanded);

        Assert.Contains(@"C:\projects", toExpand); // the root itself is still there
        Assert.DoesNotContain("ended-session-id", toExpand);
        Assert.DoesNotContain("some-agent-id", toExpand);
    }

    [Fact]
    public void ComputeKeysToExpand_MatchesAcrossRootSessionAndAgentIdFields()
    {
        var dto = new RootsTreeDto(
            Roots: new[]
            {
                new RootTreeDto(@"C:\projects", true, new[] { LiveSession(agents: new[] { LiveAgent("agent-1") }) }),
            },
            UnattributedSessions: new[] { HistoricalSession("unattr-session") },
            UnattributedAgents: new[] { LiveAgent("orphan-agent") },
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);
        var newTree = MonitorTreeBuilder.Build(dto);

        const string sessionId = "22b04584-99e9-4343-b36d-8937b69321da"; // matches LiveSession()'s default
        var previouslyExpanded = new HashSet<string>
        {
            @"C:\projects",
            sessionId,
            "agent-1",
            "(unattributed)",
            "unattr-session",
            "orphan-agent",
        };

        var toExpand = MonitorTreeExpansion.ComputeKeysToExpand(newTree, previouslyExpanded);

        Assert.Contains(@"C:\projects", toExpand);
        Assert.Contains(sessionId, toExpand);
        Assert.Contains("agent-1", toExpand);
        Assert.Contains("(unattributed)", toExpand);
        Assert.Contains("unattr-session", toExpand);
        Assert.Contains("orphan-agent", toExpand);
    }

    [Fact]
    public void ComputeKeysToExpand_NoPreviouslyExpandedKeys_ReturnsEmptySet()
    {
        var dto = new RootsTreeDto(
            Roots: new[] { new RootTreeDto(@"C:\projects", true, new[] { LiveSession() }) },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);
        var newTree = MonitorTreeBuilder.Build(dto);

        var toExpand = MonitorTreeExpansion.ComputeKeysToExpand(newTree, new HashSet<string>());

        Assert.Empty(toExpand);
    }

    // ---- MonitorTreeExpansion.ComputeDefaultExpansionForNewKeys: auto-expand-live-by-default -

    private const string SessionId = "22b04584-99e9-4343-b36d-8937b69321da"; // matches LiveSession()'s default

    [Fact]
    public void ComputeDefaultExpansionForNewKeys_BrandNewRootWithLiveSession_BothDefaultExpand()
    {
        var dto = new RootsTreeDto(
            Roots: new[] { new RootTreeDto(@"C:\projects", true, new[] { LiveSession() }) },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);
        var newTree = MonitorTreeBuilder.Build(dto);

        var toExpand = MonitorTreeExpansion.ComputeDefaultExpansionForNewKeys(newTree, new HashSet<string>());

        Assert.Contains(@"C:\projects", toExpand);
        Assert.Contains(SessionId, toExpand);
    }

    [Fact]
    public void ComputeDefaultExpansionForNewKeys_BrandNewHistoricalOnlyRootAndSession_DoesNotDefaultExpand()
    {
        var dto = new RootsTreeDto(
            Roots: new[] { new RootTreeDto(@"C:\projects", true, new[] { HistoricalSession() }) },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);
        var newTree = MonitorTreeBuilder.Build(dto);

        var toExpand = MonitorTreeExpansion.ComputeDefaultExpansionForNewKeys(newTree, new HashSet<string>());

        Assert.Empty(toExpand);
    }

    [Fact]
    public void ComputeDefaultExpansionForNewKeys_AlreadySeenLiveSession_IsNotIncludedEvenThoughLive()
    {
        // The session (and its root) were already rendered on a previous tick, so their expand
        // state must come from ComputeKeysToExpand's preservation, not from this "first
        // appearance" default - even though the session is still live right now.
        var dto = new RootsTreeDto(
            Roots: new[] { new RootTreeDto(@"C:\projects", true, new[] { LiveSession() }) },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);
        var newTree = MonitorTreeBuilder.Build(dto);

        var everSeenKeys = new HashSet<string> { @"C:\projects", SessionId };

        var toExpand = MonitorTreeExpansion.ComputeDefaultExpansionForNewKeys(newTree, everSeenKeys);

        Assert.Empty(toExpand);
    }

    [Fact]
    public void ComputeDefaultExpansionForNewKeys_LiveAgentUnderLiveSession_RootAndSessionDefaultExpandNoError()
    {
        var dto = new RootsTreeDto(
            Roots: new[]
            {
                new RootTreeDto(@"C:\projects", true, new[] { LiveSession(agents: new[] { LiveAgent("agent-1") }) }),
            },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);
        var newTree = MonitorTreeBuilder.Build(dto);

        var toExpand = MonitorTreeExpansion.ComputeDefaultExpansionForNewKeys(newTree, new HashSet<string>());

        Assert.Contains(@"C:\projects", toExpand);
        Assert.Contains(SessionId, toExpand);
    }

    [Fact]
    public void ComputeDefaultExpansionForNewKeys_OnlyRootPreviouslySeen_SessionStillDefaultExpandsAndCascadesToRoot()
    {
        // Per-key "first appearance": the root was seen before but this particular session is
        // brand new (and live), so the session gets the default on its own first-appearance
        // rule - and that now cascades UP to the root too, because otherwise an already-rendered
        // root would stay collapsed forever around a session that never existed the first time
        // the root was drawn (the same class of bug as the reported "new sub-agent hidden under
        // an already-seen session", just one level up: new session hidden under an already-seen
        // root). This intentionally supersedes the previous version of this test, which asserted
        // the root was NOT included - that was the bug this change fixes, not a scenario worth
        // preserving.
        var dto = new RootsTreeDto(
            Roots: new[] { new RootTreeDto(@"C:\projects", true, new[] { LiveSession() }) },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);
        var newTree = MonitorTreeBuilder.Build(dto);

        var everSeenKeys = new HashSet<string> { @"C:\projects" };

        var toExpand = MonitorTreeExpansion.ComputeDefaultExpansionForNewKeys(newTree, everSeenKeys);

        Assert.Contains(@"C:\projects", toExpand);
        Assert.Contains(SessionId, toExpand);
    }

    // ---- cascading fix: a newly-appeared live descendant re-opens an already-seen ancestor ----

    [Fact]
    public void ComputeDefaultExpansionForNewKeys_NewLiveAgentUnderAlreadySeenSession_SessionKeyCascadesIntoResult()
    {
        // The exact regression scenario reported: the session existed before this tick (its key
        // is already in everSeenKeys) but a brand-new live sub-agent just appeared under it. The
        // session's own key isn't "new" any more, but the agent's is - and that must still cascade
        // up so the session auto-expands and reveals the new agent instead of staying collapsed.
        var dto = new RootsTreeDto(
            Roots: new[]
            {
                new RootTreeDto(@"C:\projects", true, new[] { LiveSession(agents: new[] { LiveAgent("new-agent") }) }),
            },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);
        var newTree = MonitorTreeBuilder.Build(dto);

        var everSeenKeys = new HashSet<string> { @"C:\projects", SessionId };

        var toExpand = MonitorTreeExpansion.ComputeDefaultExpansionForNewKeys(newTree, everSeenKeys);

        Assert.Contains(SessionId, toExpand);
    }

    [Fact]
    public void ComputeDefaultExpansionForNewKeys_NewLiveAgentUnderAlreadySeenSessionUnderAlreadySeenRoot_CascadesTwoLevelsToRoot()
    {
        // Same scenario one level further up: root and session both already seen, only the leaf
        // agent is new - the cascade must reach all the way up to the root so the whole path down
        // to the new agent auto-opens.
        var dto = new RootsTreeDto(
            Roots: new[]
            {
                new RootTreeDto(@"C:\projects", true, new[] { LiveSession(agents: new[] { LiveAgent("new-agent") }) }),
            },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);
        var newTree = MonitorTreeBuilder.Build(dto);

        var everSeenKeys = new HashSet<string> { @"C:\projects", SessionId };

        var toExpand = MonitorTreeExpansion.ComputeDefaultExpansionForNewKeys(newTree, everSeenKeys);

        Assert.Contains(@"C:\projects", toExpand);
        Assert.Contains(SessionId, toExpand);
    }

    [Fact]
    public void ComputeDefaultExpansionForNewKeys_AlreadySeenLiveAgentUnderAlreadySeenSession_DoesNotReAddSession()
    {
        // Negative case: the session still contains a live agent, but that agent was already seen
        // on a previous tick too (nothing new appeared). This must NOT re-add the session - a
        // session the user deliberately collapsed must not be forced back open just because it
        // still happens to contain a live agent; that is governed by preservation only.
        var dto = new RootsTreeDto(
            Roots: new[]
            {
                new RootTreeDto(@"C:\projects", true, new[] { LiveSession(agents: new[] { LiveAgent("agent-1") }) }),
            },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);
        var newTree = MonitorTreeBuilder.Build(dto);

        var everSeenKeys = new HashSet<string> { @"C:\projects", SessionId, "agent-1" };

        var toExpand = MonitorTreeExpansion.ComputeDefaultExpansionForNewKeys(newTree, everSeenKeys);

        Assert.DoesNotContain(SessionId, toExpand);
        Assert.DoesNotContain(@"C:\projects", toExpand);
    }

    [Fact]
    public void ComputeDefaultExpansionForNewKeys_NewButNotLiveAgentUnderAlreadySeenSession_DoesNotAddSession()
    {
        // Negative case: a genuinely new agent key appears, but it's not live (e.g. historical/
        // ended) - only new-AND-live descendants cascade, not merely-new ones.
        var dto = new RootsTreeDto(
            Roots: new[]
            {
                new RootTreeDto(@"C:\projects", true, new[] { LiveSession(agents: new[] { HistoricalAgent("new-but-not-live") }) }),
            },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);
        var newTree = MonitorTreeBuilder.Build(dto);

        var everSeenKeys = new HashSet<string> { @"C:\projects", SessionId };

        var toExpand = MonitorTreeExpansion.ComputeDefaultExpansionForNewKeys(newTree, everSeenKeys);

        Assert.DoesNotContain(SessionId, toExpand);
        Assert.DoesNotContain(@"C:\projects", toExpand);
    }

    // ---- MonitorTreeExpansion.CollectAllKeys -----------------------------------------------

    [Fact]
    public void CollectAllKeys_ReturnsRootSessionAndAgentIds()
    {
        var dto = new RootsTreeDto(
            Roots: new[]
            {
                new RootTreeDto(@"C:\projects", true, new[] { LiveSession(agents: new[] { LiveAgent("agent-1") }) }),
            },
            UnattributedSessions: new[] { HistoricalSession("unattr-session") },
            UnattributedAgents: new[] { LiveAgent("orphan-agent") },
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);
        var tree = MonitorTreeBuilder.Build(dto);

        var keys = MonitorTreeExpansion.CollectAllKeys(tree);

        Assert.Contains(@"C:\projects", keys);
        Assert.Contains(SessionId, keys);
        Assert.Contains("agent-1", keys);
        Assert.Contains("(unattributed)", keys);
        Assert.Contains("unattr-session", keys);
        Assert.Contains("orphan-agent", keys);
    }

    // ---- MonitorTreeBuilder.GlyphFor --------------------------------------------------------

    [Theory]
    [InlineData(MonitorNodeState.Live, "●")]
    [InlineData(MonitorNodeState.Stale, "?")]
    [InlineData(MonitorNodeState.Historical, "")]
    public void GlyphFor_ReturnsExpectedGlyphPerState(MonitorNodeState state, string expected)
    {
        Assert.Equal(expected, MonitorTreeBuilder.GlyphFor(state));
    }

    // ---- Six-column data (MonitorRowColumns) -----------------------------------------------

    [Fact]
    public void Build_RootColumns_IdIsEmDashAndContextSummarisesSessions()
    {
        var dto = new RootsTreeDto(
            Roots: new[] { new RootTreeDto(@"C:\projects", true, new[] { LiveSession() }) },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);

        var tree = MonitorTreeBuilder.Build(dto);
        var columns = tree.Roots[0].Columns;

        Assert.Equal("—", columns.Id);
        Assert.Equal(@"C:\projects", columns.Name);
        Assert.Equal(string.Empty, columns.Type);
        Assert.Equal(string.Empty, columns.Model);
        Assert.Equal(string.Empty, columns.Effort);
        Assert.Equal("1 sessions, 1 running", columns.Context);
    }

    [Fact]
    public void Build_EmptyRootColumns_ContextIsBlank()
    {
        var dto = new RootsTreeDto(
            Roots: new[] { new RootTreeDto(@"C:\projects", true, Array.Empty<SessionTreeDto>()) },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);

        var tree = MonitorTreeBuilder.Build(dto);

        Assert.Equal(string.Empty, tree.Roots[0].Columns.Context);
    }

    [Fact]
    public void Build_SessionColumns_MapExpectedFields()
    {
        var dto = new RootsTreeDto(
            Roots: new[] { new RootTreeDto(@"C:\projects", true, new[] { LiveSession() }) },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);

        var tree = MonitorTreeBuilder.Build(dto);
        var columns = tree.Roots[0].Sessions[0].Columns;

        Assert.Equal("22b04584-99e", columns.Id);
        Assert.Equal("Build session monitoring application", columns.Name);
        Assert.Equal("session", columns.Type);
        Assert.Equal("Opus 5", columns.Model);
        Assert.Equal("high", columns.Effort);
        Assert.Equal("14.8% of 1M", columns.Context);
    }

    [Fact]
    public void Build_SessionColumns_AssumedWindowAddsSuffixToContext()
    {
        var session = LiveSession() with { ContextWindowSizeAssumed = true };
        var dto = new RootsTreeDto(
            Roots: new[] { new RootTreeDto(@"C:\projects", true, new[] { session }) },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);

        var tree = MonitorTreeBuilder.Build(dto);

        Assert.EndsWith("(assumed)", tree.Roots[0].Sessions[0].Columns.Context);
    }

    [Fact]
    public void Build_AgentColumns_MapExpectedFields()
    {
        var dto = new RootsTreeDto(
            Roots: new[] { new RootTreeDto(@"C:\projects", true, new[] { LiveSession(agents: new[] { LiveAgent() }) }) },
            UnattributedSessions: Array.Empty<SessionTreeDto>(),
            UnattributedAgents: Array.Empty<AgentTreeDto>(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: 1);

        var tree = MonitorTreeBuilder.Build(dto);
        var columns = tree.Roots[0].Sessions[0].Agents[0].Columns;

        Assert.Equal("abc123", columns.Id);
        Assert.Equal("Audit project-ui.md", columns.Name);
        Assert.Equal("general-purpose", columns.Type);
        Assert.Equal("claude-sonnet-5", columns.Model);
        Assert.Equal("medium", columns.Effort);
        Assert.Equal("20.6% of 200K (assumed)", columns.Context);
    }
}

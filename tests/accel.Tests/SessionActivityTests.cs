namespace Accel.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Accel.Metrics;
using Xunit;

/// <summary>
/// Working / idle for a running session, inferred from the end of its transcript (no extra hook): the entry shapes
/// below are the ones real Claude Code transcripts contain (checked against transcripts on the development
/// machine - a finished turn ends <c>assistant stop_reason=end_turn</c> then <c>system</c>
/// <c>stop_hook_summary</c> and <c>turn_duration</c>; a busy one ends on a tool call, a tool result or a prompt).
/// </summary>
public class SessionActivityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"accel-activity-{Guid.NewGuid():N}");

    public SessionActivityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // --- transcript line builders ------------------------------------------------------------------------

    private static string Json(object o) => JsonSerializer.Serialize(o);

    private static string Prompt(string text = "do the thing") =>
        Json(new { type = "user", cwd = @"C:\projects", message = new { role = "user", content = text } });

    private static string ToolResult() =>
        Json(new { type = "user", message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "t1" } } } });

    private static string Interrupt() =>
        Json(new { type = "user", message = new { role = "user", content = new object[] { new { type = "text", text = "[Request interrupted by user]" } } } });

    private static string Assistant(string? stopReason, bool sidechain = false) =>
        Json(new Dictionary<string, object?>
        {
            ["type"] = "assistant",
            ["isSidechain"] = sidechain,
            ["message"] = new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["model"] = "claude-sonnet-5",
                ["stop_reason"] = stopReason,
                ["content"] = new object[] { new { type = "text", text = "..." } },
            },
        });

    private static string System(string subtype) => Json(new { type = "system", subtype });

    private static string Noise(string type) => Json(new Dictionary<string, object?> { ["type"] = type });

    private string Write(params string[] lines)
    {
        string path = Path.Combine(_root, $"{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static TranscriptTurnState Turn(string path) => TranscriptReader.ReadTailSummary(path).Turn;

    // --- the turn state ----------------------------------------------------------------------------------

    [Fact]
    public void AToolCallWaitingForItsResult_IsWorking()
    {
        Assert.Equal(TranscriptTurnState.Working, Turn(Write(Prompt(), Assistant("tool_use"))));
    }

    [Fact]
    public void AToolResultTheModelHasNotAnsweredYet_IsWorking()
    {
        Assert.Equal(TranscriptTurnState.Working, Turn(Write(Prompt(), Assistant("tool_use"), ToolResult())));
    }

    [Fact]
    public void APromptJustSubmitted_IsWorking()
    {
        Assert.Equal(TranscriptTurnState.Working, Turn(Write(Prompt("first"), Assistant("end_turn"), System("turn_duration"), Prompt("second"))));
    }

    [Fact]
    public void AnAssistantEntryStillBeingStreamed_IsWorking()
    {
        Assert.Equal(TranscriptTurnState.Working, Turn(Write(Prompt(), Assistant(stopReason: null))));
    }

    [Fact]
    public void AFinishedTurn_IsIdle()
    {
        Assert.Equal(TranscriptTurnState.Idle, Turn(Write(Prompt(), Assistant("tool_use"), ToolResult(), Assistant("end_turn"))));
    }

    [Fact]
    public void TheSystemEntriesThatFollowAFinishedTurn_KeepItIdle()
    {
        Assert.Equal(
            TranscriptTurnState.Idle,
            Turn(Write(Prompt(), Assistant("end_turn"), System("stop_hook_summary"), System("turn_duration"))));
    }

    [Fact]
    public void ATurnTheUserInterrupted_IsIdle_EvenThoughNoStopHookFires()
    {
        Assert.Equal(TranscriptTurnState.Idle, Turn(Write(Prompt(), Assistant("tool_use"), Interrupt())));
    }

    [Fact]
    public void BookkeepingEntries_AreIgnored_InBothDirections()
    {
        Assert.Equal(
            TranscriptTurnState.Idle,
            Turn(Write(Prompt(), Assistant("end_turn"), Noise("api-request"), Noise("attachment"), Noise("mode"))));
        Assert.Equal(
            TranscriptTurnState.Working,
            Turn(Write(Prompt(), Assistant("tool_use"), Noise("api-request"), Noise("attachment"), Noise("permission-mode"))));
    }

    [Fact]
    public void AnEchoedBuiltInSlashCommand_DoesNotStartATurn()
    {
        Assert.Equal(
            TranscriptTurnState.Idle,
            Turn(Write(Prompt(), Assistant("end_turn"), Prompt("<command-name>/model</command-name>"), Prompt("<local-command-stdout>ok</local-command-stdout>"))));
    }

    [Fact]
    public void AMetaUserEntry_IsIgnored()
    {
        string meta = Json(new { type = "user", isMeta = true, message = new { role = "user", content = "caveat" } });

        Assert.Equal(TranscriptTurnState.Idle, Turn(Write(Prompt(), Assistant("end_turn"), meta)));
    }

    [Fact]
    public void ASubAgentsOwnEntries_DoNotDecideTheMainTurn()
    {
        Assert.Equal(
            TranscriptTurnState.Working,
            Turn(Write(Prompt(), Assistant("tool_use"), Assistant("end_turn", sidechain: true))));
    }

    [Fact]
    public void ATornTrailingLine_IsSkipped_NotMistakenForTheEnd()
    {
        string path = Path.Combine(_root, "torn.jsonl");
        File.WriteAllText(path, string.Join("\n", Prompt(), Assistant("end_turn")) + "\n{\"type\":\"assistant\",\"mess");

        Assert.Equal(TranscriptTurnState.Idle, Turn(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ANeverWrittenOrMissingTranscript_IsUnknown(string? name)
    {
        string? path = name is null ? null : Path.Combine(_root, "does-not-exist.jsonl");
        Assert.Equal(TranscriptTurnState.Unknown, TranscriptReader.ReadTailSummary(path).Turn);
    }

    [Fact]
    public void TheSinglePass_StillReturnsTheAssistantEntryAndTheTitle()
    {
        string title = Json(new { type = "ai-title", aiTitle = "Refactor the thing", sessionId = "s" });

        var summary = TranscriptReader.ReadTailSummary(Write(Prompt(), title, Assistant("end_turn")));

        Assert.Equal("Refactor the thing", summary.AiTitle);
        Assert.Equal("claude-sonnet-5", summary.LastAssistant?.Model);
        Assert.Equal(TranscriptTurnState.Idle, summary.Turn);
    }

    // --- the resolver ------------------------------------------------------------------------------------

    private static readonly DateTime T = new(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ASessionThatIsNotRunning_HasNoActivity()
    {
        Assert.Equal(SessionActivity.None, SessionActivityResolver.Resolve(false, TranscriptTurnState.Working, null, T, hasLiveAgents: true));
    }

    [Fact]
    public void ALiveSubAgent_MakesTheSessionWorking_WhateverTheTranscriptSays()
    {
        Assert.Equal(SessionActivity.Working, SessionActivityResolver.Resolve(true, TranscriptTurnState.Idle, null, T, hasLiveAgents: true));
    }

    [Theory]
    [InlineData(TranscriptTurnState.Idle, SessionActivity.Idle)]
    [InlineData(TranscriptTurnState.Working, SessionActivity.Working)]
    [InlineData(TranscriptTurnState.Unknown, SessionActivity.Idle)] // nothing done yet: waiting for the first prompt
    public void WithoutSubAgents_TheTranscriptDecides(TranscriptTurnState turn, SessionActivity expected)
    {
        Assert.Equal(expected, SessionActivityResolver.Resolve(true, turn, null, T, hasLiveAgents: false));
    }

    [Fact]
    public void AStopHookNewerThanTheLastTranscriptWrite_OverridesAnOpenTurn()
    {
        Assert.Equal(
            SessionActivity.Idle,
            SessionActivityResolver.Resolve(true, TranscriptTurnState.Working, waitingSinceUtc: T.AddSeconds(2), transcriptLastWriteUtc: T, hasLiveAgents: false));
    }

    [Fact]
    public void AStopHookOlderThanTheLastWrite_MeansANewTurnHasStarted()
    {
        Assert.Equal(
            SessionActivity.Working,
            SessionActivityResolver.Resolve(true, TranscriptTurnState.Working, waitingSinceUtc: T.AddMinutes(-5), transcriptLastWriteUtc: T, hasLiveAgents: false));
    }

    [Theory]
    [InlineData(SessionActivity.Working, "working")]
    [InlineData(SessionActivity.Idle, "idle")]
    [InlineData(SessionActivity.None, null)]
    public void TheWireForm_RoundTrips(SessionActivity activity, string? wire)
    {
        Assert.Equal(wire, SessionActivityResolver.ToWire(activity));
        Assert.Equal(activity, SessionActivityResolver.FromWire(wire));
    }

    [Fact]
    public void AnUnknownWireValue_IsNone()
    {
        Assert.Equal(SessionActivity.None, SessionActivityResolver.FromWire("sleeping"));
    }

    // --- through the tree builder ------------------------------------------------------------------------

    private SessionTreeDto BuildSession(string[] transcriptLines, bool live, bool withLiveAgent = false, DateTime? waitingSinceUtc = null)
    {
        string sessionId = $"session-{Guid.NewGuid():N}";
        string slugDir = Path.Combine(_root, "C--projects");
        Directory.CreateDirectory(slugDir);
        File.WriteAllLines(Path.Combine(slugDir, $"{sessionId}.jsonl"), transcriptLines);

        var state = new SessionState();
        state.UpdateSessionSnapshot(new SessionSnapshot(
            SessionId: sessionId, ModelId: "claude-opus-5", ModelDisplayName: "Opus", EffortLevel: "high",
            ContextWindowSize: 200_000, UsedTokens: 1000, UsedPercentage: 0.5, RemainingPercentage: 99.5,
            CostUsd: null, PayloadVersion: null, ReceivedAtUtc: DateTime.UtcNow, WaitingSinceUtc: waitingSinceUtc));
        if (!live)
        {
            state.MarkSessionEnded(sessionId);
        }

        if (withLiveAgent)
        {
            state.UpdateAgentRecord(new AgentRecord(
                AgentId: $"agent-{Guid.NewGuid():N}", AgentType: "general-purpose", ParentSessionId: sessionId,
                ModelId: "claude-sonnet-5", EffortLevel: "medium", InputTokens: 10, OutputTokens: 5,
                CacheCreationInputTokens: 0, CacheReadInputTokens: 0, ContextWindowSize: 200_000,
                Status: AgentStatus.Live, ReceivedAtUtc: DateTime.UtcNow, Source: "subagentStatusLine"));
        }

        var tree = new RootsTreeBuilder().Build(new[] { @"C:\projects" }, state, _root);
        return tree.Roots.Single().Sessions.Single(s => s.SessionId == sessionId);
    }

    [Fact]
    public void ALiveSessionMidTurn_IsReportedWorking()
    {
        Assert.Equal("working", BuildSession(new[] { Prompt(), Assistant("tool_use") }, live: true).Activity);
    }

    [Fact]
    public void ALiveSessionBetweenTurns_IsReportedIdle()
    {
        Assert.Equal("idle", BuildSession(new[] { Prompt(), Assistant("end_turn"), System("turn_duration") }, live: true).Activity);
    }

    [Fact]
    public void ALiveSessionWithALiveSubAgent_IsReportedWorking_EvenWhenItsOwnTranscriptIsIdle()
    {
        Assert.Equal("working", BuildSession(new[] { Prompt(), Assistant("end_turn") }, live: true, withLiveAgent: true).Activity);
    }

    [Fact]
    public void ASessionThatIsNotRunning_IsReportedWithNoActivity()
    {
        Assert.Null(BuildSession(new[] { Prompt(), Assistant("tool_use") }, live: false).Activity);
    }

    [Fact]
    public void TheStopHook_CanEndATurnTheTranscriptStillShowsOpen()
    {
        // Stop fired well after the transcript's last write: the turn is over.
        var session = BuildSession(
            new[] { Prompt(), Assistant("tool_use") }, live: true, waitingSinceUtc: DateTime.UtcNow.AddHours(1));

        Assert.Equal("idle", session.Activity);
    }
}

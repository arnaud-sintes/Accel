using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using Accel.Metrics;
using Accel.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Accel.Tests;

/// <summary>
/// Integration tests for Phase UI-D's <c>GET /roots/tree</c> route: disk enumeration,
/// cwd-based root attribution (longest-root-wins, segment-boundary matching), merging with
/// live <see cref="SessionState"/>, <see cref="ModelWindowTable"/> percentages, unattributed
/// buckets, and per-tick caching. Uses a temp fixture directory standing in for
/// <c>%USERPROFILE%\.claude\projects</c> via <see cref="EventServer.ProjectsDirOverride"/>,
/// mirroring how <c>RootFoldersConfig</c>/<c>RootsRouteTests</c> made the config-loading path
/// testable without touching real filesystem locations.
/// </summary>
public class RootsTreeRouteTests : IAsyncLifetime
{
    private readonly string _fixtureRoot = Path.Combine(Path.GetTempPath(), $"accel-roots-tree-{Guid.NewGuid():N}");
    private EventServer? _server;
    private WebApplication? _app;
    private HttpClient? _client;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_fixtureRoot);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        try { Directory.Delete(_fixtureRoot, recursive: true); } catch { /* best effort cleanup */ }
    }

    private async Task StartServerAsync(string[] roots)
    {
        _server = new EventServer { ProjectsDirOverride = _fixtureRoot };
        // Roots is a get-only property loaded at construction from RootFoldersConfig; use
        // EventServer.Build directly instead so we can inject an explicit roots array,
        // sharing the same RootsTree/State instances the test wants to read/write.
        _app = EventServer.Build(0, roots: roots, state: _server.State, rootsTree: _server.RootsTree, projectsDirOverride: _fixtureRoot);
        await _app.StartAsync();

        var addressesFeature = _app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();

        string address = addressesFeature!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    private string WriteSessionFile(string slug, string sessionId, IEnumerable<string> lines)
    {
        string slugDir = Path.Combine(_fixtureRoot, slug);
        Directory.CreateDirectory(slugDir);
        string path = Path.Combine(slugDir, $"{sessionId}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string ModeLine() =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["type"] = "mode", ["mode"] = "normal" });

    private static string UserLine(string text, string cwd) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["cwd"] = cwd,
            ["message"] = new Dictionary<string, object?> { ["content"] = text },
        });

    private static string AssistantLine(string model, string effort, int input, int output, int cacheCreate = 0, int cacheRead = 0) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "assistant",
            ["message"] = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["usage"] = new Dictionary<string, object?>
                {
                    ["input_tokens"] = input,
                    ["output_tokens"] = output,
                    ["cache_creation_input_tokens"] = cacheCreate,
                    ["cache_read_input_tokens"] = cacheRead,
                },
            },
            ["effort"] = new Dictionary<string, object?> { ["level"] = effort },
        });

    private static string AiTitleLine(string title, string sessionId) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "ai-title",
            ["aiTitle"] = title,
            ["sessionId"] = sessionId,
        });

    private static JsonElement FindSession(JsonElement root, string path, string sessionId)
    {
        foreach (var rootNode in root.GetProperty("roots").EnumerateArray())
        {
            if (rootNode.GetProperty("path").GetString() != path)
            {
                continue;
            }

            foreach (var session in rootNode.GetProperty("sessions").EnumerateArray())
            {
                if (session.GetProperty("session_id").GetString() == sessionId)
                {
                    return session;
                }
            }
        }

        throw new InvalidOperationException($"session {sessionId} not found under root {path}");
    }

    [Fact]
    public async Task HistoricalSession_WithMatchingCwd_AttributedWithTranscriptFields()
    {
        string sessionId = $"session-{Guid.NewGuid():N}";
        WriteSessionFile("C--projects", sessionId, new[]
        {
            ModeLine(),
            UserLine("Audit the UI design doc please", @"C:\projects"),
            AssistantLine("claude-sonnet-5", "medium", 1000, 200, 50, 25),
        });

        await StartServerAsync(new[] { @"C:\projects" });

        var response = await _client!.GetAsync("/roots/tree");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var session = FindSession(doc.RootElement, @"C:\projects", sessionId);

        Assert.Equal("Audit the UI design doc please", session.GetProperty("name").GetString());
        Assert.Equal("first_message", session.GetProperty("name_source").GetString());
        Assert.False(session.GetProperty("is_live").GetBoolean());
        Assert.Equal("ended", session.GetProperty("status").GetString());
        Assert.Equal("transcript", session.GetProperty("source").GetString());
        Assert.Equal("claude-sonnet-5", session.GetProperty("model_id").GetString());
        Assert.Equal("medium", session.GetProperty("effort_level").GetString());
        Assert.True(session.GetProperty("context_window_size_assumed").GetBoolean());

        long window = session.GetProperty("context_window_size").GetInt64();
        double expectedPct = Math.Round((1000 + 50 + 25) / (double)window * 100.0, 1);
        Assert.Equal(expectedPct, session.GetProperty("used_percentage").GetDouble());
        Assert.Equal(0, session.GetProperty("agents").GetArrayLength());
    }

    [Fact]
    public async Task HistoricalSession_ClaudeSonnet5_RealisticHighUsage_DoesNotExceed100Percent()
    {
        // Regression for the >100% used_percentage bug (observed up to 204% on real historical
        // sessions on disk): 331,424 input+cache tokens is a REAL value seen in this project's
        // own actual transcript history for claude-sonnet-5 - under the old 200,000-token
        // placeholder default this would have reported 165.7%. With the fix (claude-sonnet-5
        // resolving to the verified 1,000,000 window - see ModelWindowTable.cs), it must stay
        // at or under 100%.
        string sessionId = $"session-{Guid.NewGuid():N}";
        WriteSessionFile("C--projects", sessionId, new[]
        {
            ModeLine(),
            UserLine("Investigate the two real Accel bugs", @"C:\projects"),
            AssistantLine("claude-sonnet-5", "medium", 2, 500, 4099, 325191),
        });

        await StartServerAsync(new[] { @"C:\projects" });

        var response = await _client!.GetAsync("/roots/tree");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var session = FindSession(doc.RootElement, @"C:\projects", sessionId);

        Assert.Equal(1_000_000, session.GetProperty("context_window_size").GetInt64());
        Assert.True(session.GetProperty("context_window_size_assumed").GetBoolean());

        double usedPercentage = session.GetProperty("used_percentage").GetDouble();
        Assert.True(usedPercentage <= 100.0, $"used_percentage should never exceed 100% with the fixed window, was {usedPercentage}");
        Assert.Equal(32.9, usedPercentage);
    }

    [Fact]
    public async Task LiveSession_FromSessionState_AttributedWithStatusLineFields()
    {
        string sessionId = $"session-{Guid.NewGuid():N}";
        WriteSessionFile("C--projects", sessionId, new[]
        {
            ModeLine(),
            UserLine("hello", @"C:\projects"),
        });

        await StartServerAsync(new[] { @"C:\projects" });

        _server!.State.UpdateSessionSnapshot(new SessionSnapshot(
            SessionId: sessionId,
            ModelId: "claude-opus-5[1m]",
            ModelDisplayName: "Opus 5",
            EffortLevel: "high",
            ContextWindowSize: 1_000_000,
            UsedTokens: 148_223,
            UsedPercentage: 14.8,
            RemainingPercentage: 85.2,
            CostUsd: null,
            PayloadVersion: "2.1.224",
            ReceivedAtUtc: DateTime.UtcNow,
            SessionName: "Build session monitoring application"));

        var response = await _client!.GetAsync("/roots/tree");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var session = FindSession(doc.RootElement, @"C:\projects", sessionId);

        Assert.Equal("Build session monitoring application", session.GetProperty("name").GetString());
        Assert.Equal("status_line", session.GetProperty("name_source").GetString());
        Assert.True(session.GetProperty("is_live").GetBoolean());
        Assert.Equal("live", session.GetProperty("status").GetString());
        Assert.Equal("statusLine", session.GetProperty("source").GetString());
        Assert.Equal("Opus 5", session.GetProperty("model_display_name").GetString());
        Assert.Equal(1_000_000, session.GetProperty("context_window_size").GetInt64());
        Assert.False(session.GetProperty("context_window_size_assumed").GetBoolean());
        Assert.Equal(14.8, session.GetProperty("used_percentage").GetDouble());
    }

    [Fact]
    public async Task LiveSession_WithLiveSubagent_NestsOnlyLiveAgent()
    {
        string sessionId = $"session-{Guid.NewGuid():N}";
        WriteSessionFile("C--projects", sessionId, new[] { ModeLine(), UserLine("hi", @"C:\projects") });

        await StartServerAsync(new[] { @"C:\projects" });

        _server!.State.UpdateSessionSnapshot(new SessionSnapshot(
            SessionId: sessionId, ModelId: "claude-opus-5", ModelDisplayName: "Opus", EffortLevel: "high",
            ContextWindowSize: 200_000, UsedTokens: 1000, UsedPercentage: 0.5, RemainingPercentage: 99.5,
            CostUsd: null, PayloadVersion: null, ReceivedAtUtc: DateTime.UtcNow));

        string liveAgentId = $"agent-{Guid.NewGuid():N}";
        string endedAgentId = $"agent-{Guid.NewGuid():N}";
        string staleAgentId = $"agent-{Guid.NewGuid():N}";

        _server.State.UpdateAgentRecord(new AgentRecord(
            AgentId: liveAgentId, AgentType: "general-purpose", ParentSessionId: sessionId,
            ModelId: "claude-sonnet-5", EffortLevel: "medium", InputTokens: 41200, OutputTokens: 3100,
            CacheCreationInputTokens: 0, CacheReadInputTokens: 0, ContextWindowSize: 200_000,
            Status: AgentStatus.Live, ReceivedAtUtc: DateTime.UtcNow, Source: "subagentStatusLine",
            Name: "Audit project-ui.md"));

        _server.State.UpdateAgentRecord(new AgentRecord(
            AgentId: endedAgentId, AgentType: "general-purpose", ParentSessionId: sessionId,
            ModelId: "claude-sonnet-5", EffortLevel: "medium", InputTokens: 1, OutputTokens: 1,
            CacheCreationInputTokens: 0, CacheReadInputTokens: 0, ContextWindowSize: 200_000,
            Status: AgentStatus.Ended, ReceivedAtUtc: DateTime.UtcNow, Source: "transcript"));

        _server.State.UpdateAgentRecord(new AgentRecord(
            AgentId: staleAgentId, AgentType: "general-purpose", ParentSessionId: sessionId,
            ModelId: "claude-sonnet-5", EffortLevel: "medium", InputTokens: 1, OutputTokens: 1,
            CacheCreationInputTokens: 0, CacheReadInputTokens: 0, ContextWindowSize: 200_000,
            Status: AgentStatus.Stale, ReceivedAtUtc: DateTime.UtcNow, Source: "subagentStatusLine"));

        var response = await _client!.GetAsync("/roots/tree");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var session = FindSession(doc.RootElement, @"C:\projects", sessionId);

        var agents = session.GetProperty("agents").EnumerateArray().ToList();
        Assert.Single(agents);
        Assert.Equal(liveAgentId, agents[0].GetProperty("agent_id").GetString());
        Assert.Equal("Audit project-ui.md", agents[0].GetProperty("name").GetString());
        Assert.Equal("live", agents[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task NestedConfiguredRoots_AttributesToLongestMatch()
    {
        string sessionId = $"session-{Guid.NewGuid():N}";
        WriteSessionFile("C--projects-sub", sessionId, new[]
        {
            ModeLine(),
            UserLine("hi", @"C:\projects\sub"),
        });

        await StartServerAsync(new[] { @"C:\projects", @"C:\projects\sub" });

        var response = await _client!.GetAsync("/roots/tree");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var subRootSession = FindSession(doc.RootElement, @"C:\projects\sub", sessionId);
        Assert.Equal(sessionId, subRootSession.GetProperty("session_id").GetString());

        foreach (var rootNode in doc.RootElement.GetProperty("roots").EnumerateArray())
        {
            if (rootNode.GetProperty("path").GetString() == @"C:\projects")
            {
                Assert.DoesNotContain(
                    rootNode.GetProperty("sessions").EnumerateArray(),
                    s => s.GetProperty("session_id").GetString() == sessionId);
            }
        }
    }

    [Fact]
    public async Task CwdWithHyphenCollision_ExcludedFromNarrowerRoot()
    {
        string sessionId = $"session-{Guid.NewGuid():N}";
        // "C:\projects-foo" collides at the slug level with "C:\projects\foo" but must NOT be
        // treated as a descendant of "C:\projects" - this is the whole point of cwd-based
        // (not slug-based) attribution per project-ui.md.
        WriteSessionFile("C--projects-foo", sessionId, new[]
        {
            ModeLine(),
            UserLine("hi", @"C:\projects-foo"),
        });

        await StartServerAsync(new[] { @"C:\projects" });

        var response = await _client!.GetAsync("/roots/tree");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        foreach (var rootNode in doc.RootElement.GetProperty("roots").EnumerateArray())
        {
            Assert.DoesNotContain(
                rootNode.GetProperty("sessions").EnumerateArray(),
                s => s.GetProperty("session_id").GetString() == sessionId);
        }

        var unattributed = doc.RootElement.GetProperty("unattributed_sessions").EnumerateArray().ToList();
        Assert.Contains(unattributed, s => s.GetProperty("session_id").GetString() == sessionId);
    }

    [Fact]
    public async Task SessionWithNoReadableCwd_LandsInUnattributed_NeverDropped()
    {
        string sessionId = $"session-{Guid.NewGuid():N}";
        // No "cwd" field anywhere in the head window.
        WriteSessionFile("some-slug", sessionId, new[]
        {
            ModeLine(),
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "user",
                ["message"] = new Dictionary<string, object?> { ["content"] = "no cwd here" },
            }),
        });

        await StartServerAsync(new[] { @"C:\projects" });

        var response = await _client!.GetAsync("/roots/tree");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var unattributed = doc.RootElement.GetProperty("unattributed_sessions").EnumerateArray().ToList();
        Assert.Contains(unattributed, s => s.GetProperty("session_id").GetString() == sessionId);
    }

    [Fact]
    public async Task NoRootsConfigured_Returns200WithEmptyRootsArray()
    {
        await StartServerAsync(Array.Empty<string>());

        var response = await _client!.GetAsync("/roots/tree");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(doc.RootElement.GetProperty("roots").EnumerateArray());
    }

    [Fact]
    public async Task LockedFile_DoesNotFailWholeResponse_OtherSessionsStillAppear()
    {
        string lockedSessionId = $"session-{Guid.NewGuid():N}";
        string goodSessionId = $"session-{Guid.NewGuid():N}";

        string lockedPath = WriteSessionFile("C--projects", lockedSessionId, new[]
        {
            ModeLine(),
            UserLine("locked one", @"C:\projects"),
        });
        WriteSessionFile("C--projects", goodSessionId, new[]
        {
            ModeLine(),
            UserLine("good one", @"C:\projects"),
        });

        await StartServerAsync(new[] { @"C:\projects" });

        using var lockStream = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var response = await _client!.GetAsync("/roots/tree");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var goodSession = FindSession(doc.RootElement, @"C:\projects", goodSessionId);
        Assert.Equal("good one", goodSession.GetProperty("name").GetString());
    }

    [Fact]
    public async Task RepeatedCalls_WithoutFileChanges_AreConsistentAndDoNotGrowCache()
    {
        string sessionId = $"session-{Guid.NewGuid():N}";
        WriteSessionFile("C--projects", sessionId, new[]
        {
            ModeLine(),
            UserLine("caching check", @"C:\projects"),
            AssistantLine("claude-sonnet-5", "medium", 100, 20),
        });

        await StartServerAsync(new[] { @"C:\projects" });

        var first = await _client!.GetAsync("/roots/tree");
        using var firstDoc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstSession = FindSession(firstDoc.RootElement, @"C:\projects", sessionId);

        int headCountAfterFirst = _server!.RootsTree.HeadCacheCount;
        int tailCountAfterFirst = _server.RootsTree.TailCacheCount;
        Assert.True(headCountAfterFirst >= 1);
        Assert.True(tailCountAfterFirst >= 1);

        var second = await _client.GetAsync("/roots/tree");
        using var secondDoc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var secondSession = FindSession(secondDoc.RootElement, @"C:\projects", sessionId);

        // Cache size must not grow on a second scan of the same, unmodified file set.
        Assert.Equal(headCountAfterFirst, _server.RootsTree.HeadCacheCount);
        Assert.Equal(tailCountAfterFirst, _server.RootsTree.TailCacheCount);

        // Functional correctness across repeated calls.
        Assert.Equal(firstSession.GetProperty("name").GetString(), secondSession.GetProperty("name").GetString());
        Assert.Equal(
            firstSession.GetProperty("used_percentage").GetDouble(),
            secondSession.GetProperty("used_percentage").GetDouble());
    }

    [Fact]
    public void RootsTreeBuilder_TailCacheKey_InvalidatesOnlyWhenFileChanges()
    {
        var builder = new RootsTreeBuilder();
        string dir = Path.Combine(_fixtureRoot, "cache-key-slug");
        Directory.CreateDirectory(dir);
        string sessionId = $"session-{Guid.NewGuid():N}";
        string path = Path.Combine(dir, $"{sessionId}.jsonl");

        File.WriteAllLines(path, new[] { ModeLine(), UserLine("v1", @"C:\projects"), AssistantLine("claude-sonnet-5", "low", 10, 5) });

        var state = new SessionState();
        var first = builder.Build(new[] { @"C:\projects" }, state, _fixtureRoot);
        var firstSession = first.Roots.Single(r => r.Path == @"C:\projects").Sessions.Single(s => s.SessionId == sessionId);
        Assert.Equal(1, builder.TailCacheCount);

        // Unchanged file: same (length, mtime) key -> cache should still report exactly one entry.
        var second = builder.Build(new[] { @"C:\projects" }, state, _fixtureRoot);
        Assert.Equal(1, builder.TailCacheCount);
        var secondSession = second.Roots.Single(r => r.Path == @"C:\projects").Sessions.Single(s => s.SessionId == sessionId);
        Assert.Equal(firstSession.UsedTokens, secondSession.UsedTokens);

        // Modify content (changes length/mtime) -> still one cache entry (same key overwritten),
        // but the returned data must now reflect the new content, proving the key invalidated.
        Thread.Sleep(50);
        File.WriteAllLines(path, new[] { ModeLine(), UserLine("v1", @"C:\projects"), AssistantLine("claude-sonnet-5", "low", 999, 999) });
        var third = builder.Build(new[] { @"C:\projects" }, state, _fixtureRoot);
        Assert.Equal(1, builder.TailCacheCount);
        var thirdSession = third.Roots.Single(r => r.Path == @"C:\projects").Sessions.Single(s => s.SessionId == sessionId);
        Assert.NotEqual(firstSession.UsedTokens, thirdSession.UsedTokens);
    }

    // ---- P1-T4b: ai-title name source + full resolution order ----

    [Fact]
    public void AiTitlePresent_NoHigherPriorityName_UsedAsSessionName()
    {
        var builder = new RootsTreeBuilder();
        string dir = Path.Combine(_fixtureRoot, "ai-title-slug");
        Directory.CreateDirectory(dir);
        string sessionId = $"session-{Guid.NewGuid():N}";
        string path = Path.Combine(dir, $"{sessionId}.jsonl");

        // No usable first-message text (only wrapper/skip-prefixed lines), so absent an
        // ai-title the name would fall all the way back to the truncated session id.
        File.WriteAllLines(path, new[]
        {
            ModeLine(),
            UserLine("<system-reminder>irrelevant</system-reminder>", @"C:\projects"),
            AiTitleLine("Refactor the roots tree builder", sessionId),
        });

        var state = new SessionState();
        var result = builder.Build(new[] { @"C:\projects" }, state, _fixtureRoot);
        var session = result.Roots.Single(r => r.Path == @"C:\projects").Sessions.Single(s => s.SessionId == sessionId);

        Assert.Equal("Refactor the roots tree builder", session.Name);
        Assert.Equal("ai_title", session.NameSource);
    }

    [Fact]
    public void MultipleAiTitleLines_LastOneWins()
    {
        var builder = new RootsTreeBuilder();
        string dir = Path.Combine(_fixtureRoot, "ai-title-multi-slug");
        Directory.CreateDirectory(dir);
        string sessionId = $"session-{Guid.NewGuid():N}";
        string path = Path.Combine(dir, $"{sessionId}.jsonl");

        File.WriteAllLines(path, new[]
        {
            ModeLine(),
            AiTitleLine("First draft title", sessionId),
            AiTitleLine("Second, updated title", sessionId),
            AiTitleLine("Third and final title", sessionId),
        });

        var state = new SessionState();
        var result = builder.Build(new[] { @"C:\projects" }, state, _fixtureRoot);

        // No cwd anywhere in this fixture, so the session lands in unattributed - only the
        // name resolution behavior is under test here.
        var session = result.UnattributedSessions.Single(s => s.SessionId == sessionId);

        Assert.Equal("Third and final title", session.Name);
        Assert.Equal("ai_title", session.NameSource);
    }

    [Fact]
    public void AccelOverridePresent_WinsOverAiTitle()
    {
        var builder = new RootsTreeBuilder();
        string dir = Path.Combine(_fixtureRoot, "override-slug");
        Directory.CreateDirectory(dir);
        string sessionId = $"session-{Guid.NewGuid():N}";
        string path = Path.Combine(dir, $"{sessionId}.jsonl");

        File.WriteAllLines(path, new[]
        {
            ModeLine(),
            AiTitleLine("Whatever the transcript thinks it's called", sessionId),
        });

        var state = new SessionState();
        var overrides = new Dictionary<string, SessionOverride>
        {
            [sessionId] = new SessionOverride("My Custom Name", Pinned: false, Hidden: false, LastOpenedUtc: null),
        };

        var result = builder.Build(new[] { @"C:\projects" }, state, _fixtureRoot, overrides);
        var session = result.UnattributedSessions.Single(s => s.SessionId == sessionId);

        Assert.Equal("My Custom Name", session.Name);
        Assert.Equal("accel_override", session.NameSource);
    }

    [Fact]
    public void LiveStatusLineName_WinsOverAiTitle_ButOnlyWhileLive()
    {
        var builder = new RootsTreeBuilder();
        string dir = Path.Combine(_fixtureRoot, "live-name-slug");
        Directory.CreateDirectory(dir);
        string sessionId = $"session-{Guid.NewGuid():N}";
        string path = Path.Combine(dir, $"{sessionId}.jsonl");

        File.WriteAllLines(path, new[]
        {
            ModeLine(),
            AiTitleLine("Transcript-derived title", sessionId),
        });

        var state = new SessionState();
        state.UpdateSessionSnapshot(new SessionSnapshot(
            SessionId: sessionId,
            ModelId: "claude-sonnet-5",
            ModelDisplayName: null,
            EffortLevel: null,
            ContextWindowSize: null,
            UsedTokens: null,
            UsedPercentage: null,
            RemainingPercentage: null,
            CostUsd: null,
            PayloadVersion: null,
            ReceivedAtUtc: DateTime.UtcNow,
            SessionName: "Live renamed session"));

        var liveResult = builder.Build(new[] { @"C:\projects" }, state, _fixtureRoot);
        var liveSession = liveResult.UnattributedSessions.Single(s => s.SessionId == sessionId);
        Assert.Equal("Live renamed session", liveSession.Name);
        Assert.Equal("status_line", liveSession.NameSource);

        // Once the session ends, its stale statusLine name must no longer outrank the
        // transcript's own ai-title (tier 2 is gated on "currently running").
        state.MarkSessionEnded(sessionId);
        var builder2 = new RootsTreeBuilder();
        var endedResult = builder2.Build(new[] { @"C:\projects" }, state, _fixtureRoot);
        var endedSession = endedResult.UnattributedSessions.Single(s => s.SessionId == sessionId);
        Assert.Equal("Transcript-derived title", endedSession.Name);
        Assert.Equal("ai_title", endedSession.NameSource);
    }

    [Fact]
    public void AiTitleCache_SharesTailCacheKey_DoesNotReReadOnUnchangedFile()
    {
        var builder = new RootsTreeBuilder();
        string dir = Path.Combine(_fixtureRoot, "ai-title-cache-slug");
        Directory.CreateDirectory(dir);
        string sessionId = $"session-{Guid.NewGuid():N}";
        string path = Path.Combine(dir, $"{sessionId}.jsonl");

        File.WriteAllLines(path, new[] { ModeLine(), AiTitleLine("Original title", sessionId) });

        var state = new SessionState();
        var first = builder.Build(new[] { @"C:\projects" }, state, _fixtureRoot);
        var firstSession = first.UnattributedSessions.Single(s => s.SessionId == sessionId);
        Assert.Equal("Original title", firstSession.Name);
        Assert.Equal(1, builder.TailCacheCount);

        // Unchanged file: same (length, mtime) key -> exactly one cache entry, same name.
        var second = builder.Build(new[] { @"C:\projects" }, state, _fixtureRoot);
        var secondSession = second.UnattributedSessions.Single(s => s.SessionId == sessionId);
        Assert.Equal(1, builder.TailCacheCount);
        Assert.Equal("Original title", secondSession.Name);

        // Change the ai-title content (changes length/mtime) -> still exactly one cache entry
        // (same key overwritten), but the returned name must now reflect the new content,
        // proving the shared cache key actually invalidated rather than being stuck stale.
        Thread.Sleep(50);
        File.WriteAllLines(path, new[] { ModeLine(), AiTitleLine("Updated title", sessionId) });
        var third = builder.Build(new[] { @"C:\projects" }, state, _fixtureRoot);
        var thirdSession = third.UnattributedSessions.Single(s => s.SessionId == sessionId);
        Assert.Equal(1, builder.TailCacheCount);
        Assert.Equal("Updated title", thirdSession.Name);
    }
}

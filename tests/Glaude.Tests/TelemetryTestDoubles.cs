namespace Glaude.Tests;

using System;
using System.Collections.Generic;
using Glaude.App.Services;
using Glaude.Metrics;

/// <summary>
/// Test doubles for the P1-T2 push-feed seam (<see cref="ITelemetryFeed"/> and its two injected
/// mechanisms). All three exist so the feed and panel-A ViewModel can be tested headlessly: no WPF
/// <c>Dispatcher</c>, no wall-clock <c>DispatcherTimer</c>, and - importantly - no real
/// <see cref="System.IO.FileSystemWatcher"/> (the production feed only creates one when its source
/// reports a projects directory, and <see cref="FakeTelemetrySource.ProjectsDirectory"/> is null by
/// default).
/// </summary>
internal sealed class RecordingUiThreadDispatcher : IUiThreadDispatcher
{
    private readonly Queue<Action> _pending = new();

    /// <summary>When true (the default), <see cref="Post"/> runs inline, standing in for "the caller
    /// was already on the UI thread". When false, posts queue up until <see cref="Drain"/> - which is
    /// how the cross-thread marshalling requirement is asserted.</summary>
    public bool RunInline { get; set; } = true;

    public int PostCount { get; private set; }

    public int PendingCount => _pending.Count;

    public bool IsOnUiThread => RunInline;

    public void Post(Action action)
    {
        PostCount++;

        if (RunInline)
        {
            action();
            return;
        }

        _pending.Enqueue(action);
    }

    /// <summary>Runs everything queued, in order (the WPF dispatcher's FIFO guarantee).</summary>
    public void Drain()
    {
        while (_pending.Count > 0)
        {
            _pending.Dequeue()();
        }
    }
}

/// <summary>
/// Stands in for <see cref="DispatcherDebounceTimer"/>: counts restarts/stops (so the
/// <c>DebounceCoalescer</c> contract is observable) and fires only when a test says so, so no test
/// ever waits 250 ms of wall clock.
/// </summary>
internal sealed class FakeDebounceTimer : IDebounceTimer
{
    public int Restarts { get; private set; }

    public int Stops { get; private set; }

    public bool IsRunning { get; private set; }

    public bool Disposed { get; private set; }

    public event Action? Tick;

    public void Restart()
    {
        Restarts++;
        IsRunning = true;
    }

    public void Stop()
    {
        Stops++;
        IsRunning = false;
    }

    /// <summary>Simulates the debounce window elapsing.</summary>
    public void Fire() => Tick?.Invoke();

    public void Dispose() => Disposed = true;
}

/// <summary>In-memory <see cref="ITelemetrySource"/>: an explicit snapshot to hand out (or an
/// exception to throw), a manually raisable <see cref="Changed"/>, and no projects directory so the
/// feed runs watcher-less.</summary>
internal sealed class FakeTelemetrySource : ITelemetrySource
{
    public event Action? Changed;

    public string? ProjectsDirectory { get; set; }

    public RootsTreeDto Snapshot { get; set; } = TelemetryFixtures.EmptyTree();

    public Exception? ThrowOnBuild { get; set; }

    public int BuildCount { get; private set; }

    public RootsTreeDto BuildSnapshot()
    {
        BuildCount++;

        if (ThrowOnBuild is not null)
        {
            throw ThrowOnBuild;
        }

        return Snapshot;
    }

    public void RaiseChanged() => Changed?.Invoke();

    public bool HasSubscribers => Changed is not null;
}

/// <summary>Stands in for the whole feed when testing the ViewModel: tests push snapshots/failures
/// directly, and assert the ViewModel never reaches around it (no <c>SessionState</c>, no watcher,
/// no HTTP).</summary>
internal sealed class FakeTelemetryFeed : ITelemetryFeed
{
    public event Action<RootsTreeDto>? SnapshotAvailable;

    public event Action<string>? SnapshotFailed;

    public RootsTreeDto? Latest { get; set; }

    public int StartCount { get; private set; }

    public int RefreshRequestCount { get; private set; }

    public bool Disposed { get; private set; }

    public void Start() => StartCount++;

    public void RequestRefresh() => RefreshRequestCount++;

    public void Publish(RootsTreeDto snapshot)
    {
        Latest = snapshot;
        SnapshotAvailable?.Invoke(snapshot);
    }

    public void Fail(string message) => SnapshotFailed?.Invoke(message);

    public bool HasSnapshotSubscribers => SnapshotAvailable is not null;

    public void Dispose() => Disposed = true;
}

/// <summary>
/// Fixture builders for the real <see cref="RootsTreeDto"/> shape produced by
/// <see cref="RootsTreeBuilder"/> - same construction style as
/// <c>MonitorTreeBuilderTests</c>/<c>RootsTreeRouteTests</c>, so the ViewModel is exercised against
/// the actual DTO record definitions rather than an invented shape.
/// </summary>
internal static class TelemetryFixtures
{
    public static AgentTreeDto Agent(string agentId, string status = "live", string? name = null) => new(
        AgentId: agentId,
        Name: name,
        AgentType: "general-purpose",
        ModelId: "claude-sonnet-5",
        EffortLevel: "medium",
        InputTokens: 1000,
        OutputTokens: 200,
        CacheCreationInputTokens: 0,
        CacheReadInputTokens: 0,
        ContextWindowSize: 200_000,
        ContextWindowSizeAssumed: true,
        UsedPercentage: 0.5,
        Status: status,
        Source: "subagentStatusLine",
        AsOf: new DateTime(2026, 8, 14, 10, 0, 0, DateTimeKind.Utc));

    public static SessionTreeDto Session(
        string sessionId,
        bool isLive = false,
        AgentTreeDto[]? agents = null,
        string name = "a session") => new(
        SessionId: sessionId,
        Name: name,
        NameSource: "first_message",
        Cwd: @"C:\projects",
        ProjectDir: "C--projects",
        IsLive: isLive,
        Status: isLive ? "live" : "ended",
        ModelId: "claude-sonnet-5",
        ModelDisplayName: isLive ? "Sonnet 5" : null,
        EffortLevel: "medium",
        ContextWindowSize: 1_000_000,
        ContextWindowSizeAssumed: !isLive,
        UsedTokens: 123_456,
        UsedPercentage: 12.3,
        Source: isLive ? "statusLine" : "transcript",
        AsOf: new DateTime(2026, 8, 14, 10, 0, 0, DateTimeKind.Utc),
        LastActivityUtc: new DateTime(2026, 8, 14, 10, 0, 0, DateTimeKind.Utc),
        Agents: agents ?? Array.Empty<AgentTreeDto>());

    public static RootTreeDto Root(string path, params SessionTreeDto[] sessions) =>
        new(Path: path, Exists: true, Sessions: sessions);

    public static RootsTreeDto Tree(
        RootTreeDto[]? roots = null,
        SessionTreeDto[]? unattributedSessions = null,
        AgentTreeDto[]? unattributedAgents = null,
        DateTime? generatedAtUtc = null) => new(
        Roots: roots ?? Array.Empty<RootTreeDto>(),
        UnattributedSessions: unattributedSessions ?? Array.Empty<SessionTreeDto>(),
        UnattributedAgents: unattributedAgents ?? Array.Empty<AgentTreeDto>(),
        GeneratedAtUtc: generatedAtUtc ?? new DateTime(2026, 8, 14, 10, 0, 5, DateTimeKind.Utc),
        ScanMs: 7);

    public static RootsTreeDto EmptyTree() => Tree();
}

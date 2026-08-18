namespace Accel.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Accel.App.Services;
using Accel.Cli;
using Accel.Metrics;

/// <summary>
/// Panel E's ViewModel (design doc "claude-agentgraph.md" §7.1): a second reader on the same
/// <see cref="ITelemetryFeed"/> panel A uses, plus the same read-only <see cref="ISessionSelectionService"/>
/// - never a filtered view of <see cref="RootsPanelViewModel"/>'s tree. Panel A's node objects are
/// thrown away and rebuilt wholesale on every telemetry tick (locked-in decision 8: no point-to-point
/// panel bindings), so a panel-E design that held a reference into panel A's tree would hold a
/// reference that goes stale ~250ms later - this class instead re-projects its own
/// <see cref="MonitorTree"/> straight from the same <see cref="RootsTreeDto"/> snapshot.
///
/// <para><b>No direct event wiring</b>, same contract as <see cref="RootsPanelViewModel"/>: never
/// touches <see cref="SessionState"/>, a <c>FileSystemWatcher</c>, a timer, or <c>HttpClient</c> -
/// only whole <see cref="RootsTreeDto"/> snapshots from the feed, marshalled through
/// <see cref="IUiThreadDispatcher"/>. Read-only consumer of <see cref="ISessionSelectionService"/>:
/// panel C's <see cref="TabsViewModel"/> remains the sole writer.</para>
/// </summary>
public sealed partial class AgentGraphViewModel : ObservableObject, IDisposable
{
    private readonly ITelemetryFeed _feed;
    private readonly IUiThreadDispatcher _dispatcher;
    private readonly ISessionSelectionService? _selection;

    private RootsTreeDto? _latest;
    private bool _disposed;

    // A freshly created session has no transcript file yet - Claude Code only writes one after its
    // first turn - so MonitorTreeBuilder.Build simply has no row for it for a real, brief window.
    // Without this, that window looked identical to "the session existed and then vanished," which
    // is a different (and much more alarming) situation. Tracking every focused id this instance has
    // actually seen once lets the two be told apart.
    private readonly HashSet<string> _everSeenSessionIds = new(StringComparer.Ordinal);

    public AgentGraphViewModel(
        ITelemetryFeed feed,
        IUiThreadDispatcher dispatcher,
        ISessionSelectionService? selection = null)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _selection = selection;

        _feed.SnapshotAvailable += OnSnapshotAvailable;
        _feed.SnapshotFailed += OnSnapshotFailed;

        _selection?.Subscribe(this, OnFocusedSessionChanged);

        if (_feed.Latest is { } latest)
        {
            Rebuild(latest);
        }
    }

    /// <summary>Parent first, then its live agents in <see cref="MonitorSessionNode.Agents"/> order -
    /// the same order <see cref="AgentGraphLayout.Compute"/> indexes into.</summary>
    public ObservableCollection<AgentGraphNodeViewModel> Nodes { get; } = new();

    /// <summary>Same name/role as <see cref="RootsPanelViewModel.StatusText"/>, initial value
    /// verbatim - panel E's header caption.</summary>
    [ObservableProperty]
    private string _statusText = "Waiting for telemetry…";

    /// <summary>A focused session node was found in the latest snapshot - drives the canvas's
    /// <c>Visibility</c>.</summary>
    [ObservableProperty]
    private bool _hasGraph;

    /// <summary>The focused session has at least one live agent - hides the "no sub-agents" hint.</summary>
    [ObservableProperty]
    private bool _hasAgents;

    /// <summary>The full-tree-then-project rebuild. Public so tests can drive it directly with a
    /// fixture <see cref="RootsTreeDto"/>, exactly as <see cref="RootsPanelViewModel.Rebuild"/> is.</summary>
    public void Rebuild(RootsTreeDto? snapshot)
    {
        _latest = snapshot;

        if (snapshot is null)
        {
            Nodes.Clear();
            HasGraph = false;
            HasAgents = false;
            StatusText = "Waiting for telemetry…";
            return;
        }

        var tree = MonitorTreeBuilder.Build(snapshot);
        var focusedId = _selection?.FocusedSessionId;

        if (string.IsNullOrEmpty(focusedId))
        {
            Nodes.Clear();
            HasGraph = false;
            HasAgents = false;
            StatusText = "No session focused — select a session in the tab strip to see its agent graph.";
            return;
        }

        var session = FindFocusedSession(tree, focusedId);
        if (session is null)
        {
            Nodes.Clear();
            HasGraph = false;
            HasAgents = false;
            StatusText = _everSeenSessionIds.Contains(focusedId)
                ? $"Session {TruncateId(focusedId)} is no longer in the tree."
                : "Waiting for session to start…";
            return;
        }

        _everSeenSessionIds.Add(focusedId);
        ProjectNodes(session);
        HasGraph = true;
        HasAgents = session.Agents.Length > 0;
        StatusText = session.State == MonitorNodeState.Live
            ? string.Empty
            : "Session ended — showing last known state.";
    }

    private void ProjectNodes(MonitorSessionNode session)
    {
        Nodes.Clear();

        var parent = new AgentGraphNodeViewModel(
            session.SessionId,
            AgentGraphNodeRole.Parent,
            session.State,
            session.Columns,
            session.DurationMs,
            session.ConsumedTokens,
            isFocused: IsFocused(session.SessionId),
            consumedTokensIsContextOnly: true);
        Nodes.Add(parent);

        foreach (var agent in session.Agents)
        {
            Nodes.Add(new AgentGraphNodeViewModel(
                agent.AgentId,
                AgentGraphNodeRole.Child,
                agent.State,
                agent.Columns,
                agent.DurationMs,
                agent.ConsumedTokens,
                isFocused: false,
                consumedTokensIsContextOnly: false,
                parentName: parent.DisplayName));
        }
    }

    /// <summary>Matches <see cref="MonitorTreeBuilder"/>'s own 12-char session-id truncation
    /// convention (that helper is private, so this mirrors it rather than reaching in).</summary>
    private const int IdTruncateLength = 12;

    private static string TruncateId(string id) => id.Length <= IdTruncateLength ? id : id[..IdTruncateLength];

    private bool IsFocused(string key) =>
        _selection is not null && !string.IsNullOrEmpty(key) && _selection.IsFocused(key);

    private static MonitorSessionNode? FindFocusedSession(MonitorTree tree, string focusedId) =>
        EnumerateSessions(tree).FirstOrDefault(s => string.Equals(s.SessionId, focusedId, StringComparison.OrdinalIgnoreCase));

    private static System.Collections.Generic.IEnumerable<MonitorSessionNode> EnumerateSessions(MonitorTree tree) =>
        tree.Roots.SelectMany(r => r.Sessions).Concat(tree.Unattributed?.Sessions ?? Array.Empty<MonitorSessionNode>());

    private void OnSnapshotAvailable(RootsTreeDto snapshot) => _dispatcher.Post(() =>
    {
        if (!_disposed)
        {
            Rebuild(snapshot);
        }
    });

    private void OnSnapshotFailed(string message) => _dispatcher.Post(() =>
    {
        if (!_disposed)
        {
            StatusText = $"Refresh failed: {message}";
        }
    });

    /// <summary>A focus change with no new telemetry must still re-target the graph - re-projecting
    /// the cached snapshot is the whole cost (design doc §7.1's third change-signal row).</summary>
    private void OnFocusedSessionChanged(FocusedSessionChangedMessage message) => _dispatcher.Post(() =>
    {
        if (!_disposed)
        {
            Rebuild(_latest);
        }
    });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _feed.SnapshotAvailable -= OnSnapshotAvailable;
        _feed.SnapshotFailed -= OnSnapshotFailed;
        _selection?.Unsubscribe(this);
    }
}

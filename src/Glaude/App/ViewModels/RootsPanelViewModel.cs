namespace Glaude.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Glaude.App.Services;
using Glaude.Cli;
using Glaude.Metrics;

/// <summary>What a <see cref="RootsPanelNodeViewModel"/> represents - the WPF equivalent of the
/// three <c>Build*TreeNode</c> helpers in <c>MonitorForm</c>, plus the "(no sessions)" placeholder
/// row it synthesizes for an empty root.</summary>
public enum RootsPanelNodeKind
{
    Root,
    Session,
    Agent,
    Placeholder,
}

/// <summary>
/// One row in panel A's tree. Shape and content come straight from the pure
/// <see cref="MonitorTreeBuilder"/> DTOs (which in turn come from
/// <see cref="RootsTreeBuilder"/>'s <see cref="RootsTreeDto"/>), so the WPF panel and the WinForms
/// window render the same data with the same formatting until CX-T1 retires the latter.
///
/// <para><see cref="Key"/> is the <b>stable</b> identity used to preserve expansion and selection
/// across the full rebuild each telemetry tick performs: root path / session id / agent id, exactly
/// the keys <c>MonitorForm</c> stores in <c>TreeNode.Tag</c> and
/// <see cref="MonitorTreeExpansion"/> matches on. Placeholder rows have an empty key and are
/// therefore never matched - the same rule <see cref="MonitorTreeExpansion"/> applies to degraded
/// nodes.</para>
/// </summary>
public sealed partial class RootsPanelNodeViewModel : ObservableObject
{
    private readonly RootsPanelViewModel? _owner;

    public RootsPanelNodeViewModel(
        string key,
        string text,
        RootsPanelNodeKind kind,
        MonitorNodeState state,
        MonitorRowColumns columns,
        RootsPanelViewModel? owner = null)
    {
        Key = key ?? string.Empty;
        Text = text ?? string.Empty;
        Kind = kind;
        State = state;
        Columns = columns ?? MonitorRowColumns.Empty;
        _owner = owner;
    }

    /// <summary>Stable id (root path / session id / agent id); empty for placeholder rows.</summary>
    public string Key { get; }

    /// <summary>The single-line label, identical to the WinForms row text.</summary>
    public string Text { get; }

    public RootsPanelNodeKind Kind { get; }

    /// <summary>Live/Historical/Stale - P1-T4 turns this into glyphs/weights; P1-T2 only carries it.</summary>
    public MonitorNodeState State { get; }

    /// <summary>The six-column projection (ID | Name | Type | Model | Effort | Context).</summary>
    public MonitorRowColumns Columns { get; }

    public ObservableCollection<RootsPanelNodeViewModel> Children { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _owner?.OnNodeSelectionChanged(this, value);

    public override string ToString() => Text;
}

/// <summary>
/// Panel A's ViewModel (P1-T2): a read-only tree of every configured root, the sessions found under
/// it, and each live session's live sub-agents - fed exclusively by <see cref="ITelemetryFeed"/>.
///
/// <para><b>No direct event wiring:</b> this class never touches <see cref="SessionState.Changed"/>,
/// a <c>FileSystemWatcher</c>, a timer, or <c>HttpClient</c>. It receives whole
/// <see cref="RootsTreeDto"/> snapshots from the feed (already debounced at 250 ms and already
/// marshalled onto the UI thread) and additionally posts its own handler through the same
/// <see cref="IUiThreadDispatcher"/> so that even a feed double that publishes off-thread cannot
/// mutate the <see cref="ObservableCollection{T}"/>s from the wrong thread.</para>
///
/// <para><b>Rebuild semantics are <c>MonitorForm.RenderTree</c>'s, not an approximation of them:</b>
/// each snapshot is turned into a <c>MonitorTree</c> by the same
/// <see cref="MonitorTreeBuilder.Build"/>; the currently-expanded stable keys and the selected
/// stable key are captured <i>before</i> the collections are cleared; the rebuilt tree's
/// re-expansion set is computed by the same pure
/// <see cref="MonitorTreeExpansion.ComputeKeysToExpand"/> unioned with
/// <see cref="MonitorTreeExpansion.ComputeDefaultExpansionForNewKeys"/> against the same
/// ever-seen-keys set (so a first-time-seen live node auto-expands exactly once, and a user's
/// deliberate collapse of a still-live session is never refought); and the previous selection is
/// re-applied by key lookup, silently dropped if that node no longer exists. Scroll position is the
/// one thing not restored: WPF's <c>TreeView</c> has no <c>TopNode</c> equivalent to key on, and
/// P1-T4 owns panel A's presentation.</para>
/// </summary>
public sealed partial class RootsPanelViewModel : ObservableObject, IDisposable
{
    private readonly ITelemetryFeed _feed;
    private readonly IUiThreadDispatcher _dispatcher;

    /// <summary>Every stable key this ViewModel instance has ever rendered - the exact role of
    /// <c>MonitorForm._everSeenKeys</c>, including its "only ever grows, one instance per window"
    /// lifetime.</summary>
    private readonly HashSet<string> _everSeenKeys = new(StringComparer.Ordinal);

    /// <summary>Set while <see cref="Rebuild"/> mutates the tree, so the node-driven selection
    /// tracking below doesn't mistake "the old node was thrown away" for "the user changed the
    /// selection" and lose the key we are about to restore.</summary>
    private bool _rebuilding;

    private bool _disposed;

    public RootsPanelViewModel(ITelemetryFeed feed, IUiThreadDispatcher dispatcher)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        _feed.SnapshotAvailable += OnSnapshotAvailable;
        _feed.SnapshotFailed += OnSnapshotFailed;

        // A feed that already has a snapshot (e.g. Start() was called before this panel was
        // constructed) must not leave the panel blank until the next change signal.
        if (_feed.Latest is { } latest)
        {
            Rebuild(latest);
        }
    }

    /// <summary>The root-level rows: every configured root, in config order, plus the synthetic
    /// "(unattributed)" node last when it exists - same order as <c>MonitorForm.RenderTree</c>.</summary>
    public ObservableCollection<RootsPanelNodeViewModel> Roots { get; } = new();

    /// <summary>Human-readable feed status, mirroring <c>MonitorForm</c>'s status strip (including
    /// its "Refresh failed: …" text on a failed rebuild) plus the counts that make an
    /// empty-but-working tree distinguishable from a broken one.</summary>
    [ObservableProperty]
    private string _statusText = "Waiting for telemetry…";

    /// <summary>Stable key of the currently selected node (root path / session id / agent id), or
    /// null. Preserved across rebuilds. A future <c>ISessionSelectionService</c> (P3-T1) becomes the
    /// authority for the focused session; this property is only panel A's own tree selection.</summary>
    [ObservableProperty]
    private string? _selectedKey;

    /// <summary>Number of configured roots in the last snapshot.</summary>
    [ObservableProperty]
    private int _rootCount;

    /// <summary>Total sessions across all roots (plus unattributed) in the last snapshot.</summary>
    [ObservableProperty]
    private int _sessionCount;

    /// <summary>How many of <see cref="SessionCount"/> are live.</summary>
    [ObservableProperty]
    private int _liveSessionCount;

    /// <summary>True once at least one snapshot has been rendered - lets the view distinguish
    /// "no data yet" from "data arrived and there genuinely are no sessions".</summary>
    [ObservableProperty]
    private bool _hasSnapshot;

    /// <summary>Starts the feed (idempotent) - separate from the constructor so a host can build the
    /// whole panel graph before any telemetry starts flowing.</summary>
    public void Start() => _feed.Start();

    /// <summary>Manual refresh: goes through the feed's debounce window like any other signal, so
    /// there is still exactly one throttling mechanism.</summary>
    [RelayCommand]
    private void Refresh() => _feed.RequestRefresh();

    /// <summary>Collapses every node (a cheap user affordance that also exercises the
    /// expansion-preservation path: the collapsed state survives the next rebuild).</summary>
    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var node in EnumerateAll(Roots))
        {
            node.IsExpanded = false;
        }
    }

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

    /// <summary>
    /// The full-tree rebuild. Public so tests can drive it directly with a fixture
    /// <see cref="RootsTreeDto"/>, exactly as <c>MonitorTreeBuilderTests</c> drives the pure
    /// builder.
    /// </summary>
    public void Rebuild(RootsTreeDto? snapshot)
    {
        var tree = MonitorTreeBuilder.Build(snapshot);

        _rebuilding = true;
        try
        {
            // Capture BEFORE clearing - the old node objects are the only place the current
            // expand/selection state lives (MonitorForm.RenderTree captures in the same order).
            var expandedKeys = CaptureExpandedKeys();
            string? selectedKey = SelectedKey;

            Roots.Clear();

            foreach (var root in tree.Roots)
            {
                Roots.Add(BuildRootNode(root));
            }

            if (tree.Unattributed is not null)
            {
                Roots.Add(BuildRootNode(tree.Unattributed));
            }

            var keysToExpand = MonitorTreeExpansion.ComputeKeysToExpand(tree, expandedKeys);
            keysToExpand.UnionWith(MonitorTreeExpansion.ComputeDefaultExpansionForNewKeys(tree, _everSeenKeys));
            ApplyExpansion(Roots, keysToExpand);

            _everSeenKeys.UnionWith(MonitorTreeExpansion.CollectAllKeys(tree));

            if (!string.IsNullOrEmpty(selectedKey))
            {
                var selectedNode = FindByKey(Roots, selectedKey!);
                if (selectedNode is not null)
                {
                    selectedNode.IsSelected = true;
                }
                else
                {
                    // The previously selected node is gone from this snapshot (session filtered
                    // out, agent no longer live) - drop the selection rather than pointing at a
                    // node that no longer exists. MonitorForm's equivalent is simply not finding
                    // the key and leaving SelectedNode null.
                    SelectedKey = null;
                }
            }
        }
        finally
        {
            _rebuilding = false;
        }

        UpdateCounters(snapshot);
    }

    private void UpdateCounters(RootsTreeDto? snapshot)
    {
        var rootDtos = snapshot?.Roots ?? Array.Empty<RootTreeDto>();
        var unattributed = snapshot?.UnattributedSessions ?? Array.Empty<SessionTreeDto>();

        var allSessions = rootDtos.SelectMany(r => r.Sessions ?? Array.Empty<SessionTreeDto>()).Concat(unattributed).ToArray();

        RootCount = rootDtos.Length;
        SessionCount = allSessions.Length;
        LiveSessionCount = allSessions.Count(s => s.IsLive);
        HasSnapshot = snapshot is not null;

        if (snapshot is null)
        {
            StatusText = "Waiting for telemetry…";
            return;
        }

        string asOf = snapshot.GeneratedAtUtc.ToString("u", CultureInfo.InvariantCulture);
        StatusText = string.Create(
            CultureInfo.InvariantCulture,
            $"{RootCount} root(s), {SessionCount} session(s), {LiveSessionCount} running — live state as of {asOf}; sessions started before this window opened are shown as historical");
    }

    /// <summary>Called by a node whose <c>IsSelected</c> changed (the WPF <c>TreeViewItem</c> is
    /// bound two-way to it), so <see cref="SelectedKey"/> tracks the user's selection without the
    /// ViewModel needing a reference to the <c>TreeView</c> control. Ignored during a rebuild,
    /// where selection is being restored rather than chosen.</summary>
    internal void OnNodeSelectionChanged(RootsPanelNodeViewModel node, bool isSelected)
    {
        if (_rebuilding)
        {
            if (isSelected)
            {
                SelectedKey = string.IsNullOrEmpty(node.Key) ? null : node.Key;
            }

            return;
        }

        if (isSelected)
        {
            SelectedKey = string.IsNullOrEmpty(node.Key) ? null : node.Key;
        }
        else if (SelectedKey == node.Key)
        {
            SelectedKey = null;
        }
    }

    private HashSet<string> CaptureExpandedKeys()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in EnumerateAll(Roots))
        {
            if (node.IsExpanded && !string.IsNullOrEmpty(node.Key))
            {
                result.Add(node.Key);
            }
        }

        return result;
    }

    private static void ApplyExpansion(IEnumerable<RootsPanelNodeViewModel> nodes, IReadOnlySet<string> keysToExpand)
    {
        foreach (var node in EnumerateAll(nodes))
        {
            if (!string.IsNullOrEmpty(node.Key) && keysToExpand.Contains(node.Key))
            {
                node.IsExpanded = true;
            }
        }
    }

    private static RootsPanelNodeViewModel? FindByKey(IEnumerable<RootsPanelNodeViewModel> nodes, string key) =>
        EnumerateAll(nodes).FirstOrDefault(n => n.Key == key);

    private static IEnumerable<RootsPanelNodeViewModel> EnumerateAll(IEnumerable<RootsPanelNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;

            foreach (var child in EnumerateAll(node.Children))
            {
                yield return child;
            }
        }
    }

    private RootsPanelNodeViewModel BuildRootNode(MonitorRootNode root)
    {
        var node = new RootsPanelNodeViewModel(root.Path, root.Text, RootsPanelNodeKind.Root, MonitorNodeState.Historical, root.Columns, this);

        if (root.Sessions.Length == 0 && root.OrphanAgents.Length == 0)
        {
            // Same single placeholder child MonitorForm.BuildRootTreeNode adds for an empty root -
            // deliberately keyless so it never participates in expansion/selection matching.
            string placeholder = MonitorTreeBuilder.NoSessionsPlaceholder();
            var placeholderColumns = new MonitorRowColumns(string.Empty, placeholder, string.Empty, string.Empty, string.Empty, string.Empty);
            node.Children.Add(new RootsPanelNodeViewModel(string.Empty, placeholder, RootsPanelNodeKind.Placeholder, MonitorNodeState.Historical, placeholderColumns, this));
        }
        else
        {
            foreach (var session in root.Sessions)
            {
                node.Children.Add(BuildSessionNode(session));
            }

            foreach (var agent in root.OrphanAgents)
            {
                node.Children.Add(BuildAgentNode(agent));
            }
        }

        return node;
    }

    private RootsPanelNodeViewModel BuildSessionNode(MonitorSessionNode session)
    {
        var node = new RootsPanelNodeViewModel(session.SessionId, session.Text, RootsPanelNodeKind.Session, session.State, session.Columns, this);

        foreach (var agent in session.Agents)
        {
            node.Children.Add(BuildAgentNode(agent));
        }

        return node;
    }

    private RootsPanelNodeViewModel BuildAgentNode(MonitorAgentNode agent) =>
        new(agent.AgentId, agent.Text, RootsPanelNodeKind.Agent, agent.State, agent.Columns, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _feed.SnapshotAvailable -= OnSnapshotAvailable;
        _feed.SnapshotFailed -= OnSnapshotFailed;
    }
}

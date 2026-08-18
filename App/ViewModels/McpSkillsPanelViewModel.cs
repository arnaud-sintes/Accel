namespace Accel.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Accel.App.Services;
using Accel.Metrics;

/// <summary>One row in panel A's "MCP"/"SKILLS" mini-lists: a tool (or skill) name plus how many
/// times the focused session has invoked it. Plain and immutable, same shape as
/// <see cref="GitPanelEntryViewModel"/> - rows are never mutated in place, they are rebuilt
/// wholesale by <see cref="McpSkillsPanelViewModel.Rebuild"/>.</summary>
public sealed class ToolUsageRowViewModel
{
    public ToolUsageRowViewModel(string name, int hitCount)
    {
        Name = name ?? string.Empty;
        HitCount = hitCount;
    }

    /// <summary>Display name - an MCP tool with its <c>mcp__</c> prefix already stripped by
    /// <see cref="Accel.Metrics.MetricsPipeline"/>, or a skill's own name.</summary>
    public string Name { get; }

    /// <summary>How many <c>PostToolUse</c> hits Accel observed for <see cref="Name"/> in the
    /// focused session. Only counts calls made while Accel was running (see
    /// <see cref="RootsTreeBuilder"/>: historical sessions report empty usage arrays).</summary>
    public int HitCount { get; }

    /// <summary>Never count-badge-only: the row's tooltip/automation name spells the same number
    /// out as words, the accessibility rule the git status badge follows too.</summary>
    public string AutomationDescription => $"{Name}: {HitCount} call(s).";
}

/// <summary>
/// Panel A's bottom section: the focused session's MCP-tool and Skill hit counts, as two flat
/// lists.
///
/// <para>A third independent reader of the same <see cref="ITelemetryFeed"/> /
/// <see cref="ISessionSelectionService"/> pair <see cref="AgentGraphViewModel"/> and
/// <see cref="GitPanelViewModel"/> use - never a filtered view of <see cref="RootsPanelViewModel"/>'s
/// tree, following the same rule panel B's two sections already follow (each section owns its
/// ViewModel; a ViewModel never reaches into another panel's).</para>
///
/// <para>All the data is already on the pushed <see cref="SessionTreeDto"/>
/// (<see cref="SessionTreeDto.McpUsage"/>/<see cref="SessionTreeDto.SkillUsage"/>, populated by
/// <see cref="RootsTreeBuilder"/>), so a rebuild is a lookup plus a clear-and-repopulate of the two
/// collections - the same non-in-place refresh <see cref="GitPanelViewModel"/> does, and no I/O of
/// its own.</para>
/// </summary>
public sealed partial class McpSkillsPanelViewModel : ObservableObject, IDisposable
{
    private readonly ITelemetryFeed _feed;
    private readonly IUiThreadDispatcher _dispatcher;
    private readonly ISessionSelectionService? _selection;

    private RootsTreeDto? _latest;
    private bool _disposed;

    public McpSkillsPanelViewModel(
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

        Rebuild(_feed.Latest);
    }

    /// <summary>MCP tool calls for the focused session, most-used first (count descending, then
    /// name) - the order <see cref="RootsTreeBuilder"/> already emits, re-applied here so the list
    /// is correct regardless of the producer's ordering.</summary>
    public ObservableCollection<ToolUsageRowViewModel> McpUsage { get; } = new();

    /// <summary>Skill invocations for the focused session - see <see cref="McpUsage"/>.</summary>
    public ObservableCollection<ToolUsageRowViewModel> SkillUsage { get; } = new();

    /// <summary>Caption for both mini-panels: "nothing focused" / a failure hint / empty when a
    /// focused session's counts are being shown, same role as
    /// <see cref="GitPanelViewModel.StatusText"/>.</summary>
    [ObservableProperty]
    private string _statusText = "No session focused.";

    /// <summary>The full rebuild. Public so tests can drive it directly with a fixture
    /// <see cref="RootsTreeDto"/>, exactly as <see cref="GitPanelViewModel.Rebuild"/> is.</summary>
    public void Rebuild(RootsTreeDto? snapshot)
    {
        _latest = snapshot;

        McpUsage.Clear();
        SkillUsage.Clear();

        string? focusedId = _selection?.FocusedSessionId;

        if (string.IsNullOrEmpty(focusedId))
        {
            StatusText = "No session focused.";
            return;
        }

        var session = FindSession(snapshot, focusedId);
        if (session is null)
        {
            StatusText = "Waiting for session…";
            return;
        }

        Fill(McpUsage, session.McpUsage);
        Fill(SkillUsage, session.SkillUsage);
        StatusText = string.Empty;
    }

    /// <summary>Null (a historical session, or a snapshot produced before the counters existed) is
    /// simply "no hits" - never an error, never a crash.</summary>
    private static void Fill(ObservableCollection<ToolUsageRowViewModel> target, ToolHitCountDto[]? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var hit in source
            .Where(h => h is not null)
            .OrderByDescending(h => h.Count)
            .ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase))
        {
            target.Add(new ToolUsageRowViewModel(hit.Name, hit.Count));
        }
    }

    /// <summary>Same lookup style <see cref="AgentGraphViewModel"/> uses: every root's sessions plus
    /// the unattributed ones, matched case-insensitively (a tabId and a transcript-derived session
    /// id need not agree on hex casing - see <see cref="ISessionSelectionService.IsFocused"/>).</summary>
    private static SessionTreeDto? FindSession(RootsTreeDto? snapshot, string focusedId) =>
        EnumerateSessions(snapshot)
            .FirstOrDefault(s => string.Equals(s.SessionId, focusedId, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<SessionTreeDto> EnumerateSessions(RootsTreeDto? snapshot)
    {
        if (snapshot is null)
        {
            yield break;
        }

        foreach (var root in snapshot.Roots ?? Array.Empty<RootTreeDto>())
        {
            foreach (var session in root.Sessions ?? Array.Empty<SessionTreeDto>())
            {
                yield return session;
            }
        }

        foreach (var session in snapshot.UnattributedSessions ?? Array.Empty<SessionTreeDto>())
        {
            yield return session;
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

    /// <summary>A focus change with no new telemetry must still re-target both lists - re-resolving
    /// against the cached snapshot is the whole cost, same as <see cref="GitPanelViewModel"/>'s own
    /// focus-change handler.</summary>
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

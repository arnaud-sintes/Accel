namespace Accel.App.ViewModels;

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Accel.App.Services;
using Accel.Cli;
using Accel.Metrics;

/// <summary>One row in panel B's read-only git status list - a single path on one side (staged or
/// unstaged) of a `git status` line. No stage/unstage/discard action - Phase 7 scope is a plain
/// changes list, same restraint <see cref="FilesPanelNodeViewModel"/> applies to the file tree
/// above it.</summary>
public sealed class GitPanelEntryViewModel
{
    public GitPanelEntryViewModel(GitChangeEntry entry)
    {
        Path = entry.Path;
        StatusLetter = char.ToUpperInvariant(entry.StatusCode).ToString();
        StatusDescription = entry.StatusDescription;
    }

    /// <summary>Repo-relative path, exactly as `git status` reported it - this row's full text and
    /// its tooltip.</summary>
    public string Path { get; }

    /// <summary>Single-letter badge (M/A/D/R/C/U/?) - always paired with <see cref="StatusDescription"/>
    /// in the automation name, never color-only (same accessibility rule <see cref="FilesPanelNodeViewModel"/>
    /// follows for folder vs. file).</summary>
    public string StatusLetter { get; }

    public string StatusDescription { get; }

    public string AutomationDescription => $"{StatusDescription}: {Path}.";
}

/// <summary>
/// Panel B's second ViewModel (Phase 7): a read-only, flat git status list for whichever folder is
/// currently focused - the exact same root <see cref="FocusedRootResolver"/> resolves for
/// <see cref="FilesPanelViewModel"/>, so the file tree and the git list above/below each other
/// always agree on which folder they describe.
///
/// <para>Entries are split into <see cref="StagedChanges"/> and <see cref="Changes"/> (unstaged +
/// untracked), matching VS Code's Source Control view grouping. No commit/stage/push action exists
/// yet - this is list-only, same restraint panel B's file tree already applies (no rename/delete/
/// open).</para>
///
/// <para>Like <see cref="FilesPanelViewModel"/>, the list is rebuilt via <see cref="GitStatusBuilder.Build"/>
/// only when the focus signal actually changes (<see cref="Rebuild"/> is a no-op when the resolved
/// root matches <see cref="_resolvedRootPath"/>, same anti-thrash fix <see cref="FilesPanelViewModel.Rebuild"/>
/// applies) - never polled, never watched.</para>
///
/// <para><b>Follows the file tree's expanded folder, when it is itself a repo.</b> The default
/// context is the resolved root above (e.g. "C:/projects", which typically isn't a repo itself),
/// but <see cref="OnFilesPanelFolderExpanded"/> - wired to <see cref="FilesPanelViewModel.FolderExpanded"/>
/// by the composition root - lets the user drill into a specific project folder in the file tree and
/// have this section switch to that project's git status instead, without needing a selection
/// concept the read-only file tree doesn't otherwise have. Only takes effect when the expanded
/// folder resolves to a real repository (<see cref="GitStatusBuilder.Build"/> succeeds) - an
/// ordinary subfolder of the same repo, or a subfolder that isn't a repo at all, leaves the
/// currently displayed status alone. Cleared on the next real <see cref="Rebuild"/> (a genuine focus
/// change), since an expanded-folder override tied to the *previous* root would be stale.</para>
/// </summary>
public sealed partial class GitPanelViewModel : ObservableObject, IDisposable
{
    private readonly ITelemetryFeed _feed;
    private readonly IUiThreadDispatcher _dispatcher;
    private readonly ISessionSelectionService? _selection;
    private readonly RootsPanelViewModel? _rootsPanel;

    private RootsTreeDto? _latest;
    private string? _resolvedRootPath;
    private string? _expandedFolderPath;
    private bool _rootResolvedOnce;
    private bool _disposed;

    public GitPanelViewModel(
        ITelemetryFeed feed,
        IUiThreadDispatcher dispatcher,
        ISessionSelectionService? selection = null,
        RootsPanelViewModel? rootsPanel = null)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _selection = selection;
        _rootsPanel = rootsPanel;

        _feed.SnapshotAvailable += OnSnapshotAvailable;
        _feed.SnapshotFailed += OnSnapshotFailed;
        _selection?.Subscribe(this, OnFocusedSessionChanged);

        if (_rootsPanel is not null)
        {
            _rootsPanel.PropertyChanged += OnRootsPanelPropertyChanged;
        }

        Rebuild(_feed.Latest);
    }

    /// <summary>Sorted by path (ordinal, case-insensitive) - <c>git status</c>'s own output order is
    /// index-status-first, not alphabetical.</summary>
    public ObservableCollection<GitPanelEntryViewModel> StagedChanges { get; } = new();

    /// <summary>Sorted by path - see <see cref="StagedChanges"/>.</summary>
    public ObservableCollection<GitPanelEntryViewModel> Changes { get; } = new();

    /// <summary>The focused folder's path (when it is a git repository), or a "nothing focused"/
    /// "not a repository"/error hint - panel B's git section caption, same role as
    /// <see cref="FilesPanelViewModel.StatusText"/>.</summary>
    [ObservableProperty]
    private string _statusText = "No folder or session focused.";

    /// <summary>Whether the focused folder resolved to a real git repository - lets the view
    /// distinguish "nothing focused"/"not a repo" from "repo focused, currently clean".</summary>
    [ObservableProperty]
    private bool _hasRepo;

    /// <summary>The repo's folder name (e.g. "Accel") - empty when <see cref="HasRepo"/> is false.</summary>
    [ObservableProperty]
    private string _repoName = string.Empty;

    /// <summary>The current branch's upstream (e.g. "origin/main"), or a "no upstream" hint when the
    /// branch isn't tracking one - empty when <see cref="HasRepo"/> is false.</summary>
    [ObservableProperty]
    private string _remoteBranchText = string.Empty;

    /// <summary>"N change(s)" summary across <see cref="StagedChanges"/> and <see cref="Changes"/>
    /// combined - empty when <see cref="HasRepo"/> is false.</summary>
    [ObservableProperty]
    private string _changesSummaryText = string.Empty;

    /// <summary>Just the numeric count portion of <see cref="ChangesSummaryText"/> - split out so the
    /// view can bold only the count, not the "change(s)" label.</summary>
    [ObservableProperty]
    private string _changesCountText = string.Empty;

    /// <summary>"N commit(s) to push" summary (commits on the current branch ahead of its upstream) -
    /// empty when <see cref="HasRepo"/> is false or the branch has no upstream configured.</summary>
    [ObservableProperty]
    private string _pendingPushSummaryText = string.Empty;

    /// <summary>Just the numeric count portion of <see cref="PendingPushSummaryText"/> - split out so
    /// the view can bold only the count, not the "commit(s) to push" label.</summary>
    [ObservableProperty]
    private string _pendingPushCountText = string.Empty;

    /// <summary>The full rebuild. Public so tests can drive it directly with a fixture
    /// <see cref="RootsTreeDto"/>, exactly as <see cref="FilesPanelViewModel.Rebuild"/> is. A resolved
    /// root equal to the last one is a no-op (see this class's remarks) - a genuine change clears any
    /// <see cref="OnFilesPanelFolderExpanded"/> override, since it belonged to the previous root.</summary>
    public void Rebuild(RootsTreeDto? snapshot)
    {
        _latest = snapshot;

        string? rootPath = FocusedRootResolver.Resolve(snapshot, _selection, _rootsPanel);

        if (_rootResolvedOnce && string.Equals(rootPath, _resolvedRootPath, StringComparison.Ordinal))
        {
            return;
        }

        _rootResolvedOnce = true;
        _resolvedRootPath = rootPath;
        _expandedFolderPath = null;

        RefreshDisplay();
    }

    /// <summary>Called (via the composition root's wiring) whenever the file tree's
    /// <see cref="FilesPanelViewModel.FolderExpanded"/> fires - see this class's remarks for the
    /// "only when it's itself a repo" rule.</summary>
    public void OnFilesPanelFolderExpanded(string folderPath)
    {
        _expandedFolderPath = folderPath;
        RefreshDisplay();
    }

    /// <summary>Called (via the composition root's wiring) whenever the file tree's
    /// <see cref="FilesPanelViewModel.FolderCollapsed"/> fires. Only reacts when the collapsed folder
    /// is the one currently driving this section (or an ancestor of it, since collapsing a folder also
    /// hides every expanded descendant) - falls back to <paramref name="nearestExpandedAncestor"/>
    /// (or the resolved root, when <see langword="null"/>) rather than continuing to show a subtree
    /// that's no longer visible in the file tree.</summary>
    public void OnFilesPanelFolderCollapsed(string collapsedFolderPath, string? nearestExpandedAncestor)
    {
        if (_expandedFolderPath is null)
        {
            return;
        }

        bool showingCollapsedSubtree = string.Equals(_expandedFolderPath, collapsedFolderPath, StringComparison.OrdinalIgnoreCase)
            || _expandedFolderPath.StartsWith(collapsedFolderPath + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        if (!showingCollapsedSubtree)
        {
            return;
        }

        _expandedFolderPath = nearestExpandedAncestor;
        RefreshDisplay();
    }

    private void RefreshDisplay()
    {
        StagedChanges.Clear();
        Changes.Clear();

        string? effectivePath = _resolvedRootPath ?? _expandedFolderPath;

        if (string.IsNullOrEmpty(effectivePath))
        {
            HasRepo = false;
            StatusText = "No folder or session focused.";
            ClearSummary();
            return;
        }

        GitChangeEntry[]? entries = null;

        if (!string.IsNullOrEmpty(_expandedFolderPath))
        {
            var expandedEntries = GitStatusBuilder.Build(_expandedFolderPath);
            if (expandedEntries is not null)
            {
                entries = expandedEntries;
                effectivePath = _expandedFolderPath;
            }
        }

        if (entries is null && !string.IsNullOrEmpty(_resolvedRootPath))
        {
            entries = GitStatusBuilder.Build(_resolvedRootPath);
            effectivePath = _resolvedRootPath;
        }

        if (entries is null)
        {
            HasRepo = false;
            StatusText = $"Not a git repository: {effectivePath}";
            ClearSummary();
            return;
        }

        foreach (var entry in entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            var row = new GitPanelEntryViewModel(entry);
            (entry.IsStaged ? StagedChanges : Changes).Add(row);
        }

        HasRepo = true;
        StatusText = entries.Length == 0 ? $"{effectivePath} (clean)" : effectivePath!;

        var summary = GitStatusBuilder.BuildSummary(effectivePath);
        RepoName = summary?.RepoName ?? string.Empty;
        RemoteBranchText = summary is null
            ? string.Empty
            : summary.RemoteBranch ?? $"{summary.Branch} (no upstream)";

        int changeCount = StagedChanges.Count + Changes.Count;
        ChangesCountText = changeCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ChangesSummaryText = $"{changeCount} change(s)";

        if (summary?.RemoteBranch is null)
        {
            PendingPushCountText = string.Empty;
            PendingPushSummaryText = string.Empty;
        }
        else
        {
            PendingPushCountText = summary.AheadCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            PendingPushSummaryText = $"{summary.AheadCount} commit(s) to push";
        }
    }

    private void ClearSummary()
    {
        RepoName = string.Empty;
        RemoteBranchText = string.Empty;
        ChangesSummaryText = string.Empty;
        ChangesCountText = string.Empty;
        PendingPushSummaryText = string.Empty;
        PendingPushCountText = string.Empty;
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

    /// <summary>A focus change with no new telemetry must still re-target the list - re-resolving
    /// against the cached snapshot is the whole cost, same rationale as
    /// <see cref="FilesPanelViewModel"/>'s own focus-change handler.</summary>
    private void OnFocusedSessionChanged(FocusedSessionChangedMessage message) => _dispatcher.Post(() =>
    {
        if (!_disposed)
        {
            Rebuild(_latest);
        }
    });

    /// <summary>Panel A's own tree selection changed - see <see cref="FilesPanelViewModel"/>'s
    /// identical handler for why <see cref="RootsPanelViewModel.SelectedKey"/> is the one property
    /// name to react to.</summary>
    private void OnRootsPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RootsPanelViewModel.SelectedKey))
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            if (!_disposed)
            {
                Rebuild(_latest);
            }
        });
    }

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

        if (_rootsPanel is not null)
        {
            _rootsPanel.PropertyChanged -= OnRootsPanelPropertyChanged;
        }
    }
}

namespace Accel.App.ViewModels;

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Accel.App;
using Accel.App.Services;
using Accel.Cli;
using Accel.Metrics;

/// <summary>One row in panel B's read-only git status list - a single path on one side (staged or
/// unstaged) of a `git status` line. No stage/unstage/discard action - Phase 7 scope is a plain
/// changes list, same restraint <see cref="FilesPanelNodeViewModel"/> applies to the file tree
/// above it.</summary>
public sealed class GitPanelEntryViewModel
{
    public GitPanelEntryViewModel(GitChangeEntry entry, string repoRootPath)
    {
        Path = entry.Path;
        StatusLetter = entry.StatusCode == '?' ? "U" : char.ToUpperInvariant(entry.StatusCode).ToString();
        StatusDescription = entry.StatusDescription;
        RepoRootPath = repoRootPath;

        // git status always prints '/'-separated paths regardless of OS - Combine tolerates a '/'
        // on Windows fine at the API level, but normalizing keeps FullPath consistent with every
        // other path in this app (all built via Path.Combine/DirectorySeparatorChar).
        FullPath = System.IO.Path.Combine(repoRootPath, entry.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));

        // This row's own double-click gesture (MainWindow.GitChangeRow_MouseLeftButtonDown) is
        // deliberately narrower than every status this list can show: Added/Untracked/Deleted open a
        // single-pane view (MainWindow.ShowFileTabAsync - editable when a working-tree copy reads as
        // text, read-only for Deleted's git-show fallback), Modified opens a side-by-side diff
        // (MainWindow.ShowGitDiffTabAsync) - Renamed/Copied/Conflict have no well-defined "before" or
        // "after" this Phase's viewer can show cleanly yet.
        IsOpenable = entry.StatusCode is 'A' or '?' or 'D' or 'M';
        IsModified = entry.StatusCode == 'M';
        IsStaged = entry.IsStaged;
    }

    /// <summary>Repo-relative path, exactly as `git status` reported it - this row's full text and
    /// its tooltip.</summary>
    public string Path { get; }

    /// <summary>Single-letter badge (M/A/D/R/C/U, Untracked shown as "U" rather than raw "?") - always
    /// paired with <see cref="StatusDescription"/>
    /// in the automation name, never color-only (same accessibility rule <see cref="FilesPanelNodeViewModel"/>
    /// follows for folder vs. file).</summary>
    public string StatusLetter { get; }

    public string StatusDescription { get; }

    /// <summary>The repository root this entry's <see cref="Path"/> is relative to - passed through
    /// to <see cref="TabsViewModel.AddGitChangeTab"/> so a Deleted entry's read-only tab can fall back
    /// to <c>git show HEAD:&lt;Path&gt;</c> when <see cref="FullPath"/> no longer exists on disk.</summary>
    public string RepoRootPath { get; }

    /// <summary>The absolute on-disk path - present (working-tree copy) for every status except a
    /// pure Deleted entry, where it names a path that no longer exists.</summary>
    public string FullPath { get; }

    /// <summary>Whether double-clicking this row opens a read-only tab - see this constructor's
    /// remarks for which statuses qualify.</summary>
    public bool IsOpenable { get; }

    /// <summary>Whether this is a Modified ('M') row - determines whether
    /// <c>MainWindow.GitChangeRow_MouseLeftButtonDown</c> opens a single read-only view or a
    /// side-by-side diff.</summary>
    public bool IsModified { get; }

    /// <summary>Staged (index vs. HEAD) or unstaged (working tree vs. index) - which side of a
    /// Modified row's comparison is the working-tree file vs. a git revision. See
    /// <c>MainWindow.GitChangeRow_MouseLeftButtonDown</c> for how this picks the diff's two sides.</summary>
    public bool IsStaged { get; }

    public string AutomationDescription => $"{StatusDescription}: {Path}.";
}

/// <summary>
/// Panel B's second ViewModel: a git status list plus a set of mutating actions (stage/unstage,
/// discard, commit, push/pull, branch switch) for whichever folder is currently focused - the exact
/// same root <see cref="FocusedRootResolver"/> resolves for <see cref="FilesPanelViewModel"/>, so
/// the file tree and the git list above/below each other always agree on which folder they
/// describe.
///
/// <para>Entries are split into <see cref="StagedChanges"/> and <see cref="Changes"/> (unstaged +
/// untracked), matching VS Code's Source Control view grouping. Every mutating command below shells
/// out via <see cref="GitActionsService"/> and, on success, re-runs <see cref="RefreshDisplay"/>
/// rather than mutating <see cref="StagedChanges"/>/<see cref="Changes"/> directly - the list is
/// always a whole fresh `git status`, never a locally patched-up guess at what git did.</para>
///
/// <para><b>Three refresh triggers, and why each one is needed.</b> (1) A focus change, through
/// <see cref="Rebuild"/> - which stays a no-op when the resolved root is unchanged, the same
/// anti-thrash fast path <see cref="FilesPanelViewModel.Rebuild"/> has, so that a telemetry snapshot
/// from unrelated session activity cannot make this panel shell out to `git` several times.
/// (2) This panel's own commands, calling <see cref="RefreshDisplay"/> directly - they cannot go
/// through <see cref="ITelemetryFeed.RequestRefresh"/> for it, because that lands back in
/// <see cref="Rebuild"/> and hits exactly that fast path, which is why a commit or a push used to
/// leave its own result invisible until the user re-focused something. (3) A debounced
/// <see cref="IDirectoryWatcher"/> over the repository root, for everything Accel does not perform
/// itself: a Claude Code session committing in the terminal, an agent editing files in parallel, a
/// `git pull` in another window. Still no polling anywhere - an untouched repository costs
/// nothing.</para>
///
/// <para><b>Reading git is off the UI thread for trigger (3), and only for (3).</b> One refresh is
/// about half a dozen `git` subprocesses - measured at ~400ms on a mid-sized repository - so doing
/// that synchronously on every watcher tick froze the whole window for a visible fraction of every
/// second while an agent was working. <see cref="RefreshAsync"/> therefore reads on a thread pool
/// thread (<see cref="ReadDisplayState"/> touches no ViewModel state, which is what makes that safe)
/// and applies the result on the UI thread, coalescing ticks that arrive mid-read into one follow-up
/// pass instead of stacking up another half-dozen processes each. Triggers (1) and (2) stay
/// synchronous: they are user-driven and rare, and a command the user just invoked is expected to
/// have finished changing the list by the time it returns.</para>
///
/// <para><b>The watcher follows the repository, not the displayed folder</b>
/// (<see cref="GitStatusBuilder.FindRepositoryRoot"/>). Those differ whenever the folder being shown
/// is a subfolder of a repo - `git status` reports the whole repository from anywhere inside it - and
/// watching the subfolder would miss <c>.git</c>, i.e. exactly the commits, pushes and branch
/// switches this panel most needs to notice.</para>
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
    private readonly IGitActionsDialogService _actionDialogs;
    private readonly IFilesEntryConfirmationService _discardConfirmation;
    private readonly IDirectoryWatcher? _watcher;

    private RootsTreeDto? _latest;
    private string? _resolvedRootPath;
    private string? _expandedFolderPath;
    private string? _effectiveRepoPath;
    private bool _suppressBranchSelectionEcho;
    private bool _rootResolvedOnce;
    private bool _refreshInFlight;
    private bool _refreshQueued;
    private bool _disposed;

    public GitPanelViewModel(
        ITelemetryFeed feed,
        IUiThreadDispatcher dispatcher,
        ISessionSelectionService? selection = null,
        RootsPanelViewModel? rootsPanel = null,
        IGitActionsDialogService? actionDialogs = null,
        IFilesEntryConfirmationService? discardConfirmation = null,
        IDirectoryWatcher? watcher = null)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _selection = selection;
        _rootsPanel = rootsPanel;
        _actionDialogs = actionDialogs ?? new WpfGitActionsDialogService();
        _discardConfirmation = discardConfirmation ?? new MessageBoxFilesEntryConfirmationService();

        // Optional (and null in every unit test) so this ViewModel stays drivable without a real
        // watcher, a real timer or real filesystem timing - same reason the dialog services above are
        // injected. Owned from here on: disposed with this panel.
        _watcher = watcher;
        if (_watcher is not null)
        {
            _watcher.Changed += OnWatchedDirectoryChanged;
        }

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

    /// <summary>Local branch names for the header's branch-switcher ComboBox - repopulated by
    /// <see cref="RefreshBranchesAsync"/> after every <see cref="RefreshDisplay"/>.</summary>
    public ObservableCollection<string> AvailableBranches { get; } = new();

    /// <summary>The branch shown/selected in the header ComboBox. Setting this to a value other than
    /// the current branch triggers <see cref="SwitchBranchCommand"/> - see
    /// <see cref="OnSelectedBranchChanged"/> for the echo-suppression guard that keeps
    /// <see cref="RefreshBranchesAsync"/> from triggering a checkout every time it re-syncs this
    /// property to the branch git already reports as current.</summary>
    [ObservableProperty]
    private string? _selectedBranch;

    /// <summary>True while any mutating command (stage/unstage/discard/commit/push/pull/checkout) is
    /// in flight - disables the whole action toolbar and branch switcher rather than reasoning about
    /// which combinations of concurrent commands would be safe.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Short label describing the in-flight command (e.g. "Pushing…") - shown next to a busy
    /// indicator while <see cref="IsBusy"/> is true.</summary>
    [ObservableProperty]
    private string? _busyStatusText;

    partial void OnSelectedBranchChanged(string? value)
    {
        if (_suppressBranchSelectionEcho || string.IsNullOrEmpty(value) || string.IsNullOrEmpty(_effectiveRepoPath))
        {
            return;
        }

        _ = SwitchBranchAsync(value);
    }

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

        RefreshDisplay(refreshBranchList: true);
    }

    /// <summary>Called (via the composition root's wiring) whenever the file tree's
    /// <see cref="FilesPanelViewModel.FolderExpanded"/> fires - see this class's remarks for the
    /// "only when it's itself a repo" rule.</summary>
    public void OnFilesPanelFolderExpanded(string folderPath)
    {
        _expandedFolderPath = folderPath;
        RefreshDisplay(refreshBranchList: true);
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
        RefreshDisplay(refreshBranchList: true);
    }

    /// <summary>
    /// Re-runs `git status` for the repository currently on screen and rebuilds the lists - the
    /// public entry point for "something changed on disk that this panel did not do itself"
    /// (a commit, push, pull, checkout or file edit made by a Claude Code session, another editor, or
    /// panel D's own save path).
    ///
    /// <para>Skipped while <see cref="IsBusy"/>: one of this panel's own commands is mid-flight, and
    /// its own churn (<c>.git/index.lock</c> appearing, refs being rewritten) is exactly what the
    /// watcher is reporting. Reading a half-finished git state would show the user a list that never
    /// existed; the command refreshes on completion anyway.</para>
    ///
    /// <para>Fire-and-forget by design - the caller is an event handler with nowhere to await. See
    /// <see cref="LastRefreshTask"/> for how a test observes completion.</para>
    /// </summary>
    public void Refresh() => LastRefreshTask = RefreshAsync();

    /// <summary>The task started by the most recent <see cref="Refresh"/>, exposed purely so a test
    /// can await the refresh it just triggered rather than race it. Never awaited in production, where
    /// the whole point is not to block the UI thread on it.</summary>
    internal Task? LastRefreshTask { get; private set; }

    /// <summary>
    /// The off-UI-thread half of <see cref="Refresh"/>: reads git on a thread pool thread, then
    /// applies the result on the UI thread.
    /// </summary>
    /// <remarks>
    /// <para><b>Coalescing, not queuing.</b> While a read is in flight, further ticks only set a flag;
    /// when the read lands, one more pass runs if anything arrived meanwhile. Without this, a burst
    /// that outpaces the read (a rebase, a large checkout) would start a fresh half-dozen
    /// subprocesses per tick and fall further behind - and every result but the last would be thrown
    /// away anyway.</para>
    ///
    /// <para><b>Staleness.</b> The folder is captured before the read and re-checked after it: if
    /// focus moved in between, that move has already triggered its own refresh, so applying this
    /// now-stale read would briefly show the previous repository's changes under the new one's name.</para>
    /// </remarks>
    internal async Task RefreshAsync()
    {
        if (IsBusy || _disposed)
        {
            return;
        }

        if (_refreshInFlight)
        {
            _refreshQueued = true;
            return;
        }

        _refreshInFlight = true;
        try
        {
            do
            {
                _refreshQueued = false;

                string? resolvedRoot = _resolvedRootPath;
                string? expandedFolder = _expandedFolderPath;
                var state = await Task.Run(() => ReadDisplayState(resolvedRoot, expandedFolder)).ConfigureAwait(true);

                if (_disposed || IsBusy
                    || !string.Equals(resolvedRoot, _resolvedRootPath, StringComparison.Ordinal)
                    || !string.Equals(expandedFolder, _expandedFolderPath, StringComparison.Ordinal))
                {
                    return;
                }

                ApplyDisplayState(state, refreshBranchList: false);
            }
            while (_refreshQueued);
        }
        finally
        {
            _refreshInFlight = false;
        }
    }

    /// <summary>The <see cref="IDirectoryWatcher"/> already raises on the UI thread; posting anyway
    /// costs nothing when already there (see <see cref="IUiThreadDispatcher.Post"/>) and keeps the
    /// disposed-check idiom identical to every other handler on this class.</summary>
    private void OnWatchedDirectoryChanged() => _dispatcher.Post(() =>
    {
        if (!_disposed)
        {
            Refresh();
        }
    });

    /// <param name="refreshBranchList">Whether to re-read the <i>list</i> of local branches, which
    /// costs its own `git` process. False for a watcher-driven refresh: the branch actually checked
    /// out comes free from the summary this method already reads (so an external checkout still shows
    /// up), while the set of branches that exist only changes when one is created or deleted - and
    /// that is picked up by the next command, focus change, or a current branch the list has never
    /// heard of.</param>
    /// <summary>Everything one refresh reads from git, as a plain value: which folder the status
    /// actually came from, its entries (null when that folder is not in a repository at all), and the
    /// header summary. Exists so the reading and the rendering can happen on different threads.</summary>
    private sealed record GitDisplayState(string? EffectivePath, GitChangeEntry[]? Entries, GitRepoSummary? Summary);

    /// <summary>
    /// The whole git-reading half of a refresh, as a static function of its two inputs - it touches no
    /// field and no observable property, which is exactly what makes it safe to call on a thread pool
    /// thread from <see cref="RefreshAsync"/>. The expanded-folder override wins when it resolves to a
    /// repository; otherwise the resolved root does (see this class's remarks).
    /// </summary>
    private static GitDisplayState ReadDisplayState(string? resolvedRootPath, string? expandedFolderPath)
    {
        string? effectivePath = resolvedRootPath ?? expandedFolderPath;

        if (string.IsNullOrEmpty(effectivePath))
        {
            return new GitDisplayState(null, null, null);
        }

        GitChangeEntry[]? entries = null;

        if (!string.IsNullOrEmpty(expandedFolderPath))
        {
            var expandedEntries = GitStatusBuilder.Build(expandedFolderPath);
            if (expandedEntries is not null)
            {
                entries = expandedEntries;
                effectivePath = expandedFolderPath;
            }
        }

        if (entries is null && !string.IsNullOrEmpty(resolvedRootPath))
        {
            entries = GitStatusBuilder.Build(resolvedRootPath);
            effectivePath = resolvedRootPath;
        }

        return entries is null
            ? new GitDisplayState(effectivePath, null, null)
            : new GitDisplayState(effectivePath, entries, GitStatusBuilder.BuildSummary(effectivePath));
    }

    /// <summary>The synchronous refresh: read then apply, both inline. Used by a focus change and by
    /// this panel's own commands - see this class's remarks for why those two stay synchronous while
    /// the watcher-driven path does not.</summary>
    private void RefreshDisplay(bool refreshBranchList = false) =>
        ApplyDisplayState(ReadDisplayState(_resolvedRootPath, _expandedFolderPath), refreshBranchList);

    /// <summary>Renders a <see cref="GitDisplayState"/> into this ViewModel's observable state. Must
    /// run on the UI thread; does no git I/O of its own except the watcher re-target in
    /// <see cref="SetEffectiveRepoPath"/>, which is guarded to a genuine change of repository.</summary>
    private void ApplyDisplayState(GitDisplayState state, bool refreshBranchList)
    {
        StagedChanges.Clear();
        Changes.Clear();

        string? effectivePath = state.EffectivePath;

        if (string.IsNullOrEmpty(effectivePath))
        {
            HasRepo = false;
            StatusText = "No folder or session focused.";
            ClearSummary();
            SetEffectiveRepoPath(null);
            ClearBranches();
            return;
        }

        var entries = state.Entries;

        if (entries is null)
        {
            HasRepo = false;
            StatusText = $"Not a git repository: {effectivePath}";
            ClearSummary();
            SetEffectiveRepoPath(null);
            ClearBranches();
            return;
        }

        foreach (var entry in entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            var row = new GitPanelEntryViewModel(entry, effectivePath!);
            (entry.IsStaged ? StagedChanges : Changes).Add(row);
        }

        HasRepo = true;
        StatusText = entries.Length == 0 ? $"{effectivePath} (clean)" : effectivePath!;
        bool repoChanged = !string.Equals(_effectiveRepoPath, effectivePath, StringComparison.Ordinal);
        SetEffectiveRepoPath(effectivePath);

        var summary = state.Summary;
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

        SyncSelectedBranch(summary?.Branch);

        // A branch the ComboBox has never heard of means the list really is stale (something outside
        // Accel created and checked out a branch), so re-read it even on a cheap refresh.
        bool branchUnknown = summary?.Branch is { } branch && !AvailableBranches.Contains(branch);
        if (repoChanged || refreshBranchList || branchUnknown)
        {
            _ = RefreshBranchListAsync(effectivePath!, summary?.Branch);
        }
    }

    /// <summary>Sets the repository this panel is acting on and re-points the watcher at the
    /// <i>enclosing repository root</i> rather than at <paramref name="path"/> itself - see this
    /// class's remarks for why those two are not the same folder. Falls back to
    /// <paramref name="path"/> when git cannot name a root (git missing, or the folder is not in a
    /// repository, in which case there is nothing to show and nothing useful to watch anyway).</summary>
    private void SetEffectiveRepoPath(string? path)
    {
        string? previous = _effectiveRepoPath;
        _effectiveRepoPath = path;

        // Guarded on an actual change of folder, not just called every refresh: resolving the root
        // is another `git` subprocess, and RefreshDisplay now runs on every watcher tick - the
        // overwhelming majority of which are further changes to the repo already being watched.
        if (_watcher is null || string.Equals(previous, path, StringComparison.Ordinal))
        {
            return;
        }

        _watcher.Watch(path is null ? null : GitStatusBuilder.FindRepositoryRoot(path) ?? path);
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

    private void ClearBranches()
    {
        AvailableBranches.Clear();
        _suppressBranchSelectionEcho = true;
        SelectedBranch = null;
        _suppressBranchSelectionEcho = false;
    }

    /// <summary>Points the header ComboBox at the branch that is actually checked out, without
    /// letting the assignment be mistaken for the user picking a branch (which would start a
    /// checkout). Free of I/O: the caller already has the branch name.</summary>
    private void SyncSelectedBranch(string? currentBranch)
    {
        if (string.Equals(SelectedBranch, currentBranch, StringComparison.Ordinal))
        {
            return;
        }

        _suppressBranchSelectionEcho = true;
        SelectedBranch = currentBranch;
        _suppressBranchSelectionEcho = false;
    }

    /// <summary>Repopulates <see cref="AvailableBranches"/> for <paramref name="repoPath"/> - kept as
    /// a separate async tail call rather than folding into <see cref="RefreshDisplay"/> itself, since
    /// that method's own rebuild is otherwise fully synchronous and every other caller expects it to
    /// stay that way. <paramref name="currentBranch"/> is passed in rather than re-read: the caller
    /// has just built the summary that contains it, and re-reading it here was a second `git` process
    /// per refresh.</summary>
    private async Task RefreshBranchListAsync(string repoPath, string? currentBranch)
    {
        string[]? branches = await GitActionsService.ListLocalBranchesAsync(repoPath).ConfigureAwait(true);

        if (_disposed || !string.Equals(_effectiveRepoPath, repoPath, StringComparison.Ordinal))
        {
            return; // stale by the time this completed - a newer refresh has already superseded it.
        }

        _suppressBranchSelectionEcho = true;
        AvailableBranches.Clear();
        foreach (string branch in branches ?? Array.Empty<string>())
        {
            AvailableBranches.Add(branch);
        }

        SelectedBranch = currentBranch;
        _suppressBranchSelectionEcho = false;
    }

    /// <summary>Stages a single path - context-menu action on an unstaged/untracked
    /// <see cref="GitPanelEntryViewModel"/> row.</summary>
    [RelayCommand]
    private Task StageFileAsync(GitPanelEntryViewModel? entry) =>
        RunGitActionAsync(entry?.RepoRootPath, "Stage", "Staging…",
            ct => GitActionsService.StageAsync(entry!.RepoRootPath, entry.Path, ct));

    /// <summary>Unstages a single path - context-menu action on a staged
    /// <see cref="GitPanelEntryViewModel"/> row.</summary>
    [RelayCommand]
    private Task UnstageFileAsync(GitPanelEntryViewModel? entry) =>
        RunGitActionAsync(entry?.RepoRootPath, "Unstage", "Unstaging…",
            ct => GitActionsService.UnstageAsync(entry!.RepoRootPath, entry.Path, ct));

    /// <summary>Stages every unstaged/untracked change in one call - the toolbar's "Stage all"
    /// button. A no-op (no git call, no busy flag) when there's nothing unstaged to add - deliberately
    /// not gated by a generated <c>CanExecute</c>, since <see cref="Changes"/> is a plain
    /// <see cref="ObservableCollection{T}"/> whose count changes wouldn't otherwise notify the
    /// command, same reasoning <see cref="FilesPanelViewModel"/>'s commands use plain guards instead
    /// of <c>CanExecute</c> predicates.</summary>
    [RelayCommand]
    private Task StageAllAsync()
    {
        if (Changes.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunGitActionAsync(_effectiveRepoPath, "Stage all", "Staging…",
            ct => GitActionsService.StageAllAsync(_effectiveRepoPath!, ct));
    }

    /// <summary>Reverts a single path back to HEAD (see <see cref="GitActionsService.DiscardAsync"/>
    /// for the staged/unstaged/untracked distinction), after a tiered confirmation - context-menu
    /// "Discard changes…" action.</summary>
    [RelayCommand]
    private async Task DiscardFileAsync(GitPanelEntryViewModel? entry)
    {
        if (entry is null || string.IsNullOrEmpty(entry.RepoRootPath))
        {
            return;
        }

        bool confirmed = _discardConfirmation.ConfirmDiscardChanges(entry.Path, entry.IsStaged);
        if (!confirmed)
        {
            return;
        }

        bool isUntracked = entry.StatusLetter == "U";
        await RunGitActionAsync(entry.RepoRootPath, "Discard changes", "Discarding…",
            ct => GitActionsService.DiscardAsync(entry.RepoRootPath, entry.Path, entry.IsStaged, isUntracked, ct)).ConfigureAwait(true);
    }

    /// <summary>Opens the commit-message dialog and, once confirmed, commits every currently staged
    /// change - the toolbar's "Commit" button. A no-op when nothing is staged - see
    /// <see cref="StageAllAsync"/>'s remarks for why this is a plain guard rather than a generated
    /// <c>CanExecute</c>.</summary>
    [RelayCommand]
    private async Task CommitAsync()
    {
        if (string.IsNullOrEmpty(_effectiveRepoPath) || StagedChanges.Count == 0)
        {
            return;
        }

        string? message = _actionDialogs.PromptForCommitMessage(StagedChanges.Count);
        if (string.IsNullOrWhiteSpace(message))
        {
            return; // cancelled
        }

        await RunGitActionAsync(_effectiveRepoPath, "Commit", "Committing…",
            ct => GitActionsService.CommitAsync(_effectiveRepoPath!, message, ct)).ConfigureAwait(true);
    }

    /// <summary>Pushes the current branch to its upstream - the toolbar's "Push" button.</summary>
    [RelayCommand]
    private Task PushAsync() =>
        RunGitActionAsync(_effectiveRepoPath, "Push", "Pushing…",
            ct => GitActionsService.PushAsync(_effectiveRepoPath!, ct));

    /// <summary>Pulls the current branch's upstream - the toolbar's "Pull" button.</summary>
    [RelayCommand]
    private Task PullAsync() =>
        RunGitActionAsync(_effectiveRepoPath, "Pull", "Pulling…",
            ct => GitActionsService.PullAsync(_effectiveRepoPath!, ct));

    /// <summary>Switches to <paramref name="branchName"/>, warning first if the working tree has
    /// uncommitted changes - invoked from <see cref="OnSelectedBranchChanged"/> when the header
    /// ComboBox selection changes by user action (not by <see cref="RefreshBranchesAsync"/> re-syncing
    /// it). Reverts <see cref="SelectedBranch"/> back to the branch that was current, without ever
    /// calling git, whenever the user cancels the dirty-tree warning or git itself refuses the
    /// checkout.</summary>
    /// <summary>Internal (not private) purely so tests can drive this directly without going
    /// through the <see cref="SelectedBranch"/> property setter's fire-and-forget dispatch.</summary>
    internal async Task SwitchBranchAsync(string branchName)
    {
        string? repoPath = _effectiveRepoPath;
        if (string.IsNullOrEmpty(repoPath))
        {
            return;
        }

        string? previousBranch = GitStatusBuilder.BuildSummary(repoPath)?.Branch;
        if (string.Equals(previousBranch, branchName, StringComparison.Ordinal))
        {
            return;
        }

        if (GitActionsService.HasUncommittedChanges(repoPath))
        {
            bool proceed = AccelMessageDialog.ShowConfirm(
                null,
                "Switching branches will keep your uncommitted changes and may fail if they conflict — continue?",
                "Switch branch",
                AccelDialogIcon.Warning);

            if (!proceed)
            {
                RevertSelectedBranch(previousBranch);
                return;
            }
        }

        IsBusy = true;
        BusyStatusText = "Switching branch…";
        try
        {
            var result = await GitActionsService.CheckoutBranchAsync(repoPath, branchName).ConfigureAwait(true);
            if (result.Outcome != GitActionOutcome.Success)
            {
                RevertSelectedBranch(previousBranch);
                AccelMessageDialog.ShowMessage(null, result.ErrorMessage ?? "The checkout failed.", "Switch branch", AccelDialogIcon.Error);
                return;
            }

            RefreshDisplay(refreshBranchList: true);
            _feed.RequestRefresh();
        }
        finally
        {
            IsBusy = false;
            BusyStatusText = null;
        }
    }

    private void RevertSelectedBranch(string? previousBranch)
    {
        _suppressBranchSelectionEcho = true;
        SelectedBranch = previousBranch;
        _suppressBranchSelectionEcho = false;
    }

    /// <summary>Shared shape for every simple mutating command: guard/announce
    /// <see cref="IsBusy"/>/<see cref="BusyStatusText"/>, run <paramref name="action"/>, refresh on
    /// success, show an error dialog on failure.</summary>
    private async Task RunGitActionAsync(string? repoPath, string title, string busyText, Func<CancellationToken, Task<GitActionResult>> action)
    {
        if (string.IsNullOrEmpty(repoPath) || IsBusy)
        {
            return;
        }

        IsBusy = true;
        BusyStatusText = busyText;
        try
        {
            var result = await action(default).ConfigureAwait(true);
            if (result.Outcome != GitActionOutcome.Success)
            {
                AccelMessageDialog.ShowMessage(null, result.ErrorMessage ?? "The operation failed.", title, AccelDialogIcon.Error);
                return;
            }

            // RefreshDisplay() rather than Refresh(): IsBusy is still true here (the finally below
            // clears it), and this is the one caller that must refresh *because* of the command it
            // just finished. RequestRefresh stays for everything outside this panel.
            RefreshDisplay(refreshBranchList: true);
            _feed.RequestRefresh();
        }
        finally
        {
            IsBusy = false;
            BusyStatusText = null;
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

        if (_watcher is not null)
        {
            _watcher.Changed -= OnWatchedDirectoryChanged;
            _watcher.Dispose();
        }

        if (_rootsPanel is not null)
        {
            _rootsPanel.PropertyChanged -= OnRootsPanelPropertyChanged;
        }
    }
}

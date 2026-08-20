namespace Accel.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Accel.App;
using Accel.App.Services;
using Accel.Cli;
using Accel.Metrics;
using Accel.Orchestration;

/// <summary>
/// One row in panel B's read-only file/folder tree. The only user interaction this ViewModel itself
/// supports is a folder's own expand/collapse (<see cref="IsExpanded"/>, two-way bound) - no
/// selection, no rename, no delete. (A file row's double-click does open its tab in panel C/D, but
/// that gesture is handled entirely in <c>MainWindow.FilesTreeViewItem_MouseDoubleClick</c>, never
/// through this class.)
///
/// <para><b>Children load lazily, on first expand</b> (<see cref="OnIsExpandedChanged"/> calls
/// <see cref="FilesTreeBuilder.BuildChildren"/> with this node's own path) - never eagerly for the
/// whole subtree. Building the entire tree up front was tried first and had a real bug: one call
/// to a single, shared "total node" budget walked depth-first, so a large/deep branch earlier in
/// alphabetical order (e.g. a folder with lots of nested content) could exhaust the whole budget
/// before later siblings were even enumerated - silently truncating the top-level listing itself,
/// not just some deeply nested folder. Building one level at a time removes the failure mode
/// entirely: no shared budget, so a sibling's size can never affect another sibling's visibility.</para>
///
/// <para>A directory with <see cref="FileTreeNode.HasChildren"/> true gets one placeholder child
/// (<see cref="Placeholder"/>, never itself expandable/visible-with-content) purely so WPF's
/// <c>TreeView</c> renders an expand arrow before the real children are known - the default
/// <c>TreeViewItem</c> template only shows that arrow when <c>ItemsSource</c> already has at least
/// one item, and there is no bindable way to say "show it anyway" without a custom template.</para>
/// </summary>
public sealed partial class FilesPanelNodeViewModel : ObservableObject
{
    private readonly Action<string>? _onDirectoryExpanded;
    private readonly Action<string>? _onDirectoryCollapsed;
    private bool _childrenLoaded;

    public FilesPanelNodeViewModel(
        FileTreeNode node,
        Action<string>? onDirectoryExpanded = null,
        Action<string>? onDirectoryCollapsed = null)
    {
        Key = node.Path;
        Name = node.Name;
        IsDirectory = node.IsDirectory;
        _onDirectoryExpanded = onDirectoryExpanded;
        _onDirectoryCollapsed = onDirectoryCollapsed;

        if (IsDirectory && node.HasChildren)
        {
            Children.Add(Placeholder);
        }
        else
        {
            _childrenLoaded = true;
        }
    }

    /// <summary>The sentinel placeholder child - never real content, and never itself the target of
    /// a lazy load (<see cref="OnIsExpandedChanged"/> guards on <see cref="IsDirectory"/>, which this
    /// is not).</summary>
    private static readonly FilesPanelNodeViewModel Placeholder = new();

    private FilesPanelNodeViewModel()
    {
        Key = string.Empty;
        Name = string.Empty;
        IsDirectory = false;
        _childrenLoaded = true;
    }

    /// <summary>The full filesystem path - stable identity, and this row's tooltip text. Empty for
    /// <see cref="Placeholder"/>.</summary>
    public string Key { get; }

    public string Name { get; }

    public bool IsDirectory { get; }

    /// <summary>Dotfile/dot-directory convention (<c>.git</c>, <c>.vscode</c>, ...) - the only
    /// "hidden" signal available without a filesystem round-trip per row, and the one every
    /// cross-platform tool already treats as the hidden convention.</summary>
    public bool IsHidden => Name.Length > 0 && Name[0] == '.';

    /// <summary>Short chip text for the file-type badge (e.g. "TS", "{}"), empty for a directory or
    /// an unrecognized extension - see <see cref="FileTypeIconResolver"/>.</summary>
    public string IconLabel => IsDirectory ? string.Empty : FileTypeIconResolver.Resolve(Name).Label;

    /// <summary>Chip background colour for <see cref="IconLabel"/>, as a hex string ready for
    /// <see cref="Accel.App.Converters.HexToBrushConverter"/>.</summary>
    public string IconColorHex => IsDirectory ? string.Empty : FileTypeIconResolver.Resolve(Name).ColorHex;

    public ObservableCollection<FilesPanelNodeViewModel> Children { get; } = new();

    /// <summary>Whether <see cref="Children"/> reflects real, on-disk content yet (false while it
    /// still only holds <see cref="Placeholder"/>) - lets <see cref="FilesPanelViewModel.ApplyFilterRecursive"/>
    /// search only what has already been loaded, rather than forcing a synchronous, unbounded
    /// filesystem walk of every never-expanded folder (potentially the whole tree, including things
    /// like <c>node_modules</c> or <c>.git</c>) on every keystroke.</summary>
    internal bool ChildrenLoaded => _childrenLoaded;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Whether the search box's current text matches this row or any descendant - see
    /// <see cref="FilesPanelViewModel.ApplyFilter"/>. Defaults to true (every row visible) so a
    /// panel with no search text active never needs to touch this at all.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    partial void OnIsExpandedChanged(bool value)
    {
        // Reported on every expand, not just the first (lazy-load below only runs once) - panel
        // B's git section (GitPanelViewModel.OnFilesPanelFolderExpanded) uses this to follow
        // whichever folder the user is actually looking at, so re-expanding an already-loaded
        // folder must still notify.
        if (IsDirectory)
        {
            if (value)
            {
                _onDirectoryExpanded?.Invoke(Key);
            }
            else
            {
                _onDirectoryCollapsed?.Invoke(Key);
            }
        }

        if (!value || !IsDirectory || _childrenLoaded)
        {
            return;
        }

        _childrenLoaded = true;
        Children.Clear();

        foreach (var child in FilesTreeBuilder.BuildChildren(Key))
        {
            Children.Add(new FilesPanelNodeViewModel(child, _onDirectoryExpanded, _onDirectoryCollapsed));
        }
    }

    /// <summary>
    /// Re-reads this row's children from disk and merges the result into <see cref="Children"/>
    /// <b>in place</b> - matching rows keep their existing ViewModel instance, and therefore their
    /// <see cref="IsExpanded"/> state and their whole already-loaded subtree.
    ///
    /// <para><b>Why merging rather than rebuilding.</b> The obvious implementation of "refresh" -
    /// clear and re-add - is the exact bug <see cref="FilesPanelViewModel.Rebuild"/>'s no-op fast path
    /// exists to avoid: replacement rows always start collapsed, so every folder the user had opened
    /// snaps shut. That is tolerable once, on a genuine focus change; it is not tolerable on a
    /// refresh triggered by an agent writing a file somewhere in the tree, which can happen several
    /// times a minute while the user is reading it.</para>
    ///
    /// <para><b>Why this stays bounded.</b> Recursion stops at any folder whose children were never
    /// loaded (<see cref="ChildrenLoaded"/>) - such a row is updated by <see cref="SyncExpandArrow"/>
    /// from data the parent's enumeration already produced, at no I/O cost at all. So a refresh costs
    /// one directory enumeration per folder the user has actually opened, and nothing for the rest of
    /// the tree - the same discipline the lazy load itself follows.</para>
    /// </summary>
    internal void RefreshLoadedChildren() =>
        Merge(Children, FilesTreeBuilder.BuildChildren(Key), _onDirectoryExpanded, _onDirectoryCollapsed);

    /// <summary>
    /// Brings a never-expanded folder's expand arrow in line with disk: a folder that was empty when
    /// this level was enumerated needs its arrow back once something appears inside it, and one that
    /// has since been emptied needs it taken away.
    /// </summary>
    /// <remarks>
    /// Takes <paramref name="hasChildren"/> from the caller rather than probing for it. The parent's
    /// <see cref="FilesTreeBuilder.BuildChildren"/> already computed exactly this for every child it
    /// returned, so probing again would open one extra directory handle per collapsed folder per
    /// refresh - which, on a folder with a few hundred subdirectories being refreshed while an agent
    /// works in it, is the difference between a cheap refresh and a visibly janky one.
    /// </remarks>
    internal void SyncExpandArrow(bool hasChildren)
    {
        if (hasChildren && Children.Count == 0)
        {
            Children.Add(Placeholder);
        }
        else if (!hasChildren && Children.Count > 0 && !_childrenLoaded)
        {
            Children.Clear();
        }
    }

    /// <summary>
    /// Reconciles <paramref name="rows"/> against <paramref name="latest"/> so that afterwards it
    /// holds exactly <paramref name="latest"/>, in that order, reusing the existing ViewModel for
    /// every row that is still there. Shared by <see cref="RefreshLoadedChildren"/> (a folder's
    /// children) and <see cref="FilesPanelViewModel.Refresh"/> (the root's own top level), which are
    /// the same operation at two different levels.
    /// </summary>
    /// <remarks>
    /// Identity is the full path compared <b>ordinally</b>, not case-insensitively: on Windows a pure
    /// case rename ("readme.md" to "README.md") is the same file to the filesystem but a different
    /// <see cref="Name"/> to render, and <see cref="Name"/> is immutable - so it has to come through
    /// as a replacement row rather than a silently-stale one. <see cref="IsDirectory"/> is part of the
    /// identity for the same reason: a path can be deleted and re-created as the other kind between
    /// two refreshes.
    /// </remarks>
    internal static void Merge(
        ObservableCollection<FilesPanelNodeViewModel> rows,
        FileTreeNode[] latest,
        Action<string>? onDirectoryExpanded,
        Action<string>? onDirectoryCollapsed)
    {
        for (int i = 0; i < latest.Length; i++)
        {
            var node = latest[i];
            int existing = IndexOfEntry(rows, node, i);

            if (existing < 0)
            {
                rows.Insert(i, new FilesPanelNodeViewModel(node, onDirectoryExpanded, onDirectoryCollapsed));
                continue;
            }

            if (existing != i)
            {
                rows.Move(existing, i);
            }

            if (!rows[i].IsDirectory)
            {
                continue;
            }

            // Recurse only into what the user opened; everything else is settled from node.HasChildren,
            // which this enumeration already worked out - see SyncExpandArrow's remarks.
            if (rows[i].ChildrenLoaded)
            {
                rows[i].RefreshLoadedChildren();
            }
            else
            {
                rows[i].SyncExpandArrow(node.HasChildren);
            }
        }

        // Everything past latest.Length is what the loop above never claimed - i.e. rows whose entry
        // is gone from disk, already pushed to the tail by the Move calls.
        while (rows.Count > latest.Length)
        {
            rows.RemoveAt(rows.Count - 1);
        }
    }

    /// <summary>Where <paramref name="node"/> already lives in <paramref name="rows"/> at or after
    /// <paramref name="startIndex"/>, or -1. Never searches before <paramref name="startIndex"/>:
    /// those slots are already reconciled, so a match there would mean moving a row backwards over
    /// content that is known-correct.</summary>
    private static int IndexOfEntry(ObservableCollection<FilesPanelNodeViewModel> rows, FileTreeNode node, int startIndex)
    {
        for (int i = startIndex; i < rows.Count; i++)
        {
            if (rows[i].IsDirectory == node.IsDirectory && string.Equals(rows[i].Key, node.Path, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Accessible text description for <c>AutomationProperties.Name</c> - same
    /// never-colour/weight-alone rule <see cref="RootsPanelNodeViewModel"/> follows, even though this
    /// row's only visual distinction (bold for a folder) is already independent of colour.</summary>
    public string AutomationDescription =>
        (IsDirectory ? $"Folder: {Name}." : $"File: {Name}.") + (IsHidden ? " Hidden." : string.Empty);
}

/// <summary>
/// Panel B's ViewModel: a read-only file/folder tree rooted at whichever folder is currently
/// focused - the focused session's cwd (<see cref="ISessionSelectionService.FocusedSessionId"/>,
/// resolved against the same <see cref="ITelemetryFeed"/> snapshot panel A/E read) if a session is
/// focused, else panel A's own tree selection (<see cref="RootsPanelViewModel.SelectedRootPath"/>)
/// if a root/session/agent row is selected there instead.
///
/// <para><b>Two inputs, two different refreshes.</b> A change of <i>which folder</i> to show (a new
/// telemetry snapshot, a focused-session change, or panel A's own selection changing) goes through
/// <see cref="Rebuild"/>, which replaces the tree wholesale. A change to the <i>contents</i> of the
/// folder already being shown goes through <see cref="Refresh"/>, which merges disk into the
/// existing rows without disturbing them. Neither is a poll: the contents path is driven by an
/// injected <see cref="IDirectoryWatcher"/> (a debounced <c>FileSystemWatcher</c>, never a timer),
/// plus this panel's own explorer commands calling <see cref="Refresh"/> directly.</para>
///
/// <para><b>Why disk has to be an input at all.</b> Before it was, the panel only ever refreshed on
/// a focus change - so its own New/Rename/Delete commands appeared to do nothing (they refreshed
/// through <see cref="ITelemetryFeed.RequestRefresh"/>, which lands back in <see cref="Rebuild"/> and
/// hits the no-op fast path below), and a Claude Code session creating or deleting files in this very
/// folder in parallel - the thing Accel exists to watch - left the tree silently stale.</para>
///
/// <para><b>Resolving to the same root path is a no-op</b> (<see cref="Rebuild"/> compares against
/// <see cref="_currentRootPath"/> before touching <see cref="Nodes"/>): every trigger above can fire
/// with no real focus change (e.g. a telemetry snapshot from unrelated session activity elsewhere),
/// and a full <c>Nodes.Clear()</c> + rebuild threw away every <see cref="FilesPanelNodeViewModel.IsExpanded"/>
/// the user had set, since the replacement nodes always start collapsed - the tree would silently
/// snap shut moments after the user expanded something. Skipping the rebuild when the resolved root
/// hasn't changed both restores the "only when the focus signal actually changes" invariant above
/// (which nothing previously enforced) and fixes that bug in one place.</para>
/// </summary>
public sealed partial class FilesPanelViewModel : ObservableObject, IDisposable
{
    private readonly ITelemetryFeed _feed;
    private readonly IUiThreadDispatcher _dispatcher;
    private readonly ISessionSelectionService? _selection;
    private readonly RootsPanelViewModel? _rootsPanel;
    private readonly IFilesEntryDialogService _entryDialogs;
    private readonly IFilesEntryConfirmationService _entryConfirmation;
    private readonly IDirectoryWatcher? _watcher;

    private RootsTreeDto? _latest;
    private string? _currentRootPath;
    private bool _rootResolvedOnce;
    private bool _disposed;

    /// <summary>The search box's current text - filters <see cref="Nodes"/> down to rows whose
    /// <see cref="FilesPanelNodeViewModel.Name"/> contains it (case-insensitive), plus any ancestor
    /// folder of a match. Deliberately does <b>not</b> reach into a folder the user has never
    /// expanded (see <see cref="ApplyFilterRecursive"/>'s remarks) - only already-loaded content is
    /// searched, so a match nested under a folder that's still collapsed simply isn't found until
    /// the user expands it themselves.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Every folder this VM auto-expanded (and thereby lazily loaded) purely to search or
    /// reveal a match under a previously-collapsed row - collapsed back once <see cref="SearchText"/>
    /// is cleared, so clearing a search restores the tree to how the user actually left it.</summary>
    private readonly HashSet<FilesPanelNodeViewModel> _autoExpandedForSearch = new();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>The search box's reset button.</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    private void ApplyFilter()
    {
        string search = SearchText.Trim();

        if (search.Length == 0)
        {
            foreach (var node in _autoExpandedForSearch)
            {
                node.IsExpanded = false;
            }

            _autoExpandedForSearch.Clear();

            foreach (var node in EnumerateAll(Nodes))
            {
                node.IsVisible = true;
            }

            return;
        }

        foreach (var node in Nodes)
        {
            ApplyFilterRecursive(node, search);
        }
    }

    /// <summary>
    /// Only recurses into a directory whose children are already loaded (<see cref="FilesPanelNodeViewModel.ChildrenLoaded"/>)
    /// - a folder the user has never expanded is matched on its own <see cref="FilesPanelNodeViewModel.Name"/>
    /// only, never force-loaded just to search it. Forcing every never-expanded folder open on every
    /// keystroke was tried first and hung the app on any real project tree: it turns a single
    /// keystroke into a synchronous, unbounded filesystem walk of the whole tree (including things
    /// like <c>node_modules</c> or <c>.git</c>), all on the UI thread.
    /// </summary>
    private bool ApplyFilterRecursive(FilesPanelNodeViewModel node, string search)
    {
        bool selfMatch = node.Name.Contains(search, StringComparison.OrdinalIgnoreCase);
        bool anyChildVisible = false;

        if (node.IsDirectory && node.ChildrenLoaded)
        {
            foreach (var child in node.Children)
            {
                if (ApplyFilterRecursive(child, search))
                {
                    anyChildVisible = true;
                }
            }

            if (anyChildVisible && !node.IsExpanded)
            {
                node.IsExpanded = true;
                _autoExpandedForSearch.Add(node);
            }
        }

        node.IsVisible = selfMatch || anyChildVisible;
        return node.IsVisible;
    }

    private static IEnumerable<FilesPanelNodeViewModel> EnumerateAll(IEnumerable<FilesPanelNodeViewModel> nodes)
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

    /// <summary>Every directory path currently expanded in <see cref="Nodes"/> - lets
    /// <see cref="FolderCollapsed"/> report the nearest still-expanded ancestor of a folder that just
    /// collapsed, so the git section (<see cref="GitPanelViewModel.OnFilesPanelFolderCollapsed"/>) can
    /// fall back to it instead of clinging to a subtree that's no longer visible.</summary>
    private readonly HashSet<string> _expandedFolderPaths = new(StringComparer.OrdinalIgnoreCase);

    public FilesPanelViewModel(
        ITelemetryFeed feed,
        IUiThreadDispatcher dispatcher,
        ISessionSelectionService? selection = null,
        RootsPanelViewModel? rootsPanel = null,
        IFilesEntryDialogService? entryDialogs = null,
        IFilesEntryConfirmationService? entryConfirmation = null,
        IDirectoryWatcher? watcher = null)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _selection = selection;
        _rootsPanel = rootsPanel;
        _entryDialogs = entryDialogs ?? new WpfFilesEntryDialogService();
        _entryConfirmation = entryConfirmation ?? new MessageBoxFilesEntryConfirmationService();

        // Optional (and null in every unit test) so the whole ViewModel stays drivable without a real
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

    /// <summary>The focused folder's top-level children - the root folder itself is deliberately
    /// never a row here (the panel's status text already names it), only its contents.</summary>
    public ObservableCollection<FilesPanelNodeViewModel> Nodes { get; } = new();

    /// <summary>The focused folder's path (when a tree is showing), or a "nothing focused"/error
    /// hint - panel B's header caption, same role as <see cref="AgentGraphViewModel.StatusText"/>.</summary>
    [ObservableProperty]
    private string _statusText = "No folder or session focused.";

    /// <summary>Whether <see cref="Nodes"/> currently reflects a real, resolved folder - lets the view
    /// distinguish "nothing focused yet" from "focused folder is genuinely empty".</summary>
    [ObservableProperty]
    private bool _hasTree;

    /// <summary>Raised whenever a directory node in <see cref="Nodes"/> (at any depth) is expanded,
    /// with that folder's full path - panel B's git section (<see cref="GitPanelViewModel.OnFilesPanelFolderExpanded"/>)
    /// subscribes to this so it can follow whichever folder the user is actually looking at, when
    /// that folder is itself a git repository.</summary>
    public event Action<string>? FolderExpanded;

    /// <summary>Raised whenever a directory node collapses, with that folder's path and the nearest
    /// still-expanded ancestor folder's path (or <see langword="null"/> when no ancestor is still
    /// expanded, meaning the session/root itself) - lets panel B's git section
    /// (<see cref="GitPanelViewModel.OnFilesPanelFolderCollapsed"/>) fall back off a subtree that just
    /// disappeared instead of continuing to show it.</summary>
    public event Action<string, string?>? FolderCollapsed;

    /// <summary>The currently resolved focused root - the containment boundary every explorer command
    /// below passes to <see cref="FileSystemEntryPlanner"/>, and the parent directory used for a
    /// "New File…"/"New Folder…" invoked with nothing selected (the tree's empty background).</summary>
    public string? CurrentRootPath => _currentRootPath;

    /// <summary>
    /// Raised after Delete/DeletePermanently/MoveRename actually removes <paramref name="oldPath"/>
    /// from its old location (a plain delete, or the pre-move path of a rename/move) - never for a
    /// create. <c>MainWindow</c> subscribes to close any open tab keyed on that path, or nested under
    /// it: per this feature's scope, a mutated tab is simply closed, never rebound/reloaded.
    /// </summary>
    public event Action<string, bool>? EntryRemovedOrMoved;

    /// <summary>The full rebuild. Public so tests can drive it directly with a fixture
    /// <see cref="RootsTreeDto"/>, exactly as <see cref="AgentGraphViewModel.Rebuild"/> is. See this
    /// class's remarks for why resolving to the same root path as last time is a no-op.</summary>
    public void Rebuild(RootsTreeDto? snapshot)
    {
        _latest = snapshot;

        string? rootPath = FocusedRootResolver.Resolve(snapshot, _selection, _rootsPanel);

        if (_rootResolvedOnce && string.Equals(rootPath, _currentRootPath, StringComparison.Ordinal))
        {
            return;
        }

        _rootResolvedOnce = true;
        _currentRootPath = rootPath;

        // Follows the tree, not the session: the watcher only ever covers the folder actually on
        // screen, so a focus change stops watching the folder nobody is looking at any more.
        _watcher?.Watch(rootPath);

        Nodes.Clear();
        _expandedFolderPaths.Clear();

        // Every rebuild replaces every node - the old ones this set references are gone either way,
        // so there is nothing to collapse back.
        _autoExpandedForSearch.Clear();

        if (string.IsNullOrEmpty(rootPath))
        {
            HasTree = false;
            StatusText = "No folder or session focused.";
            return;
        }

        var children = FilesTreeBuilder.BuildRootChildren(rootPath);
        if (children is null)
        {
            HasTree = false;
            StatusText = $"Folder not found: {rootPath}";
            return;
        }

        foreach (var child in children)
        {
            Nodes.Add(new FilesPanelNodeViewModel(child, OnNodeExpanded, OnNodeCollapsed));
        }

        HasTree = true;
        StatusText = Nodes.Count == 0 ? $"{rootPath} (empty)" : rootPath;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            ApplyFilter();
        }
    }

    /// <summary>
    /// Re-reads the folder currently on screen and merges disk into the existing rows, keeping every
    /// expanded folder expanded (see <see cref="FilesPanelNodeViewModel.Merge"/>). This is the
    /// "contents changed" refresh, as opposed to <see cref="Rebuild"/>'s "different folder" one.
    ///
    /// <para>Called by the injected <see cref="IDirectoryWatcher"/> for an external change (an agent,
    /// another editor, a git checkout) and directly by this panel's own explorer commands, which
    /// cannot rely on <see cref="ITelemetryFeed.RequestRefresh"/> for it - that path ends in
    /// <see cref="Rebuild"/>, which correctly does nothing when the focused folder is unchanged.</para>
    ///
    /// <para>A no-op when no folder is resolved yet; a root that has itself disappeared degrades to
    /// the same "Folder not found" state <see cref="Rebuild"/> produces, rather than leaving the last
    /// known contents of a folder that no longer exists on screen.</para>
    /// </summary>
    public void Refresh()
    {
        if (string.IsNullOrEmpty(_currentRootPath))
        {
            return;
        }

        var children = FilesTreeBuilder.BuildRootChildren(_currentRootPath);

        if (children is null)
        {
            Nodes.Clear();
            _autoExpandedForSearch.Clear();
            SyncExpandedFolderPaths();
            HasTree = false;
            StatusText = $"Folder not found: {_currentRootPath}";
            return;
        }

        FilesPanelNodeViewModel.Merge(Nodes, children, OnNodeExpanded, OnNodeCollapsed);
        SyncExpandedFolderPaths();

        HasTree = true;
        StatusText = Nodes.Count == 0 ? $"{_currentRootPath} (empty)" : _currentRootPath;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            ApplyFilter();
        }
    }

    /// <summary>
    /// Brings the two collections that hold references <i>into</i> the tree back in line with the
    /// tree after a <see cref="Refresh"/> may have dropped rows: <see cref="_expandedFolderPaths"/>
    /// (paths) and <see cref="_autoExpandedForSearch"/> (node instances). A merge removes rows
    /// silently, so without this an expanded folder that was deleted on disk would stay in the set for
    /// ever - and would keep being reported as the "nearest still-expanded ancestor" of later
    /// collapses, pointing panel B's git section at a folder that no longer exists.
    ///
    /// <para>Also raises <see cref="FolderCollapsed"/> for each expanded folder that vanished, with
    /// the same nearest-still-expanded-ancestor fallback a user-driven collapse produces - a folder
    /// deleted underneath the git section is exactly as gone, from that section's point of view, as
    /// one the user closed.</para>
    /// </summary>
    private void SyncExpandedFolderPaths()
    {
        var liveExpandedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var liveNodes = new HashSet<FilesPanelNodeViewModel>();

        foreach (var node in EnumerateAll(Nodes))
        {
            liveNodes.Add(node);

            if (node.IsDirectory && node.IsExpanded)
            {
                liveExpandedPaths.Add(node.Key);
            }
        }

        _autoExpandedForSearch.RemoveWhere(node => !liveNodes.Contains(node));

        var vanished = new List<string>();
        foreach (string path in _expandedFolderPaths)
        {
            if (!liveExpandedPaths.Contains(path))
            {
                vanished.Add(path);
            }
        }

        _expandedFolderPaths.Clear();
        foreach (string path in liveExpandedPaths)
        {
            _expandedFolderPaths.Add(path);
        }

        foreach (string path in vanished)
        {
            string? ancestor = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(ancestor) && !_expandedFolderPaths.Contains(ancestor))
            {
                ancestor = Path.GetDirectoryName(ancestor);
            }

            FolderCollapsed?.Invoke(path, string.IsNullOrEmpty(ancestor) ? null : ancestor);
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

    private void OnNodeExpanded(string path)
    {
        _expandedFolderPaths.Add(path);
        FolderExpanded?.Invoke(path);
    }

    private void OnNodeCollapsed(string path)
    {
        _expandedFolderPaths.Remove(path);

        string? ancestor = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(ancestor) && !_expandedFolderPaths.Contains(ancestor))
        {
            ancestor = Path.GetDirectoryName(ancestor);
        }

        FolderCollapsed?.Invoke(path, string.IsNullOrEmpty(ancestor) ? null : ancestor);
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

    /// <summary>A focus change with no new telemetry must still re-target the tree - re-resolving
    /// against the cached snapshot is the whole cost, same rationale as
    /// <see cref="AgentGraphViewModel"/>'s own focus-change handler.</summary>
    private void OnFocusedSessionChanged(FocusedSessionChangedMessage message) => _dispatcher.Post(() =>
    {
        if (!_disposed)
        {
            Rebuild(_latest);
        }
    });

    /// <summary>Panel A's own tree selection changed - <see cref="RootsPanelViewModel.SelectedKey"/> is
    /// the only property on that ViewModel that actually raises <see cref="INotifyPropertyChanged"/>
    /// (<see cref="RootsPanelViewModel.SelectedRootPath"/> is a plain computed getter over it), so this
    /// is the one property name to react to.</summary>
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

    /// <summary>
    /// Creates a new file. <paramref name="node"/> is the row that was right-clicked - a directory row
    /// creates inside itself, any other row (or <see langword="null"/>, the tree's empty background)
    /// creates at <see cref="CurrentRootPath"/>.
    /// </summary>
    [RelayCommand]
    private Task NewFileAsync(FilesPanelNodeViewModel? node) =>
        CreateEntryAsync(node, NewFileSystemEntryKind.File);

    /// <summary>See <see cref="NewFileAsync"/> - identical shape, for a folder.</summary>
    [RelayCommand]
    private Task NewFolderAsync(FilesPanelNodeViewModel? node) =>
        CreateEntryAsync(node, NewFileSystemEntryKind.Folder);

    private async Task CreateEntryAsync(FilesPanelNodeViewModel? node, NewFileSystemEntryKind kind)
    {
        string? parentDir = node is { IsDirectory: true } && !string.IsNullOrEmpty(node.Key) ? node.Key : _currentRootPath;
        if (string.IsNullOrEmpty(parentDir) || string.IsNullOrEmpty(_currentRootPath))
        {
            return;
        }

        string? name = _entryDialogs.PromptForNewEntryName(kind, parentDir);
        if (string.IsNullOrWhiteSpace(name))
        {
            return; // cancelled
        }

        var plan = kind == NewFileSystemEntryKind.File
            ? FileSystemEntryPlanner.PlanCreateFile(parentDir, name, _currentRootPath)
            : FileSystemEntryPlanner.PlanCreateFolder(parentDir, name, _currentRootPath);

        string title = kind == NewFileSystemEntryKind.File ? "New file" : "New folder";
        if (!plan.IsSafe)
        {
            AccelMessageDialog.ShowMessage(null, string.Join('\n', plan.Warnings), title, AccelDialogIcon.Error);
            return;
        }

        var result = await Task.Run(() => FileSystemEntryExecutor.Execute(plan)).ConfigureAwait(true);
        if (result.Outcome == FileSystemEntryOutcome.Failed)
        {
            AccelMessageDialog.ShowMessage(null, result.Detail ?? "The operation failed.", title, AccelDialogIcon.Error);
            return;
        }

        // Refresh() for this panel's own tree - RequestRefresh below cannot do it, since it lands in
        // Rebuild, whose same-root fast path is a no-op - and RequestRefresh for everything else that
        // reads the focused folder through telemetry.
        Refresh();
        _feed.RequestRefresh();
    }

    /// <summary>
    /// Opens the Rename/Move dialog for <paramref name="node"/> and, once confirmed, moves it - a
    /// plain rename is simply a move whose destination shares the source's own parent directory. On
    /// success, raises <see cref="EntryRemovedOrMoved"/> for the row's old path before refreshing.
    /// </summary>
    [RelayCommand]
    private async Task MoveRenameAsync(FilesPanelNodeViewModel? node)
    {
        if (node is null || string.IsNullOrEmpty(node.Key) || string.IsNullOrEmpty(_currentRootPath))
        {
            return;
        }

        string? destination = _entryDialogs.PromptForMoveDestination(node.Key, node.IsDirectory);
        if (string.IsNullOrWhiteSpace(destination))
        {
            return; // cancelled
        }

        var plan = FileSystemEntryPlanner.PlanMove(node.Key, destination, _currentRootPath);
        if (!plan.IsSafe)
        {
            AccelMessageDialog.ShowMessage(null, string.Join('\n', plan.Warnings), "Rename / Move", AccelDialogIcon.Error);
            return;
        }

        var result = await Task.Run(() => FileSystemEntryExecutor.Execute(plan)).ConfigureAwait(true);
        if (result.Outcome == FileSystemEntryOutcome.Failed)
        {
            AccelMessageDialog.ShowMessage(null, result.Detail ?? "The operation failed.", "Rename / Move", AccelDialogIcon.Error);
            return;
        }

        if (result.Outcome == FileSystemEntryOutcome.Succeeded)
        {
            EntryRemovedOrMoved?.Invoke(node.Key, node.IsDirectory);
        }

        Refresh();
        _feed.RequestRefresh();
    }

    /// <summary>Moves <paramref name="node"/> to the recycle bin, after confirming.</summary>
    [RelayCommand]
    private Task DeleteAsync(FilesPanelNodeViewModel? node) => DeleteCoreAsync(node, SessionRemovalMode.RecycleBin);

    /// <summary>Permanently deletes <paramref name="node"/>, after a stronger confirmation.</summary>
    [RelayCommand]
    private Task DeletePermanentlyAsync(FilesPanelNodeViewModel? node) => DeleteCoreAsync(node, SessionRemovalMode.PermanentDelete);

    private async Task DeleteCoreAsync(FilesPanelNodeViewModel? node, SessionRemovalMode mode)
    {
        if (node is null || string.IsNullOrEmpty(node.Key) || string.IsNullOrEmpty(_currentRootPath))
        {
            return;
        }

        bool confirmed = mode == SessionRemovalMode.RecycleBin
            ? _entryConfirmation.ConfirmDelete(node.Name, node.IsDirectory)
            : _entryConfirmation.ConfirmPermanentDelete(node.Name, node.IsDirectory);
        if (!confirmed)
        {
            return;
        }

        var plan = FileSystemEntryPlanner.PlanDelete(node.Key, _currentRootPath);
        string title = mode == SessionRemovalMode.RecycleBin ? "Delete" : "Delete permanently";
        if (!plan.IsSafe)
        {
            AccelMessageDialog.ShowMessage(null, string.Join('\n', plan.Warnings), title, AccelDialogIcon.Error);
            return;
        }

        var result = await Task.Run(() => FileSystemEntryExecutor.Execute(plan, mode)).ConfigureAwait(true);
        if (result.Outcome == FileSystemEntryOutcome.Failed)
        {
            AccelMessageDialog.ShowMessage(null, result.Detail ?? "The operation failed.", title, AccelDialogIcon.Error);
            return;
        }

        // NotPresent (the plan-time snapshot going stale) is a soft-success, same as
        // SessionRemoverExecutor's own NotPresent handling - either way, the row is gone.
        EntryRemovedOrMoved?.Invoke(node.Key, node.IsDirectory);
        Refresh();
        _feed.RequestRefresh();
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

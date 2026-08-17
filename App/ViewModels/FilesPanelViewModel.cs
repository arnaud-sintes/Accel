namespace Accel.App.ViewModels;

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Accel.App.Services;
using Accel.Cli;
using Accel.Metrics;

/// <summary>
/// One row in panel B's read-only file/folder tree. The only user interaction it supports is a
/// folder's own expand/collapse (<see cref="IsExpanded"/>, two-way bound) - no selection, no
/// rename, no delete, no file-open action.
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
    private bool _childrenLoaded;

    public FilesPanelNodeViewModel(FileTreeNode node, Action<string>? onDirectoryExpanded = null)
    {
        Key = node.Path;
        Name = node.Name;
        IsDirectory = node.IsDirectory;
        _onDirectoryExpanded = onDirectoryExpanded;

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

    public ObservableCollection<FilesPanelNodeViewModel> Children { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        // Reported on every expand, not just the first (lazy-load below only runs once) - panel
        // B's git section (GitPanelViewModel.OnFilesPanelFolderExpanded) uses this to follow
        // whichever folder the user is actually looking at, so re-expanding an already-loaded
        // folder must still notify.
        if (value && IsDirectory)
        {
            _onDirectoryExpanded?.Invoke(Key);
        }

        if (!value || !IsDirectory || _childrenLoaded)
        {
            return;
        }

        _childrenLoaded = true;
        Children.Clear();

        foreach (var child in FilesTreeBuilder.BuildChildren(Key))
        {
            Children.Add(new FilesPanelNodeViewModel(child, _onDirectoryExpanded));
        }
    }

    /// <summary>Accessible text description for <c>AutomationProperties.Name</c> - same
    /// never-colour/weight-alone rule <see cref="RootsPanelNodeViewModel"/> follows, even though this
    /// row's only visual distinction (bold for a folder) is already independent of colour.</summary>
    public string AutomationDescription => IsDirectory ? $"Folder: {Name}." : $"File: {Name}.";
}

/// <summary>
/// Panel B's ViewModel: a read-only file/folder tree rooted at whichever folder is currently
/// focused - the focused session's cwd (<see cref="ISessionSelectionService.FocusedSessionId"/>,
/// resolved against the same <see cref="ITelemetryFeed"/> snapshot panel A/E read) if a session is
/// focused, else panel A's own tree selection (<see cref="RootsPanelViewModel.SelectedRootPath"/>)
/// if a root/session/agent row is selected there instead.
///
/// <para><b>No direct event wiring:</b> like <see cref="RootsPanelViewModel"/>/
/// <see cref="AgentGraphViewModel"/>, this class never touches a <c>FileSystemWatcher</c> or a
/// timer. The tree is rebuilt via <see cref="FilesTreeBuilder.Build"/> only when the focus signal
/// actually changes (a new telemetry snapshot, a focused-session change, or panel A's own selection
/// changing) - never polled.</para>
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

    private RootsTreeDto? _latest;
    private string? _currentRootPath;
    private bool _rootResolvedOnce;
    private bool _disposed;

    public FilesPanelViewModel(
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

        Nodes.Clear();

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
            Nodes.Add(new FilesPanelNodeViewModel(child, path => FolderExpanded?.Invoke(path)));
        }

        HasTree = true;
        StatusText = Nodes.Count == 0 ? $"{rootPath} (empty)" : rootPath;
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

namespace Accel.App.Services;

using System;
using System.IO;
using Accel.Cli;

/// <summary>
/// A debounced "something under this folder changed on disk" signal, re-targetable at runtime.
///
/// <para><b>Why panel B needs this at all.</b> Panel B's two sections
/// (<see cref="Accel.App.ViewModels.FilesPanelViewModel"/>,
/// <see cref="Accel.App.ViewModels.GitPanelViewModel"/>) both describe the *filesystem*, not
/// telemetry, and both used to refresh only when the focus signal changed. That left two holes
/// nothing else covered: (a) the panel's own mutating commands (new/rename/delete, stage/commit/
/// push/branch) refreshed through <see cref="ITelemetryFeed.RequestRefresh"/>, which loops back
/// into a <c>Rebuild</c> that is deliberately a no-op when the resolved root hasn't changed - so
/// the very operation the user just performed was invisible until they re-focused something; and
/// (b) a Claude Code session working in the same folder in parallel (Accel's entire reason to
/// exist) creates, rewrites and deletes exactly these files while the panel is showing them, with
/// no focus change at all. This interface is the missing second input: disk itself.</para>
///
/// <para><b>Not a poller.</b> Same discipline as <see cref="ITelemetryFeed"/>: a real
/// <see cref="FileSystemWatcher"/> pushes, a <see cref="DebounceCoalescer"/> collapses the burst,
/// and <see cref="Changed"/> fires once per quiet window on the UI thread. Nothing here re-checks
/// disk on a schedule, so an idle folder costs nothing. Exists as an interface purely so the two
/// ViewModels stay unit-testable headlessly (a test double raises <see cref="Changed"/> by hand,
/// with no real watcher, no timer and no filesystem timing) - the same seam
/// <see cref="IDebounceTimer"/>/<see cref="IUiThreadDispatcher"/> provide elsewhere.</para>
/// </summary>
public interface IDirectoryWatcher : IDisposable
{
    /// <summary>Raised on the UI thread, once per coalesced burst of filesystem events under
    /// <see cref="WatchedPath"/>. Never raised for a <see cref="Watch"/> call itself - re-targeting
    /// is not a change.</summary>
    event Action? Changed;

    /// <summary>The folder currently being watched, or null when watching nothing (never
    /// <see cref="Watch"/>ed, watched a path that doesn't exist, or watcher creation failed).</summary>
    string? WatchedPath { get; }

    /// <summary>Points this watcher at <paramref name="directoryPath"/>, replacing whatever it was
    /// watching before; null (or a path that isn't a directory) stops watching entirely. Idempotent
    /// for the path already being watched.</summary>
    void Watch(string? directoryPath);
}

/// <summary>
/// The production <see cref="IDirectoryWatcher"/>: one recursive <see cref="FileSystemWatcher"/>
/// behind the same <see cref="DebounceCoalescer"/> + <see cref="IDebounceTimer"/> pair
/// <see cref="TelemetryFeed"/> uses, so panel B's disk-change path throttles exactly the way the
/// telemetry path already does rather than inventing a second mechanism.
///
/// <para><b>Why the debounce window matters more here than in the telemetry path.</b> A single
/// logical operation is many filesystem events: a `git commit` rewrites <c>.git/index</c>, a lock
/// file, <c>HEAD</c> and several refs; an agent editing a file typically writes a temp file and
/// renames it. Firing <see cref="Changed"/> per event would run a `git status` (or a directory
/// re-enumeration) several times per user-visible change, all on the UI thread. The coalescer
/// collapses each burst into one refresh, and the caller picks the window - panel B's git section
/// deliberately uses a longer one than its file tree, because its refresh shells out to `git`.</para>
///
/// <para><b>Never throws, never fatal.</b> A watcher that can't be created (permissions, a path
/// that vanished mid-call, a network share) leaves <see cref="WatchedPath"/> null and the panel
/// simply behaves as it did before disk watching existed - refreshing on focus change and on its
/// own commands. Same best-effort contract as
/// <see cref="TelemetryFeed"/>'s own <c>TryCreateProjectsWatcher</c>.</para>
/// </summary>
public sealed class FileSystemDirectoryWatcher : IDirectoryWatcher
{
    /// <summary>64 KB (the maximum useful value for a non-network path). The default 8 KB overflows
    /// on exactly the workloads this watcher exists for - a `git checkout` or an agent rewriting a
    /// tree faster than the buffer drains - and an overflow is reported as
    /// <see cref="FileSystemWatcher.Error"/> with the intervening events *lost*, not queued. See
    /// <see cref="OnWatcherError"/> for the belt-and-braces handling of an overflow that happens
    /// anyway.</summary>
    private const int InternalBufferSize = 64 * 1024;

    /// <summary>
    /// Directory names whose contents are dropped before they can cost a refresh, for <i>both</i>
    /// panels and regardless of any constructor flag - build output and tool caches.
    ///
    /// <para><b>Why this is not optional.</b> A single <c>dotnet build</c> (or <c>npm install</c>, or
    /// a test run) creates, rewrites and deletes thousands of files under these names. Without this
    /// filter, a build in the folder panel B happens to be showing keeps the debounce window
    /// permanently re-armed and turns the whole panel into a refresh loop for as long as the build
    /// lasts - re-enumerating every expanded folder and, for the git section, re-running `git status`
    /// - while none of it is content the user is looking at. This is the same defaulted
    /// watcher-exclude list every editor ships (VS Code's <c>files.watcherExclude</c>) and for the
    /// same reason.</para>
    ///
    /// <para><b>What it costs.</b> A change <i>inside</i> one of these folders no longer refreshes
    /// panel B on its own. They are gitignored in practically every project, so the git section loses
    /// nothing; the file tree keeps showing them and still enumerates them on expand and on a focus
    /// change, so the only loss is live auto-refresh of a build-output folder the user has expanded.
    /// Matched as whole path segments (see <see cref="IsIgnoredPath"/>) and only <i>below</i> the
    /// watched root, so a project that genuinely lives in a folder called <c>target</c> is unaffected.</para>
    /// </summary>
    private static readonly string[] HighChurnDirectories =
    {
        "bin", "obj", "node_modules", "target", "packages", ".vs", ".idea", ".gradle",
        "__pycache__", ".pytest_cache", ".mypy_cache", ".venv", ".tox", ".next", ".nuxt", ".cache",
    };

    private readonly IUiThreadDispatcher _dispatcher;
    private readonly IDebounceTimer _timer;
    private readonly DebounceCoalescer _coalescer;
    private readonly bool _includeContentChanges;
    private readonly bool _ignoreGitInternals;

    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <param name="dispatcher">Marshals thread-pool watcher callbacks onto the UI thread
    /// <i>before</i> they reach the coalescer, so the coalescer stays single-threaded and needs no
    /// locking - the same invariant <see cref="TelemetryFeed"/> establishes.</param>
    /// <param name="timer">The debounce window. Owned and disposed by this instance.</param>
    /// <param name="includeContentChanges">Whether a write to an existing file (rather than a
    /// create/delete/rename) counts as a change. True for panel B's git section, whose `git status`
    /// output turns on file *content*; false for its file tree, which renders names and nesting only
    /// and would otherwise refresh on every keystroke an agent saves.</param>
    /// <param name="ignoreGitInternals">Whether to drop events under a <c>.git</c> directory. True
    /// for the file tree: a repo's <c>.git</c> churns constantly (index, locks, refs, objects) and
    /// none of it changes the tree's shape unless the user has <c>.git</c> itself expanded, which is
    /// not worth re-enumerating every loaded folder for. Deliberately false for the git section -
    /// <c>.git</c> is precisely where a commit, push or branch switch shows up. Note that
    /// <see cref="HighChurnDirectories"/> is dropped either way, independently of this flag.</param>
    public FileSystemDirectoryWatcher(
        IUiThreadDispatcher dispatcher,
        IDebounceTimer timer,
        bool includeContentChanges = true,
        bool ignoreGitInternals = false)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        _includeContentChanges = includeContentChanges;
        _ignoreGitInternals = ignoreGitInternals;

        _coalescer = new DebounceCoalescer(restartTimer: _timer.Restart, stopTimer: _timer.Stop);
        _timer.Tick += OnTimerTick;
    }

    public event Action? Changed;

    public string? WatchedPath { get; private set; }

    public void Watch(string? directoryPath)
    {
        if (_disposed)
        {
            return;
        }

        if (string.Equals(WatchedPath, directoryPath, StringComparison.OrdinalIgnoreCase) && _watcher is not null)
        {
            return;
        }

        Teardown();

        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return;
        }

        _watcher = TryCreateWatcher(directoryPath);
        WatchedPath = _watcher is null ? null : directoryPath;
    }

    private FileSystemWatcher? TryCreateWatcher(string directoryPath)
    {
        try
        {
            var notifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName;
            if (_includeContentChanges)
            {
                notifyFilter |= NotifyFilters.LastWrite | NotifyFilters.Size;
            }

            var watcher = new FileSystemWatcher(directoryPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = notifyFilter,
                InternalBufferSize = InternalBufferSize,
            };

            watcher.Created += OnWatcherEvent;
            watcher.Deleted += OnWatcherEvent;
            watcher.Renamed += OnWatcherEvent;
            watcher.Error += OnWatcherError;

            // Only subscribed when content changes count: NotifyFilter alone does not suppress
            // Changed for a create/rename (Windows reports those as several notifications), so an
            // unfiltered Changed subscription would defeat includeContentChanges: false.
            if (_includeContentChanges)
            {
                watcher.Changed += OnWatcherEvent;
            }

            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        // Filtered here, on the thread-pool callback, rather than after marshalling: the whole point
        // is that an ignored event costs nothing, and a Post per event would already be a cost.
        string? root = WatchedPath;
        if (root is not null && IsIgnoredPath(root, e.FullPath, _ignoreGitInternals))
        {
            return;
        }

        SignalOnUiThread();
    }

    /// <summary>
    /// An <see cref="FileSystemWatcher.Error"/> means events were <i>lost</i> (buffer overflow) or
    /// the watch itself died (the watched folder was deleted or its volume went away). Both cases
    /// are handled the same way: signal a change regardless - the panel's refresh re-reads disk from
    /// scratch, so it recovers whatever the dropped events would have said - then try to re-arm the
    /// watch on the same path, which succeeds after an overflow and fails harmlessly once the folder
    /// is genuinely gone.
    /// </summary>
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        string? path = WatchedPath;
        SignalOnUiThread();

        _dispatcher.Post(() =>
        {
            if (_disposed || !string.Equals(WatchedPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return; // already re-targeted elsewhere - re-arming the old path would be wrong.
            }

            Teardown();
            Watch(path);
        });
    }

    /// <summary>
    /// Whether an event for <paramref name="fullPath"/> should be dropped: it is under a
    /// <see cref="HighChurnDirectories"/> folder, or (when <paramref name="ignoreGitInternals"/>)
    /// under <c>.git</c>.
    /// </summary>
    /// <remarks>
    /// <para>Two rules, both load-bearing. <b>Whole segments only</b>, never substrings, so a real
    /// file or folder named <c>.gitignore</c>, <c>.github</c>, <c>binaries</c> or <c>targets</c> is
    /// not silently ignored. <b>Only below <paramref name="root"/></b>, because the watched root's own
    /// path is not the user's content: a project checked out at <c>C:\work\target\app</c> would
    /// otherwise have every single event in it dropped.</para>
    ///
    /// <para>Internal rather than private purely so the rule can be unit-tested directly, without
    /// provoking real filesystem events.</para>
    /// </remarks>
    internal static bool IsIgnoredPath(string root, string fullPath, bool ignoreGitInternals)
    {
        if (fullPath.Length <= root.Length)
        {
            return false;
        }

        ReadOnlySpan<char> remaining = fullPath.AsSpan(root.Length);

        while (!remaining.IsEmpty)
        {
            int separator = remaining.IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            ReadOnlySpan<char> segment = separator < 0 ? remaining : remaining[..separator];

            if (ignoreGitInternals && segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (string ignored in HighChurnDirectories)
            {
                if (segment.Equals(ignored, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            remaining = separator < 0 ? default : remaining[(separator + 1)..];
        }

        return false;
    }

    private void SignalOnUiThread()
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            if (!_disposed)
            {
                _coalescer.Signal();
            }
        });
    }

    private void OnTimerTick()
    {
        if (!_disposed && _coalescer.Elapsed())
        {
            Changed?.Invoke();
        }
    }

    private void Teardown()
    {
        WatchedPath = null;

        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnWatcherEvent;
        _watcher.Deleted -= OnWatcherEvent;
        _watcher.Renamed -= OnWatcherEvent;
        _watcher.Changed -= OnWatcherEvent;
        _watcher.Error -= OnWatcherError;
        _watcher.Dispose();
        _watcher = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Teardown();

        _timer.Tick -= OnTimerTick;
        _timer.Stop();
        _timer.Dispose();
    }
}

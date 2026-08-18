namespace Accel.App.Services;

using System;
using System.IO;
using Accel.Cli;
using Accel.Metrics;
using Accel.Server;

/// <summary>
/// P1-T2 / locked-in decision 8: the single push-style telemetry feed the WPF layer subscribes to.
///
/// <para>Everything a panel ViewModel needs arrives as a whole <see cref="RootsTreeDto"/> pushed at
/// it on the UI thread. There is deliberately <b>no HTTP polling of <c>/roots/tree</c></b> here (nor
/// any <see cref="System.Net.Http.HttpClient"/> anywhere in this file): the feed reads the same
/// in-process <see cref="RootsTreeBuilder"/>/<see cref="SessionState"/> instances the Kestrel routes
/// read, which is what makes the route and the panel byte-identical by construction rather than by
/// convention. Panels also never wire themselves to <see cref="SessionState.Changed"/> or a
/// <see cref="FileSystemWatcher"/> directly - that WinForms-style point-to-point event wiring stops
/// at this interface.</para>
/// </summary>
public interface ITelemetryFeed : IDisposable
{
    /// <summary>
    /// Raised - always on the UI thread (see <see cref="IUiThreadDispatcher"/>) - with a freshly
    /// built snapshot, once per coalesced burst of change signals.
    /// </summary>
    event Action<RootsTreeDto>? SnapshotAvailable;

    /// <summary>
    /// Raised on the UI thread when building a snapshot threw. Mirrors
    /// <c>MonitorForm.RefreshAndRender</c>'s catch-all: the last good snapshot stays as-is and the
    /// failure is surfaced as text, never as a crash.
    /// </summary>
    event Action<string>? SnapshotFailed;

    /// <summary>The most recent successfully built snapshot, or null before the first one.</summary>
    RootsTreeDto? Latest { get; }

    /// <summary>
    /// Subscribes to the underlying change signals and publishes one immediate snapshot, so a
    /// freshly opened panel is never blank while waiting for the first change - exactly what
    /// <c>MonitorForm.OnLoad</c> does with its direct <c>RefreshAndRender()</c> call.
    /// </summary>
    void Start();

    /// <summary>
    /// Injects one "something changed" signal by hand (the manual refresh command). Goes through
    /// the same debounce window as a real signal, so a click-storm still coalesces into one
    /// rebuild.
    /// </summary>
    void RequestRefresh();
}

/// <summary>
/// The push sources <see cref="TelemetryFeed"/> composes, behind an interface so unit tests can
/// supply an in-memory double instead of a live <see cref="EventServer"/> and a real
/// <see cref="FileSystemWatcher"/>. <see cref="EventServerTelemetrySource"/> is the production
/// implementation and is a straight transcription of what <c>MonitorForm</c> reads today.
/// </summary>
public interface ITelemetrySource
{
    /// <summary>The primary push signal: <see cref="SessionState.Changed"/>, i.e. anything that
    /// arrived via a hook/statusline POST into this process. Fires on Kestrel request threads.</summary>
    event Action? Changed;

    /// <summary>
    /// The directory to hang the secondary <see cref="FileSystemWatcher"/> signal off (the same
    /// <c>%USERPROFILE%\.claude\projects</c> tree <see cref="RootsTreeBuilder"/> scans), or null to
    /// run without a watcher at all. Tests return null; production returns the real (or overridden)
    /// projects directory.
    /// </summary>
    string? ProjectsDirectory { get; }

    /// <summary>Builds a full snapshot - the same call <c>GET /roots/tree</c> and
    /// <c>MonitorForm.RefreshAndRender</c> make, on the same shared builder instance so its caches
    /// are shared too.</summary>
    RootsTreeDto BuildSnapshot();
}

/// <summary>
/// Production <see cref="ITelemetrySource"/>: the in-process <see cref="EventServer"/>'s own
/// <see cref="EventServer.State"/>, <see cref="EventServer.Roots"/> and
/// <see cref="EventServer.RootsTree"/>. Same three inputs, same single
/// <c>RootsTree.Build(Roots, State, ProjectsDirOverride)</c> call as
/// <c>MonitorForm.RefreshAndRender</c> - no HTTP hop.
/// </summary>
public sealed class EventServerTelemetrySource : ITelemetrySource
{
    private readonly EventServer _server;

    public EventServerTelemetrySource(EventServer server)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
    }

    public event Action? Changed
    {
        add => _server.State.Changed += value;
        remove => _server.State.Changed -= value;
    }

    public string? ProjectsDirectory => _server.ProjectsDirOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude",
        "projects");

    public RootsTreeDto BuildSnapshot() =>
        _server.RootsTree.Build(
            _server.Roots,
            _server.State,
            _server.ProjectsDirOverride,
            RootFoldersConfig.LoadFull().Sessions);
}

/// <summary>
/// The concrete <see cref="ITelemetryFeed"/>: <see cref="ITelemetrySource.Changed"/> +
/// <see cref="FileSystemWatcher"/> + the existing <see cref="DebounceCoalescer"/>, collapsed into
/// one UI-thread push.
///
/// <para>Signal path, transcribed from <c>MonitorForm</c>: both sources can fire on background
/// threads, so each one is marshalled onto the UI thread (<see cref="IUiThreadDispatcher.Post"/> →
/// <c>Dispatcher.BeginInvoke</c>) <i>before</i> touching the coalescer, which therefore stays
/// single-threaded and needs no locking of its own - the same invariant
/// <c>MonitorForm.SignalOnUiThread</c> establishes with <c>Control.BeginInvoke</c>. The coalescer
/// itself is used verbatim (same class, same two delegates, same 250 ms window via
/// <see cref="DispatcherDebounceTimer.Interval"/>): a signal restarts the window, and only when the
/// window elapses with a pending signal does <see cref="SnapshotAvailable"/> fire once for the whole
/// burst. Nothing in here fires on a schedule of its own.</para>
/// </summary>
public sealed class TelemetryFeed : ITelemetryFeed
{
    private readonly ITelemetrySource _source;
    private readonly IUiThreadDispatcher _dispatcher;
    private readonly IDebounceTimer _timer;
    private readonly DebounceCoalescer _coalescer;

    private FileSystemWatcher? _fileWatcher;
    private bool _started;
    private bool _disposed;

    public TelemetryFeed(ITelemetrySource source, IUiThreadDispatcher dispatcher, IDebounceTimer timer)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));

        // Verbatim reuse: the same DebounceCoalescer MonitorForm uses, driven by the same two
        // restart/stop delegates - only the timer implementation behind them differs (DispatcherTimer
        // instead of a WinForms Timer).
        _coalescer = new DebounceCoalescer(restartTimer: _timer.Restart, stopTimer: _timer.Stop);
        _timer.Tick += OnTimerTick;
    }

    public event Action<RootsTreeDto>? SnapshotAvailable;

    public event Action<string>? SnapshotFailed;

    public RootsTreeDto? Latest { get; private set; }

    /// <summary>Test/diagnostic hook: whether a secondary <see cref="FileSystemWatcher"/> is
    /// actually live (false when the projects directory doesn't exist yet, or watcher creation
    /// failed - in which case <see cref="ITelemetrySource.Changed"/> still covers live activity,
    /// exactly as in <c>MonitorForm.TryCreateProjectsWatcher</c>).</summary>
    public bool IsWatchingFileSystem => _fileWatcher is not null;

    public void Start()
    {
        if (_disposed || _started)
        {
            return;
        }

        _started = true;

        _source.Changed += OnSourceChanged;
        _fileWatcher = TryCreateProjectsWatcher();

        // Publish immediately rather than waiting for the first change signal, so the panel isn't
        // blank the first time it is shown (MonitorForm.OnLoad's direct RefreshAndRender call).
        _dispatcher.Post(Publish);
    }

    public void RequestRefresh() => SignalOnUiThread();

    // Both signal sources below fire on background threads (a Kestrel request thread for
    // SessionState.Changed; a thread-pool thread for FileSystemWatcher), hence the marshalling.
    private void OnSourceChanged() => SignalOnUiThread();

    private void OnProjectsDirChanged(object sender, FileSystemEventArgs e) => SignalOnUiThread();

    private void SignalOnUiThread()
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            _coalescer.Signal();
        });
    }

    private void OnTimerTick()
    {
        if (_disposed)
        {
            return;
        }

        if (_coalescer.Elapsed())
        {
            Publish();
        }
    }

    private void Publish()
    {
        if (_disposed)
        {
            return;
        }

        RootsTreeDto snapshot;
        try
        {
            snapshot = _source.BuildSnapshot();
        }
        catch (Exception ex)
        {
            // Never let a rebuild failure escape onto the UI thread - keep the last good snapshot
            // and surface the message, same contract as MonitorForm.RefreshAndRender's catch.
            SnapshotFailed?.Invoke(ex.Message);
            return;
        }

        Latest = snapshot;
        SnapshotAvailable?.Invoke(snapshot);
    }

    private FileSystemWatcher? TryCreateProjectsWatcher()
    {
        try
        {
            string? projectsDir = _source.ProjectsDirectory;
            if (string.IsNullOrEmpty(projectsDir) || !Directory.Exists(projectsDir))
            {
                // Nothing to watch yet - ITelemetrySource.Changed still covers live activity.
                return null;
            }

            var watcher = new FileSystemWatcher(projectsDir, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };

            watcher.Changed += OnProjectsDirChanged;
            watcher.Created += OnProjectsDirChanged;
            watcher.Renamed += OnProjectsDirChanged;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch
        {
            // Best-effort only - a watcher failure (permissions, race) must never prevent the panel
            // from working; see MonitorForm.TryCreateProjectsWatcher's identical rationale.
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_started)
        {
            _source.Changed -= OnSourceChanged;
        }

        if (_fileWatcher is not null)
        {
            _fileWatcher.EnableRaisingEvents = false;
            _fileWatcher.Changed -= OnProjectsDirChanged;
            _fileWatcher.Created -= OnProjectsDirChanged;
            _fileWatcher.Renamed -= OnProjectsDirChanged;
            _fileWatcher.Dispose();
            _fileWatcher = null;
        }

        _timer.Tick -= OnTimerTick;
        _timer.Stop();
        _timer.Dispose();
    }
}

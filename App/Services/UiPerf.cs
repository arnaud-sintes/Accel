namespace Accel.App.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

/// <summary>
/// Always-on, cheap diagnostics for "the window is slow": it writes only the slow events to
/// <c>%USERPROFILE%\.claude\accel-perf.log</c> (capped, rotated), so a report of sluggishness comes with the
/// answer to <i>what</i> held the UI thread instead of a guess.
///
/// <para>What it records (each only when over its threshold):</para>
/// <list type="bullet">
/// <item>work the app posts to the UI thread (<see cref="WpfUiThreadDispatcher.Post"/>), labelled with the method
/// that queued it - snapshot apply per panel, focus-change handlers, file/git refreshes;</item>
/// <item>every dispatcher operation, by priority and delegate - which includes WPF's own input, layout and render
/// passes, i.e. how long the window spent measuring/painting;</item>
/// <item>UI-thread stalls seen from a timer (<see cref="InstallStallProbe"/>);</item>
/// <item>input latency: how long a mouse press waited between the OS stamping it and WPF seeing it (that gap is
/// time spent <i>outside</i> the dispatcher - another window's thread, or a blocked message pump), and how long a
/// context menu took from the right-button release to being opened;</item>
/// <item>the terminal page's own slow attaches and long main-thread tasks (posted by <c>terminal.js</c>).</item>
/// </list>
///
/// <para>Off by default and inert until <see cref="Install"/> runs (so unit tests and the smoke-test harnesses
/// neither pay for it nor write the user's log). Writing is done on a background thread: the UI thread only
/// enqueues a string, and only for events that were already slow.</para>
/// </summary>
public static class UiPerf
{
    /// <summary>Default threshold for a measured scope, in milliseconds.</summary>
    public const double ScopeThresholdMs = 30;

    /// <summary>Threshold for a whole dispatcher operation, in milliseconds.</summary>
    public const double OperationThresholdMs = 60;

    /// <summary>A UI-thread stall (timer drift) is reported from this many milliseconds.</summary>
    public const double StallThresholdMs = 150;

    private const long MaxLogBytes = 1024 * 1024;

    private static Channel<string>? _channel;
    private static string _path = DefaultPath();

    /// <summary>When set, receives every log line instead of the file (tests).</summary>
    internal static Action<string>? Sink { get; set; }

    /// <summary>Whether measuring is active. False until <see cref="Install"/> (or a test sets
    /// <see cref="Sink"/> and calls <see cref="EnableForTests"/>).</summary>
    public static bool Enabled { get; private set; }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "accel-perf.log");

    internal static void EnableForTests(bool enabled) => Enabled = enabled;

    // --- logging -------------------------------------------------------------------------------------------

    /// <summary>Records a line, timestamped. A no-op while disabled.</summary>
    public static void Log(string text)
    {
        if (!Enabled)
        {
            return;
        }

        string line = $"{DateTime.Now:HH:mm:ss.fff}  {text}";
        if (Sink is { } sink)
        {
            sink(line);
            return;
        }

        _channel?.Writer.TryWrite(line);
    }

    private static void StartWriter(string path)
    {
        _path = path;
        _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var reader = _channel.Reader;

        _ = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                RotateIfLarge(path);
                var batch = new List<string>();
                while (await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    batch.Clear();
                    while (reader.TryRead(out var line))
                    {
                        batch.Add(line);
                    }

                    File.AppendAllLines(path, batch);
                    RotateIfLarge(path);
                }
            }
            catch (Exception)
            {
                // Diagnostics only: a log that cannot be written must never affect the app.
            }
        });
    }

    private static void RotateIfLarge(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
            {
                File.Move(path, path + ".1", overwrite: true);
            }
        }
        catch (Exception)
        {
            // Best-effort.
        }
    }

    // --- measuring -----------------------------------------------------------------------------------------

    /// <summary>Times a block: <c>using (UiPerf.Measure("name")) { ... }</c>. Logs when it ran at least
    /// <paramref name="thresholdMs"/>. Free when disabled.</summary>
    public static Scope Measure(string name, double thresholdMs = ScopeThresholdMs) =>
        Enabled ? new Scope(name, null, thresholdMs) : default;

    /// <summary>Times the run of <paramref name="source"/>, labelled by the method it is (computed only if it
    /// turns out slow).</summary>
    public static Scope Measure(Delegate source, double thresholdMs = ScopeThresholdMs) =>
        Enabled ? new Scope(null, source, thresholdMs) : default;

    public readonly struct Scope : IDisposable
    {
        private readonly long _start;
        private readonly string? _name;
        private readonly Delegate? _source;
        private readonly double _thresholdMs;

        internal Scope(string? name, Delegate? source, double thresholdMs)
        {
            _start = Stopwatch.GetTimestamp();
            _name = name;
            _source = source;
            _thresholdMs = thresholdMs;
        }

        public void Dispose()
        {
            if (_name is null && _source is null)
            {
                return;
            }

            double ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            if (ms >= _thresholdMs)
            {
                Log($"{ms,6:F0} ms  {_name ?? LabelOf(_source!)}");
            }
        }
    }

    /// <summary>A readable name for a queued delegate: <c>RootsPanelViewModel.OnSnapshotAvailable</c> for the
    /// compiler-generated lambda inside it.</summary>
    internal static string LabelOf(Delegate? d)
    {
        if (d is null)
        {
            return "?";
        }

        try
        {
            var method = d.Method;
            var type = method.DeclaringType;
            string typeName = type?.Name ?? "?";
            if (type?.DeclaringType is { } outer && typeName.StartsWith('<'))
            {
                typeName = outer.Name; // closure class nested in the real type
            }

            string name = method.Name;
            int close = name.IndexOf('>');
            if (name.StartsWith('<') && close > 1)
            {
                name = name[1..close]; // "<OnSnapshotAvailable>b__0" -> "OnSnapshotAvailable"
            }

            return $"{typeName}.{name}";
        }
        catch (Exception)
        {
            return d.GetType().Name;
        }
    }

    // --- installing ----------------------------------------------------------------------------------------

    private static readonly FieldInfo? OperationMethod =
        typeof(DispatcherOperation).GetField("_method", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly Stack<(long Start, DispatcherOperation Operation)> Running = new();

    /// <summary>Turns the diagnostics on for <paramref name="dispatcher"/>'s thread and writes the log header.
    /// Call once, on the UI thread, at startup.</summary>
    public static void Install(Dispatcher dispatcher, string version, string? path = null)
    {
        if (Enabled)
        {
            return;
        }

        Enabled = true;
        StartWriter(path ?? DefaultPath());
        Log($"=== Accel {version} perf log | render tier {RenderCapability.Tier >> 16} | " +
            $"{Environment.ProcessorCount} cpus | {RuntimeInformation()} ===");

        dispatcher.Hooks.OperationStarted += (_, e) =>
        {
            if (Running.Count > 64)
            {
                Running.Clear(); // an operation that never reported completion must not grow this forever
            }

            Running.Push((Stopwatch.GetTimestamp(), e.Operation));
        };

        dispatcher.Hooks.OperationCompleted += (_, e) =>
        {
            if (Running.Count == 0)
            {
                return;
            }

            var (start, operation) = Running.Pop();
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (ms >= OperationThresholdMs)
            {
                var method = OperationMethod?.GetValue(operation) as Delegate;
                Log($"{ms,6:F0} ms  dispatcher op [{operation.Priority}] {LabelOf(method)}");
            }
        };

        InstallStallProbe(dispatcher);
        InstallInputProbes();
    }

    private static string RuntimeInformation() =>
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

    /// <summary>A timer that should tick every 100 ms; when it ticks much later, the UI thread was busy (or the
    /// process was suspended) for that long. Complements the per-operation log: it also catches a thread
    /// that is blocked inside a single native call, which no managed hook sees.</summary>
    private static void InstallStallProbe(Dispatcher dispatcher)
    {
        const int intervalMs = 100;
        long last = Stopwatch.GetTimestamp();
        var timer = new DispatcherTimer(DispatcherPriority.Input, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(intervalMs),
        };
        timer.Tick += (_, _) =>
        {
            double late = Stopwatch.GetElapsedTime(last).TotalMilliseconds - intervalMs;
            last = Stopwatch.GetTimestamp();
            if (late >= StallThresholdMs)
            {
                Log($"{late + intervalMs,6:F0} ms  UI thread unresponsive (timer ran {late:F0} ms late)");
            }
        };
        timer.Start();
    }

    private static long _lastRightUpTimestamp;

    /// <summary>Input latency and context-menu open time, observed on every window of the app.</summary>
    private static void InstallInputProbes()
    {
        EventManager.RegisterClassHandler(typeof(UIElement), UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler((_, e) => NoteInput("mouse down " + e.ChangedButton, e.Timestamp)), handledEventsToo: true);

        EventManager.RegisterClassHandler(typeof(UIElement), UIElement.PreviewMouseUpEvent,
            new MouseButtonEventHandler((_, e) =>
            {
                NoteInput("mouse up " + e.ChangedButton, e.Timestamp);
                if (e.ChangedButton == MouseButton.Right)
                {
                    _lastRightUpTimestamp = Stopwatch.GetTimestamp();
                }
            }), handledEventsToo: true);

        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler((_, _) =>
            {
                long since = _lastRightUpTimestamp;
                if (since == 0)
                {
                    return;
                }

                double ms = Stopwatch.GetElapsedTime(since).TotalMilliseconds;
                _lastRightUpTimestamp = 0;
                if (ms >= 100)
                {
                    Log($"{ms,6:F0} ms  context menu: right button released -> menu opened");
                }
            }));
    }

    /// <summary>Logs a mouse event that reached WPF long after the OS stamped it. That gap is time the event spent
    /// before the dispatcher: queued behind a blocked message pump, or behind another thread's window.</summary>
    private static void NoteInput(string what, int osTimestamp)
    {
        // Environment.TickCount and the input timestamp are the same clock (milliseconds since boot, wrapping).
        int latency = unchecked(Environment.TickCount - osTimestamp);
        if (latency >= 80 && latency < 60_000)
        {
            Log($"{latency,6} ms  input latency: {what} reached WPF {latency} ms after the OS stamped it");
        }
    }
}

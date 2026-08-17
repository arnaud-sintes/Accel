namespace Accel.App.Services;

using System;
using System.Windows.Threading;

/// <summary>
/// The one-shot timer mechanism behind <see cref="Accel.Cli.DebounceCoalescer"/>, abstracted so
/// the coalescer can be reused verbatim by <see cref="TelemetryFeed"/> without dragging a real
/// wall-clock timer into unit tests. <c>MonitorForm</c> injects a
/// <c>System.Windows.Forms.Timer</c> into the same two delegates
/// (<c>restartTimer</c>/<c>stopTimer</c>); this is the WPF equivalent.
/// </summary>
public interface IDebounceTimer : IDisposable
{
    /// <summary>Raised when the debounce window elapses without being restarted.</summary>
    event Action? Tick;

    /// <summary>Stop-then-start, i.e. restart the debounce window (the coalescer's
    /// <c>restartTimer</c> delegate).</summary>
    void Restart();

    /// <summary>Stop the timer (the coalescer's <c>stopTimer</c> delegate).</summary>
    void Stop();
}

/// <summary>
/// Production <see cref="IDebounceTimer"/>: a WPF <see cref="DispatcherTimer"/> at exactly the
/// same 250 ms interval <c>MonitorForm</c>'s <c>System.Windows.Forms.Timer</c> uses. This is the
/// only throttling mechanism in the WPF telemetry path - there is deliberately no second timer,
/// no polling loop, and no HTTP refresh tick anywhere in <see cref="TelemetryFeed"/>.
/// </summary>
public sealed class DispatcherDebounceTimer : IDebounceTimer
{
    /// <summary>
    /// 250 ms, unchanged from <c>MonitorForm</c>'s <c>_debounceTimer.Interval</c> (locked-in
    /// decision 8 requires the debounce window stay exactly as it is today). Comfortably
    /// coalesces a burst of hook/statusline POSTs or file-system events into one rebuild.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public DispatcherDebounceTimer(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = Interval };
        _timer.Tick += OnTick;
    }

    public event Action? Tick;

    private void OnTick(object? sender, EventArgs e) => Tick?.Invoke();

    public void Restart()
    {
        if (_disposed)
        {
            return;
        }

        _timer.Stop();
        _timer.Start();
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        _timer.Stop();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}

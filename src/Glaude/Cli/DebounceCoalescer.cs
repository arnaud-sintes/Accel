namespace Glaude.Cli;

using System;

/// <summary>
/// Pure debounce/coalescing helper backing <see cref="MonitorForm"/>'s event-driven refresh.
///
/// This is deliberately NOT a polling loop: nothing here periodically checks anything on its
/// own. It only reacts to <see cref="Signal"/> calls (one per "something changed" notification -
/// e.g. <c>SessionState.Changed</c> firing, or a <c>FileSystemWatcher</c> event) by asking the
/// caller to (re)start a one-shot timer via <paramref name="restartTimer"/>. Because many signals
/// can arrive in a rapid burst (many hook POSTs per second, or a burst of file-system events),
/// each new <see cref="Signal"/> restarts that timer rather than letting it fire immediately, so
/// the actual rebuild only happens once no new signal has arrived for a full debounce window -
/// coalescing a burst into a single refresh instead of one refresh per signal. A polling loop, by
/// contrast, would re-check on a fixed schedule regardless of whether anything changed; this
/// class never fires unless <see cref="Signal"/> was actually called.
///
/// Kept free of any WinForms/<see cref="System.Timers.Timer"/> dependency - the actual timer
/// mechanism is injected as two delegates, so this class is fully unit-testable by calling
/// <see cref="Signal"/>/<see cref="Elapsed"/> directly and asserting on the delegate calls.
/// </summary>
public sealed class DebounceCoalescer
{
    private readonly Action _restartTimer;
    private readonly Action _stopTimer;
    private bool _pending;

    public DebounceCoalescer(Action restartTimer, Action stopTimer)
    {
        _restartTimer = restartTimer ?? throw new ArgumentNullException(nameof(restartTimer));
        _stopTimer = stopTimer ?? throw new ArgumentNullException(nameof(stopTimer));
    }

    /// <summary>Number of pending, not-yet-fired signals collapsed into "at least one" - exposed
    /// only for tests; production callers only care about <see cref="Elapsed"/>'s return value.</summary>
    public bool HasPendingSignal => _pending;

    /// <summary>Call every time a "something changed" notification arrives. Marks a rebuild as
    /// owed and (re)starts the debounce window, collapsing this signal with any others that
    /// arrive before the window elapses.</summary>
    public void Signal()
    {
        _pending = true;
        _restartTimer();
    }

    /// <summary>Call when the underlying one-shot timer actually fires. Stops the timer and
    /// returns <c>true</c> exactly when at least one <see cref="Signal"/> arrived since the last
    /// call to <see cref="Elapsed"/> - i.e. "yes, a rebuild is actually owed now" - clearing the
    /// pending flag either way.</summary>
    public bool Elapsed()
    {
        _stopTimer();

        if (!_pending)
        {
            return false;
        }

        _pending = false;
        return true;
    }
}

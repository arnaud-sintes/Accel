namespace Glaude.App.Services;

using System;
using System.Windows.Threading;

/// <summary>
/// The one seam through which every background-thread telemetry signal gets onto the WPF UI
/// thread (P1-T2 / locked-in decision 8). Exists as an interface purely so the feed and the
/// ViewModel are unit-testable headlessly: production uses <see cref="WpfUiThreadDispatcher"/>
/// (a real <see cref="Dispatcher"/> + <see cref="Dispatcher.BeginInvoke(Delegate, object[])"/>),
/// tests use an inline/immediate double, and neither the feed nor the ViewModel ever references
/// WinForms' <c>Control.Invoke</c>/<c>BeginInvoke</c>.
/// </summary>
public interface IUiThreadDispatcher
{
    /// <summary>True when the calling thread is the UI thread this dispatcher belongs to.</summary>
    bool IsOnUiThread { get; }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread. When already on that thread the action
    /// runs inline (synchronously) - this mirrors <c>MonitorForm</c>, whose own on-UI-thread
    /// callers (<c>OnLoad</c>, the debounce <c>Tick</c>) call <c>RefreshAndRender</c> directly
    /// while only its genuinely cross-thread callers go through <c>BeginInvoke</c>. Off-thread
    /// callers are posted asynchronously and must not assume the action has run on return.
    /// </summary>
    void Post(Action action);
}

/// <summary>
/// The production <see cref="IUiThreadDispatcher"/>: a thin wrapper over a WPF
/// <see cref="Dispatcher"/>. Deliberately WPF-only - the WinForms
/// <c>Control.BeginInvoke</c> path used by <c>MonitorForm</c> is not reused here (locked-in
/// decision 8: the new panel layer is WPF, and the WinForms window is retired in CX-T1).
/// </summary>
public sealed class WpfUiThreadDispatcher : IUiThreadDispatcher
{
    private readonly Dispatcher _dispatcher;

    /// <summary>Wraps an explicit dispatcher (used by tests/hosts that own their own).</summary>
    public WpfUiThreadDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>
    /// Wraps <c>Application.Current.Dispatcher</c> when an <see cref="System.Windows.Application"/>
    /// exists, falling back to the current thread's dispatcher otherwise (the app's single
    /// <see cref="System.Windows.Application"/> instance is constructed manually by
    /// <c>Program.cs</c>, so on very early startup paths <c>Application.Current</c> can still be
    /// null).
    /// </summary>
    public static WpfUiThreadDispatcher ForCurrentApplication() =>
        new(System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);

    public bool IsOnUiThread => _dispatcher.CheckAccess();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Dispatcher already shut down (window closed while a hook POST / file-system event
            // was in flight) - same "best-effort, never crash the signal path" contract
            // MonitorForm.SignalOnUiThread has for a torn-down handle.
        }
    }
}

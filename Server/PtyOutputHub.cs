namespace Accel.Server;

using Accel.Orchestration;

/// <summary>
/// The one consumer of a session's output stream, shared by every WebSocket that attaches to the tab.
///
/// <para>Panel D reuses a single xterm.js across tabs, and a session's output channel is read once, so
/// without this a terminal attaching to a running session never learns the modes the app switched on at
/// startup (alternate screen, mouse reporting, ...) and keeps whatever the previous tab left behind. The
/// hub fixes that in three coordinated ways:</para>
/// <list type="number">
/// <item><b>Mode tracking at the consumer.</b> Every chunk goes through a <see cref="TerminalModeTracker"/>
/// <i>before</i> it is sent, so the tracked state is exactly what has been handed to a client - a chunk
/// the client later throws away (a socket closing under it) still updates the state, and output still
/// queued in the channel is not applied early.</item>
/// <item><b>One reader at a time.</b> A new attach cancels the previous pump and waits for it to leave
/// before it reads, so two sockets (a fast tab A-B-A switch, or a reconnect before the server noticed
/// the dead socket) never compete for chunks of a single-reader channel. That exclusivity is also what
/// makes the tracker safe without its own lock.</item>
/// <item><b>Reset + replay prefix, then a redraw.</b> Each attach first sends <c>ESC c</c> plus the
/// tracked modes (see <see cref="TerminalModeTracker.BuildReplay"/>) - in-band, so it is ordered with the
/// output instead of racing queued writes in the browser - and then jiggles the pseudoconsole size so the
/// child repaints into the freshly reset terminal.</item>
/// </list>
/// </summary>
public sealed class PtyOutputHub
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TerminalModeTracker _modes = new();
    private readonly object _sync = new();

    private CancellationTokenSource? _owner;
    private (int Columns, int Rows)? _size;
    private bool _prefixSent;
    private bool _redrawPending;

    public PtyOutputHub(IPtyEndpoint endpoint)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public IPtyEndpoint Endpoint { get; }

    /// <summary>
    /// Pumps the session's output to <paramref name="send"/> until the channel completes or
    /// <paramref name="connection"/> / a newer attach cancels it. The latest attach wins.
    /// </summary>
    public async Task PumpOutputAsync(Func<string, CancellationToken, Task> send, CancellationToken connection)
    {
        ArgumentNullException.ThrowIfNull(send);

        using var lease = CancellationTokenSource.CreateLinkedTokenSource(connection);
        var previous = Interlocked.Exchange(ref _owner, lease);
        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The previous pump already finished and disposed its lease.
        }

        // Waits for the superseded pump to have fully left the channel; cancelled itself if an even
        // newer attach takes over while we wait (the caller treats that as a normal teardown).
        await _gate.WaitAsync(lease.Token).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                _prefixSent = false;
                _redrawPending = true;
            }

            await send(_modes.BuildReplay(), lease.Token).ConfigureAwait(false);

            bool redrawNow;
            lock (_sync)
            {
                _prefixSent = true;
                redrawNow = TakeRedrawIfReady();
            }

            if (redrawNow)
            {
                Redraw();
            }

            await foreach (string chunk in Endpoint.Output.ReadAllAsync(lease.Token).ConfigureAwait(false))
            {
                _modes.Observe(chunk);
                await send(chunk, lease.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
            Interlocked.CompareExchange(ref _owner, null, lease);
        }
    }

    /// <summary>
    /// A client resize. Always forwarded; remembered so the redraw knows the size; and if this attach's
    /// prefix has already gone out, it is also what triggers the redraw (the resize frame can arrive
    /// before or after the prefix is sent - whichever is second does the redraw).
    /// </summary>
    public void Resize(int columns, int rows)
    {
        Endpoint.Resize(columns, rows);

        bool redrawNow;
        lock (_sync)
        {
            _size = (columns, rows);
            redrawNow = TakeRedrawIfReady();
        }

        if (redrawNow)
        {
            Redraw();
        }
    }

    // Caller holds _sync. The redraw needs both the prefix delivered (so the repaint lands after the
    // reset, not before it) and a known size.
    private bool TakeRedrawIfReady()
    {
        if (!_redrawPending || !_prefixSent || _size is null)
        {
            return false;
        }

        _redrawPending = false;
        return true;
    }

    // A same-size ResizePseudoConsole need not reach the child as a change, so shrink by one column and
    // restore: the child sees two real size changes and repaints its screen into the reset terminal.
    private void Redraw()
    {
        (int columns, int rows) = _size!.Value;
        if (columns <= 1)
        {
            return;
        }

        try
        {
            Endpoint.Resize(columns - 1, rows);
            Endpoint.Resize(columns, rows);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The session is going away; a missed repaint is moot.
        }
    }
}

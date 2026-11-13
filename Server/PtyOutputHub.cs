namespace Accel.Server;

using System.Collections.Generic;
using System.Threading.Channels;
using Accel.Orchestration;

/// <summary>
/// The single owner of a session's output stream, shared by every WebSocket that attaches to the tab.
///
/// <para><b>It reads all the time, not only while a tab is shown.</b> A session's output channel is bounded
/// and its pump blocks when it is full, which is real backpressure: the pseudo-console stops being drained,
/// conhost blocks, and the child (Claude Code, a Node program that writes to the console synchronously)
/// freezes. Panel D reuses one xterm for every tab, so only the SELECTED tab has a socket; with a reader
/// that existed only per socket, a busy session in an unselected tab filled its queue within a minute or
/// two and froze until it was selected again. The hub therefore starts a permanent reader when it is
/// created. Backpressure now reaches the child only through the one tab being watched (a slow WebView), never
/// through a tab nobody is looking at.</para>
///
/// <para>The permanent reader also:</para>
/// <list type="bullet">
/// <item><b>tracks the terminal modes</b> (alternate screen, mouse reporting, ...) with a
/// <see cref="TerminalModeTracker"/> over every byte, so a terminal that attaches mid-run can be brought to
/// the session's real state;</item>
/// <item><b>discards output while no tab is attached and the session is in the alternate screen</b> (Claude
/// Code): a delta stream replayed after a reset is meaningless, and the repaint on attach rebuilds the
/// screen;</item>
/// <item><b>keeps a bounded ring of recent output while no tab is attached and the session is on the normal
/// screen</b> (a shell), replayed on attach so a command that ran in the background is not lost.</item>
/// </list>
///
/// <para><b>Attach</b> (see <see cref="Attach"/>): installs the new output sink first - superseding the
/// previous one, so two sockets never compete - and returns the reset + mode replay (+ ring). The client sends a
/// resize on every open, and a (same-size or not) <c>ResizePseudoConsole</c> makes conhost repaint the whole
/// screen; because the sink already exists, that repaint is queued behind the replay, so the order is always
/// reset, modes, then the screen. (An earlier version forced the repaint by shrinking the pseudo-console one
/// column and restoring it. Measured against a real ConPTY: conhost does not reflow the alternate screen, it
/// copies rows into a narrower buffer, so the last column of every full row was lost until the child
/// repainted that row - the "missing last letter". A same-size resize repaints losslessly.)</para>
/// </summary>
public sealed class PtyOutputHub : IDisposable
{
    /// <summary>Characters of recent normal-screen output kept while no tab is attached.</summary>
    internal const int RingCapacityChars = 256 * 1024;

    /// <summary>Chunks queued for the attached socket before the reader waits (backpressure on the watched tab only).</summary>
    private const int SinkCapacity = 256;

    private readonly object _sync = new();
    private readonly object _resizeSync = new();
    private readonly TerminalModeTracker _modes = new();
    private readonly OutputRing _ring = new(RingCapacityChars);
    private readonly CancellationTokenSource _lifetime = new();

    private Sink? _sink;
    private bool _finished;

    public PtyOutputHub(IPtyEndpoint endpoint)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _ = Task.Run(DrainAsync);
    }

    public IPtyEndpoint Endpoint { get; }

    /// <summary>
    /// Attaches a socket: from this call on the hub forwards output to it, any previous attachment is
    /// superseded, and the returned <see cref="PtyAttachment"/> carries the reset + mode replay to send first.
    /// Must be called before the socket's input is processed, so the repaint its resize provokes is queued
    /// behind that replay.
    /// </summary>
    public PtyAttachment Attach(CancellationToken connection)
    {
        var sink = new Sink(connection);
        string prefix;
        string? backlog = null;
        Sink? previous;
        lock (_sync)
        {
            previous = _sink;
            _sink = sink;
            prefix = _modes.BuildReplay();
            if (!_modes.InAlternateScreen)
            {
                backlog = _ring.Take();
            }

            if (_finished)
            {
                sink.Complete();
            }
        }

        previous?.Cancel();
        return new PtyAttachment(this, sink, prefix, backlog);
    }

    /// <summary>
    /// A client resize, forwarded to the pseudo-console. Serialised, because ConPtySession.Resize is not
    /// thread-safe and the input pump of a superseded socket can still be finishing a frame.
    /// </summary>
    public void Resize(int columns, int rows)
    {
        lock (_resizeSync)
        {
            Endpoint.Resize(columns, rows);
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        Sink? sink;
        lock (_sync)
        {
            sink = _sink;
            _sink = null;
        }

        sink?.Cancel();
    }

    private void Detach(Sink sink)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_sink, sink))
            {
                _sink = null;
            }
        }

        sink.Dispose();
    }

    // The permanent reader: the only consumer of Endpoint.Output.
    private async Task DrainAsync()
    {
        try
        {
            await foreach (string chunk in Endpoint.Output.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                Sink? sink;
                lock (_sync)
                {
                    _modes.Observe(chunk);
                    sink = _sink;
                    if (sink is null)
                    {
                        if (!_modes.InAlternateScreen)
                        {
                            _ring.Append(chunk);
                        }

                        continue;
                    }
                }

                try
                {
                    await sink.Channel.Writer.WriteAsync(chunk, sink.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The socket went away or was superseded mid-chunk; the next attach repaints the screen.
                }
                catch (ChannelClosedException)
                {
                    // Same: the attachment ended between reading the sink and writing to it.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
        catch (Exception)
        {
            // A faulted reader (the session is gone) ends the stream like a completed one.
        }
        finally
        {
            Sink? sink;
            lock (_sync)
            {
                _finished = true;
                sink = _sink;
            }

            sink?.Complete();
        }
    }

    /// <summary>One attachment's queue towards its socket.</summary>
    internal sealed class Sink : IDisposable
    {
        private readonly CancellationTokenSource _cts;

        public Sink(CancellationToken connection)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(connection);
            Channel = System.Threading.Channels.Channel.CreateBounded<string>(new BoundedChannelOptions(SinkCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
        }

        public Channel<string> Channel { get; }

        public CancellationToken Token => _cts.Token;

        public void Complete() => Channel.Writer.TryComplete();

        public void Cancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }
        }

        public void Dispose() => _cts.Dispose();
    }

    /// <summary>One socket's view of the hub; disposing it detaches.</summary>
    public sealed class PtyAttachment : IDisposable
    {
        private readonly PtyOutputHub _hub;
        private readonly Sink _sink;
        private readonly string _prefix;
        private readonly string? _backlog;

        internal PtyAttachment(PtyOutputHub hub, Sink sink, string prefix, string? backlog)
        {
            _hub = hub;
            _sink = sink;
            _prefix = prefix;
            _backlog = backlog;
        }

        /// <summary>
        /// Sends the replay, then every output chunk, until the session ends, the socket goes away, or a newer
        /// attachment supersedes this one (which surfaces as <see cref="OperationCanceledException"/>, a normal
        /// teardown for the caller).
        /// </summary>
        public async Task PumpAsync(Func<string, CancellationToken, Task> send)
        {
            ArgumentNullException.ThrowIfNull(send);

            await send(_prefix, _sink.Token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(_backlog))
            {
                await send(_backlog, _sink.Token).ConfigureAwait(false);
            }

            await foreach (string chunk in _sink.Channel.Reader.ReadAllAsync(_sink.Token).ConfigureAwait(false))
            {
                await send(chunk, _sink.Token).ConfigureAwait(false);
            }
        }

        public void Dispose() => _hub.Detach(_sink);
    }

    /// <summary>
    /// A bounded buffer of recent output. Whole chunks are dropped oldest-first; when anything was dropped the
    /// text is trimmed to the next line start, because a cut can land inside an escape sequence.
    /// </summary>
    internal sealed class OutputRing
    {
        private readonly Queue<string> _chunks = new();
        private readonly int _capacity;
        private int _length;
        private bool _dropped;

        public OutputRing(int capacity) => _capacity = capacity;

        public void Append(string chunk)
        {
            _chunks.Enqueue(chunk);
            _length += chunk.Length;
            while (_length > _capacity && _chunks.Count > 1)
            {
                _length -= _chunks.Dequeue().Length;
                _dropped = true;
            }
        }

        /// <summary>Returns the buffered text (or null if empty) and empties the ring.</summary>
        public string? Take()
        {
            if (_chunks.Count == 0)
            {
                return null;
            }

            string text = string.Concat(_chunks);
            bool dropped = _dropped;
            _chunks.Clear();
            _length = 0;
            _dropped = false;

            if (dropped)
            {
                int newline = text.IndexOf('\n');
                text = newline >= 0 ? text[(newline + 1)..] : string.Empty;
            }

            return text.Length == 0 ? null : text;
        }
    }
}

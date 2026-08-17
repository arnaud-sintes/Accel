namespace Accel.App.Services;

using System;
using CommunityToolkit.Mvvm.Messaging;

/// <summary>
/// The focused session changed. Broadcast through the owning <see cref="SessionSelectionService"/>'s
/// <see cref="WeakReferenceMessenger"/> (locked-in decision 8) - never raised as a plain CLR event, so a
/// reader panel that is torn down without unsubscribing (a WPF panel ViewModel being replaced, a dialog
/// closing) cannot be kept alive by the service.
/// </summary>
/// <param name="PreviousSessionId">The previously focused session id, or null.</param>
/// <param name="FocusedSessionId">The newly focused session id, or null when nothing is focused (e.g. the
/// last tab was closed).</param>
public sealed record FocusedSessionChangedMessage(string? PreviousSessionId, string? FocusedSessionId);

/// <summary>
/// P3-T1 / locked-in decision 8: the single app-wide hub for "which session is focused".
///
/// <para><b>This interface is the READ side, and it is the only thing panels A/B/E ever receive.</b> It
/// deliberately has no setter and no mutating method at all: the write side lives on the separate
/// <see cref="ISessionSelectionWriter"/>, which <see cref="SessionSelectionService.AcquireWriter"/> hands
/// out <i>exactly once</i> for the life of the service. The composition root gives that one writer to
/// <c>TabsViewModel</c> (panel C) and this interface to everyone else, so "panel C is the only writer" is
/// true by construction rather than by convention - a reader cannot mutate selection even by
/// down-casting, because the concrete type has no public mutator either (see
/// <see cref="SessionSelectionService"/>).</para>
///
/// <para>Change notification is a <see cref="FocusedSessionChangedMessage"/> on the service's own
/// <see cref="IMessenger"/>. Readers subscribe via <see cref="Subscribe"/>/<see cref="Unsubscribe"/>
/// rather than being handed the messenger, for the same reason the writer is separated out: handing out
/// the messenger would let any reader <i>send</i> a forged change message and lie to the other panels,
/// even though it still could not change <see cref="FocusedSessionId"/>.</para>
///
/// <para><b>Threading.</b> <see cref="FocusedSessionId"/> is safe to read from any thread. Writes (and
/// therefore the resulting message delivery) are expected to happen on the UI thread, which is where
/// <c>TabsViewModel</c>'s selection changes originate; <see cref="Subscribe"/> handlers are invoked
/// synchronously on the writing thread.</para>
/// </summary>
public interface ISessionSelectionService
{
    /// <summary>The focused session id - which is also the <c>PtyRegistry</c> tabId and the
    /// <c>--session-id</c> GUID `claude` was launched with (they are deliberately the same value; see
    /// <c>MainWindow.CreateSession_Click</c>) - or null when nothing is focused.</summary>
    string? FocusedSessionId { get; }

    /// <summary>Whether <paramref name="sessionId"/> is the focused one. Case-insensitive, because the
    /// same GUID reaches this service as a tabId from <c>TabsViewModel</c> and as a transcript-derived
    /// session id in panel A, and nothing guarantees those two agree on hex casing.</summary>
    bool IsFocused(string? sessionId);

    /// <summary>Registers <paramref name="handler"/> for <paramref name="recipient"/>. The registration is
    /// weak: it does not keep <paramref name="recipient"/> alive, and it lapses automatically once the
    /// recipient is collected. Registering the same recipient twice throws (CommunityToolkit's
    /// contract), so call <see cref="Unsubscribe"/> first if re-subscribing.</summary>
    void Subscribe(object recipient, Action<FocusedSessionChangedMessage> handler);

    /// <summary>Removes <paramref name="recipient"/>'s registration, if any. Safe to call twice.</summary>
    void Unsubscribe(object recipient);
}

/// <summary>
/// The write side of <see cref="ISessionSelectionService"/> - exactly one mutating method, and exactly
/// one instance per service (see <see cref="SessionSelectionService.AcquireWriter"/>). Held only by
/// <c>TabsViewModel</c>.
/// </summary>
public interface ISessionSelectionWriter
{
    /// <summary>The current value, so the writer does not need the read interface as well.</summary>
    string? FocusedSessionId { get; }

    /// <summary>Sets the focused session id (null = nothing focused) and broadcasts
    /// <see cref="FocusedSessionChangedMessage"/> if it actually changed. Idempotent: setting the value
    /// it already has broadcasts nothing.</summary>
    void SetFocused(string? sessionId);
}

/// <summary>
/// The production <see cref="ISessionSelectionService"/>: a string plus a
/// <see cref="WeakReferenceMessenger"/>, with the mutator deliberately unreachable except through the
/// single <see cref="ISessionSelectionWriter"/> that <see cref="AcquireWriter"/> yields once.
///
/// <para>The messenger defaults to a <b>private</b> <see cref="WeakReferenceMessenger"/> instance rather
/// than <see cref="WeakReferenceMessenger.Default"/>: the process-wide default would let two services
/// (or two tests running in parallel) cross-talk, and nothing here benefits from a shared bus.</para>
/// </summary>
public sealed class SessionSelectionService : ISessionSelectionService
{
    private readonly IMessenger _messenger;
    private readonly object _gate = new();
    private Writer? _writer;
    private string? _focusedSessionId;

    public SessionSelectionService(IMessenger? messenger = null) =>
        _messenger = messenger ?? new WeakReferenceMessenger();

    /// <inheritdoc />
    public string? FocusedSessionId
    {
        get
        {
            lock (_gate)
            {
                return _focusedSessionId;
            }
        }
    }

    /// <inheritdoc />
    public bool IsFocused(string? sessionId) =>
        !string.IsNullOrEmpty(sessionId) &&
        string.Equals(sessionId, FocusedSessionId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Yields the one and only writer for this service. The second call throws
    /// <see cref="InvalidOperationException"/> - that is the structural half of the single-writer
    /// invariant: even a class that somehow got hold of the concrete service cannot obtain a second
    /// write capability once <c>TabsViewModel</c> has taken it.
    /// </summary>
    public ISessionSelectionWriter AcquireWriter()
    {
        lock (_gate)
        {
            if (_writer is not null)
            {
                throw new InvalidOperationException(
                    "The selection writer has already been acquired. ISessionSelectionService has exactly one " +
                    "writer for its whole lifetime (locked-in decision 8: TabsViewModel/panel C), and every " +
                    "other consumer is a reader through ISessionSelectionService.");
            }

            _writer = new Writer(this);
            return _writer;
        }
    }

    /// <summary>Whether <see cref="AcquireWriter"/> has already been called.</summary>
    public bool HasWriter
    {
        get
        {
            lock (_gate)
            {
                return _writer is not null;
            }
        }
    }

    /// <inheritdoc />
    public void Subscribe(object recipient, Action<FocusedSessionChangedMessage> handler)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(handler);
        _messenger.Register<FocusedSessionChangedMessage>(recipient, (_, message) => handler(message));
    }

    /// <inheritdoc />
    public void Unsubscribe(object recipient)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        _messenger.Unregister<FocusedSessionChangedMessage>(recipient);
    }

    /// <summary>The mutator. Private on purpose - the only caller is <see cref="Writer"/>.</summary>
    private void SetFocusedCore(string? sessionId)
    {
        string? normalized = string.IsNullOrEmpty(sessionId) ? null : sessionId;
        string? previous;

        lock (_gate)
        {
            if (string.Equals(_focusedSessionId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            previous = _focusedSessionId;
            _focusedSessionId = normalized;
        }

        // Outside the lock: a subscriber that reads FocusedSessionId (panel A does, on every node)
        // must not re-enter a held lock, and a throwing subscriber must not leave it held.
        _messenger.Send(new FocusedSessionChangedMessage(previous, normalized));
    }

    private sealed class Writer : ISessionSelectionWriter
    {
        private readonly SessionSelectionService _owner;

        internal Writer(SessionSelectionService owner) => _owner = owner;

        public string? FocusedSessionId => _owner.FocusedSessionId;

        public void SetFocused(string? sessionId) => _owner.SetFocusedCore(sessionId);
    }
}

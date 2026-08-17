namespace Accel.App.Services;

using System;
using System.Collections.Generic;

/// <summary>
/// Per-session extra CLI arguments (permission-mode / dangerously-skip-permissions / free-text extra
/// args) chosen via "Edit launch args…", to be appended the next time that session is resumed
/// (plain or fork). A `claude` child's own argv cannot be changed once it is running - see
/// <see cref="Accel.Orchestration.PtySession"/> - so this is deliberately scoped to "what the next
/// `claude --resume` for this session id will additionally pass", not to the live process itself.
///
/// <para>In-memory and keyed by session id only: it does not survive an app restart, the same
/// lifetime as every other panel-A/tab-strip in-memory state (<c>PtyRegistry</c>, the roots tree).
/// Thread-safety is a plain lock rather than a concurrent collection - reads/writes are rare,
/// user-driven UI events, never a hot path.</para>
/// </summary>
public sealed class SessionResumeArgsStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string[]> _argsBySessionId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records <paramref name="arguments"/> as the extra args for <paramref name="sessionId"/>'s
    /// next resume, replacing whatever was recorded before. An empty array clears it (equivalent to
    /// never having set anything), so a user can dial a session's overrides back to none.</summary>
    public void Set(string sessionId, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(arguments);

        lock (_gate)
        {
            if (arguments.Count == 0)
            {
                _argsBySessionId.Remove(sessionId);
                return;
            }

            _argsBySessionId[sessionId] = arguments is string[] array ? array : new List<string>(arguments).ToArray();
        }
    }

    /// <summary>The extra args recorded for <paramref name="sessionId"/>, or an empty array if none were
    /// ever set (or they were cleared). Never returns null, so callers can always append the result
    /// unconditionally.</summary>
    public string[] Get(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        lock (_gate)
        {
            return _argsBySessionId.TryGetValue(sessionId, out var arguments) ? arguments : Array.Empty<string>();
        }
    }
}

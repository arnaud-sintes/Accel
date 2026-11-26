namespace Accel.Metrics;

/// <summary>Whether a running session is busy or waiting for its next prompt. <see cref="None"/> for anything that
/// is not a running session (a sleeping one has nothing to indicate).</summary>
public enum SessionActivity
{
    None,

    /// <summary>The turn is over; the session is waiting for the user's next prompt.</summary>
    Idle,

    /// <summary>The main agent or a sub-agent is working.</summary>
    Working,
}

/// <summary>
/// Decides <see cref="SessionActivity"/> from what Accel can observe without any extra hook: the state of the
/// current turn at the end of the transcript (<see cref="TranscriptReader.ReadTailSummary"/>), the live sub-agents,
/// and the <c>Stop</c> hook's timestamp. Pure, so the rules are testable one by one.
/// </summary>
public static class SessionActivityResolver
{
    public const string WorkingWire = "working";
    public const string IdleWire = "idle";

    /// <param name="isLive">The session's process is running (not ended).</param>
    /// <param name="turn">The turn state read from the transcript's tail.</param>
    /// <param name="waitingSinceUtc">When the <c>Stop</c> hook last fired for the session (a turn ended), if ever.</param>
    /// <param name="transcriptLastWriteUtc">When the transcript file was last written.</param>
    /// <param name="hasLiveAgents">At least one sub-agent of the session is live.</param>
    public static SessionActivity Resolve(
        bool isLive,
        TranscriptTurnState turn,
        DateTime? waitingSinceUtc,
        DateTime transcriptLastWriteUtc,
        bool hasLiveAgents)
    {
        if (!isLive)
        {
            return SessionActivity.None;
        }

        // A sub-agent in work is the session in work, whatever the main transcript says (the main agent is
        // typically parked on the Task tool call, which the transcript reads as a pending tool_use anyway).
        if (hasLiveAgents)
        {
            return SessionActivity.Working;
        }

        return turn switch
        {
            TranscriptTurnState.Idle => SessionActivity.Idle,

            // The transcript says a turn is open. A Stop hook newer than the last write contradicts that: the
            // turn did end and the transcript simply has nothing more to say about it - trust the hook.
            TranscriptTurnState.Working when waitingSinceUtc is { } stopped && stopped >= transcriptLastWriteUtc => SessionActivity.Idle,
            TranscriptTurnState.Working => SessionActivity.Working,

            // Nothing readable: a session that has not done anything yet is waiting for its first prompt.
            _ => SessionActivity.Idle,
        };
    }

    public static string? ToWire(SessionActivity activity) => activity switch
    {
        SessionActivity.Working => WorkingWire,
        SessionActivity.Idle => IdleWire,
        _ => null,
    };

    public static SessionActivity FromWire(string? value) => value switch
    {
        WorkingWire => SessionActivity.Working,
        IdleWire => SessionActivity.Idle,
        _ => SessionActivity.None,
    };
}

namespace Accel.App.Services;

/// <summary>What double-clicking a session row in panel A does.</summary>
public enum SessionRowAction
{
    /// <summary>Nothing to do: the session is running somewhere Accel has no tab for (another terminal), so
    /// there is nothing to switch to and resuming it here would attach a second process to a live session.</summary>
    None,

    /// <summary>The session already has a tab open in Accel: bring it to the front.</summary>
    FocusTab,

    /// <summary>The session is not open in Accel (never opened, or its tab's process has ended): resume it
    /// (<c>claude --resume</c>) in its own tab.</summary>
    Resume,
}

/// <summary>
/// The rule behind panel A's session double-click, separate from the window so it can be tested:
/// open and alive in Accel → show that tab; not open → resume it. A running session is never stopped by
/// the gesture (stopping is the tab strip's own double-click and the tab's menu).
/// </summary>
public static class SessionRowGesture
{
    /// <param name="hasLiveTab">Accel has a tab for the session and its process has not ended. A tab whose
    /// process ended still exists in the strip but counts as not open: double-clicking resumes into it.</param>
    /// <param name="sessionIsRunning">Telemetry says the session's process is alive (anywhere).</param>
    public static SessionRowAction OnDoubleClick(bool hasLiveTab, bool sessionIsRunning) =>
        hasLiveTab ? SessionRowAction.FocusTab
        : sessionIsRunning ? SessionRowAction.None
        : SessionRowAction.Resume;
}

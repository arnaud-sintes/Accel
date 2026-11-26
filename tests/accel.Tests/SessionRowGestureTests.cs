namespace Accel.Tests;

using Accel.App.Services;
using Xunit;

/// <summary>Panel A's session double-click: show the tab if the session is open in Accel, otherwise resume it.</summary>
public class SessionRowGestureTests
{
    [Theory]
    [InlineData(true)]   // the normal case for a session started in Accel
    [InlineData(false)]  // tab open before telemetry caught up
    public void ASessionWithALiveTab_FocusesThatTab_NeverStopsOrResumesIt(bool sessionIsRunning)
    {
        Assert.Equal(SessionRowAction.FocusTab, SessionRowGesture.OnDoubleClick(hasLiveTab: true, sessionIsRunning));
    }

    [Fact]
    public void ASessionNotOpenInAccel_IsResumed()
    {
        Assert.Equal(SessionRowAction.Resume, SessionRowGesture.OnDoubleClick(hasLiveTab: false, sessionIsRunning: false));
    }

    [Fact]
    public void ASessionRunningOutsideAccel_IsLeftAlone()
    {
        // No tab to show, and a second `claude --resume` against a live session is not wanted.
        Assert.Equal(SessionRowAction.None, SessionRowGesture.OnDoubleClick(hasLiveTab: false, sessionIsRunning: true));
    }
}

namespace Accel.Tests;

using Accel.App.Services;
using Accel.App.ViewModels;
using Xunit;

/// <summary>
/// P3-T3: panels B/E's stub reader. Just proves the wiring - subscribes to
/// <see cref="ISessionSelectionService"/>, reflects its current value, updates on change, and unsubscribes
/// on dispose - since the panels themselves have no real content yet (Phases 5/6).
/// </summary>
public class FocusedSessionStubViewModelTests
{
    [Fact]
    public void Constructed_WithNoFocusedSession_ShowsNoSessionFocused()
    {
        var selection = new SessionSelectionService();
        using var stub = new FocusedSessionStubViewModel(selection);

        Assert.Equal("No session focused", stub.StatusText);
    }

    [Fact]
    public void Constructed_WithAnAlreadyFocusedSession_ShowsItImmediately()
    {
        var selection = new SessionSelectionService();
        var writer = selection.AcquireWriter();
        writer.SetFocused("session-one");

        using var stub = new FocusedSessionStubViewModel(selection);

        Assert.Equal("Focused session: session-one", stub.StatusText);
    }

    [Fact]
    public void FocusChanges_AfterConstruction_UpdateStatusText()
    {
        var selection = new SessionSelectionService();
        var writer = selection.AcquireWriter();
        using var stub = new FocusedSessionStubViewModel(selection);

        writer.SetFocused("session-two");
        Assert.Equal("Focused session: session-two", stub.StatusText);

        writer.SetFocused(null);
        Assert.Equal("No session focused", stub.StatusText);
    }

    [Fact]
    public void TwoStubs_OnTheSameService_BothUpdateIndependently()
    {
        var selection = new SessionSelectionService();
        var writer = selection.AcquireWriter();
        using var stubB = new FocusedSessionStubViewModel(selection);
        using var stubE = new FocusedSessionStubViewModel(selection);

        writer.SetFocused("shared-session");

        Assert.Equal("Focused session: shared-session", stubB.StatusText);
        Assert.Equal("Focused session: shared-session", stubE.StatusText);
    }

    [Fact]
    public void Dispose_UnsubscribesSoFurtherChangesAreNotReflected()
    {
        var selection = new SessionSelectionService();
        var writer = selection.AcquireWriter();
        var stub = new FocusedSessionStubViewModel(selection);

        stub.Dispose();
        writer.SetFocused("after-dispose");

        Assert.Equal("No session focused", stub.StatusText);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var selection = new SessionSelectionService();
        var stub = new FocusedSessionStubViewModel(selection);

        stub.Dispose();
        stub.Dispose();
    }
}

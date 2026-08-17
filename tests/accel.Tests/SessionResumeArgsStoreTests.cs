namespace Accel.Tests;

using Accel.App.Services;
using Xunit;

/// <summary>Unit tests for <see cref="SessionResumeArgsStore"/> - the in-memory sessionId -> extra-args
/// map behind "Edit launch args…".</summary>
public class SessionResumeArgsStoreTests
{
    [Fact]
    public void Get_UnknownSessionId_ReturnsEmptyArray()
    {
        var store = new SessionResumeArgsStore();

        Assert.Empty(store.Get("unknown-id"));
    }

    [Fact]
    public void SetThenGet_RoundTrips()
    {
        var store = new SessionResumeArgsStore();
        var args = new[] { "--permission-mode", "bypassPermissions" };

        store.Set("session-1", args);

        Assert.Equal(args, store.Get("session-1"));
    }

    [Fact]
    public void Set_DoesNotAffectOtherSessionIds()
    {
        var store = new SessionResumeArgsStore();
        store.Set("session-1", new[] { "--dangerously-skip-permissions" });

        Assert.Empty(store.Get("session-2"));
    }

    [Fact]
    public void Set_OverwritesPreviousValueForSameSessionId()
    {
        var store = new SessionResumeArgsStore();
        store.Set("session-1", new[] { "--permission-mode", "plan" });
        store.Set("session-1", new[] { "--permission-mode", "acceptEdits" });

        Assert.Equal(new[] { "--permission-mode", "acceptEdits" }, store.Get("session-1"));
    }

    [Fact]
    public void Set_EmptyArray_ClearsAnyPreviousValue()
    {
        var store = new SessionResumeArgsStore();
        store.Set("session-1", new[] { "--permission-mode", "plan" });

        store.Set("session-1", System.Array.Empty<string>());

        Assert.Empty(store.Get("session-1"));
    }

    [Fact]
    public void SessionIds_AreCaseInsensitive()
    {
        var store = new SessionResumeArgsStore();
        store.Set("Session-ABC", new[] { "--dangerously-skip-permissions" });

        Assert.Equal(new[] { "--dangerously-skip-permissions" }, store.Get("session-abc"));
    }
}

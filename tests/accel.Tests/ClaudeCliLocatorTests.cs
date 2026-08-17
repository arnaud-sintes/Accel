namespace Accel.Tests;

using System.Collections.Generic;
using Accel.Orchestration;
using Xunit;

public class ClaudeCliLocatorTests
{
    private const string PathEnv = @"C:\dir1;C:\dir2";

    [Fact]
    public void Resolve_FindsNativeExe()
    {
        var files = new HashSet<string> { @"C:\dir1\claude.exe" };

        var result = ClaudeCliLocator.Resolve(PathEnv, files.Contains);

        Assert.Equal(ClaudeCliResolutionKind.NativeExe, result.Kind);
        Assert.Equal(@"C:\dir1\claude.exe", result.Path);
    }

    [Theory]
    [InlineData(".cmd")]
    [InlineData(".bat")]
    [InlineData(".ps1")]
    public void Resolve_FindsShim_ForEachShimExtension(string extension)
    {
        var shimPath = @"C:\dir1\claude" + extension;
        var files = new HashSet<string> { shimPath };

        var result = ClaudeCliLocator.Resolve(PathEnv, files.Contains);

        Assert.Equal(ClaudeCliResolutionKind.Shim, result.Kind);
        Assert.Equal(shimPath, result.Path);
    }

    [Fact]
    public void Resolve_ReturnsMissing_WhenNothingFound()
    {
        var result = ClaudeCliLocator.Resolve(PathEnv, _ => false);

        Assert.Equal(ClaudeCliResolutionKind.Missing, result.Kind);
        Assert.Null(result.Path);
    }

    [Fact]
    public void Resolve_PrefersNativeExe_OverShimInSameDirectory()
    {
        var files = new HashSet<string> { @"C:\dir1\claude.exe", @"C:\dir1\claude.cmd" };

        var result = ClaudeCliLocator.Resolve(PathEnv, files.Contains);

        Assert.Equal(ClaudeCliResolutionKind.NativeExe, result.Kind);
        Assert.Equal(@"C:\dir1\claude.exe", result.Path);
    }

    /// <summary>
    /// The load-bearing invariant for this class: a self-update can replace claude.exe with a
    /// different kind of artifact (or remove it) between two separate launches. There must be no
    /// cross-call cache/memoization — calling Resolve twice in a row against two different fake
    /// filesystem snapshots must reflect each snapshot independently, not the first call's result.
    /// </summary>
    [Fact]
    public void Resolve_ReflectsFreshFilesystemState_OnEveryCall_NoCrossCallCache()
    {
        var beforeUpdate = new HashSet<string> { @"C:\dir1\claude.exe" };
        var firstCall = ClaudeCliLocator.Resolve(PathEnv, beforeUpdate.Contains);
        Assert.Equal(ClaudeCliResolutionKind.NativeExe, firstCall.Kind);
        Assert.Equal(@"C:\dir1\claude.exe", firstCall.Path);

        // Simulate a self-update replacing the native exe with a shim in place.
        var afterUpdate = new HashSet<string> { @"C:\dir1\claude.cmd" };
        var secondCall = ClaudeCliLocator.Resolve(PathEnv, afterUpdate.Contains);
        Assert.Equal(ClaudeCliResolutionKind.Shim, secondCall.Kind);
        Assert.Equal(@"C:\dir1\claude.cmd", secondCall.Path);

        // And a third call where the update removed claude entirely must report Missing, not
        // fall back to either prior observation.
        var afterRemoval = new HashSet<string>();
        var thirdCall = ClaudeCliLocator.Resolve(PathEnv, afterRemoval.Contains);
        Assert.Equal(ClaudeCliResolutionKind.Missing, thirdCall.Kind);
        Assert.Null(thirdCall.Path);
    }
}

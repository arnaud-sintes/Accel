using Accel.Versioning;
using Xunit;

namespace Accel.Tests;

/// <summary>
/// Phase 3c: <see cref="VersionGate.ShouldRegisterSubagentStatusLine"/> gates whether the
/// `subagentStatusLine` hook is worth registering at all - true from v2.1.205 onward (the
/// version at which `model`/`contextWindowSize` become available), false below it or when the
/// version is unknown/unparseable.
/// </summary>
public class ShouldRegisterSubagentStatusLineTests
{
    [Fact]
    public void NullVersion_ReturnsFalse()
    {
        Assert.False(VersionGate.ShouldRegisterSubagentStatusLine(null));
    }

    [Fact]
    public void VersionBelowThreshold_ReturnsFalse()
    {
        var version = new ClaudeVersion(2, 1, 204);
        Assert.False(VersionGate.ShouldRegisterSubagentStatusLine(version));
    }

    [Fact]
    public void VersionAtThreshold_ReturnsTrue()
    {
        var version = new ClaudeVersion(2, 1, 205);
        Assert.True(VersionGate.ShouldRegisterSubagentStatusLine(version));
    }

    [Fact]
    public void VersionAboveThreshold_ReturnsTrue()
    {
        var version = new ClaudeVersion(2, 1, 224);
        Assert.True(VersionGate.ShouldRegisterSubagentStatusLine(version));
    }

    [Fact]
    public void OlderMajorMinor_ReturnsFalse()
    {
        var version = new ClaudeVersion(1, 9, 999);
        Assert.False(VersionGate.ShouldRegisterSubagentStatusLine(version));
    }

    [Fact]
    public void NewerMajor_ReturnsTrue()
    {
        var version = new ClaudeVersion(3, 0, 0);
        Assert.True(VersionGate.ShouldRegisterSubagentStatusLine(version));
    }
}

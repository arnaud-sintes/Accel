namespace Accel.Tests;

using Xunit;
using Accel.Versioning;

public class ClaudeVersionTests
{
    [Fact]
    public void TryParse_ValidVersion_Succeeds()
    {
        // Arrange
        var input = "2.1.224 (Claude Code)";

        // Act
        var result = ClaudeVersion.TryParse(input, out var version);

        // Assert
        Assert.True(result);
        Assert.Equal(2, version.Major);
        Assert.Equal(1, version.Minor);
        Assert.Equal(224, version.Patch);
    }

    [Fact]
    public void TryParse_ValidVersionWithoutSuffix_Succeeds()
    {
        // Arrange
        var input = "2.1.205";

        // Act
        var result = ClaudeVersion.TryParse(input, out var version);

        // Assert
        Assert.True(result);
        Assert.Equal(2, version.Major);
        Assert.Equal(1, version.Minor);
        Assert.Equal(205, version.Patch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("2.1")]
    [InlineData("2.x.224")]
    [InlineData("abc.def.ghi")]
    public void TryParse_InvalidInput_ReturnsFalse(string? input)
    {
        // Act
        var result = ClaudeVersion.TryParse(input, out _);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void TryParse_DoesNotThrowOnGarbageInput()
    {
        // Act & Assert - should not throw
        var result = ClaudeVersion.TryParse("!@#$%^&*()", out _);
        Assert.False(result);
    }

    [Fact]
    public void CompareTo_VersionGreater_ReturnsPositive()
    {
        // Arrange
        var v1 = new ClaudeVersion(2, 1, 224);
        var v2 = new ClaudeVersion(2, 1, 205);

        // Act & Assert
        Assert.True(v1 > v2);
        Assert.True(v1 >= v2);
        Assert.False(v1 < v2);
        Assert.False(v1 <= v2);
    }

    [Fact]
    public void CompareTo_VersionLess_ReturnsNegative()
    {
        // Arrange
        var v1 = new ClaudeVersion(2, 1, 132);
        var v2 = new ClaudeVersion(2, 1, 196);

        // Act & Assert
        Assert.True(v1 < v2);
        Assert.True(v1 <= v2);
        Assert.False(v1 > v2);
        Assert.False(v1 >= v2);
    }

    [Fact]
    public void CompareTo_VersionEqual_ReturnsZero()
    {
        // Arrange
        var v1 = new ClaudeVersion(2, 1, 205);
        var v2 = new ClaudeVersion(2, 1, 205);

        // Act & Assert
        Assert.Equal(v1, v2);
        Assert.True(v1 == v2);
        Assert.False(v1 != v2);
    }

    [Fact]
    public void CompareTo_MajorVersionDifference()
    {
        // Arrange
        var v1 = new ClaudeVersion(3, 0, 0);
        var v2 = new ClaudeVersion(2, 1, 224);

        // Act & Assert
        Assert.True(v1 > v2);
    }

    [Fact]
    public void CompareTo_MinorVersionDifference()
    {
        // Arrange
        var v1 = new ClaudeVersion(2, 2, 0);
        var v2 = new ClaudeVersion(2, 1, 224);

        // Act & Assert
        Assert.True(v1 > v2);
    }
}

public class VersionGateTests
{
    [Fact]
    public void Supports_NullVersion_ReturnsFalseForAllFeatures()
    {
        // Act & Assert
        Assert.False(VersionGate.Supports(null, Feature.SubagentStartEvent));
        Assert.False(VersionGate.Supports(null, Feature.SubagentStatusLineModelAndContextWindow));
        Assert.False(VersionGate.Supports(null, Feature.SubagentStatusLineEffort));
        Assert.False(VersionGate.Supports(null, Feature.ContextWindowCurrentNotCumulative));
        Assert.False(VersionGate.Supports(null, Feature.StatusLinePromptId));
    }

    [Fact]
    public void Supports_SubagentStartEvent_Threshold()
    {
        // Arrange
        var versionBefore = new ClaudeVersion(2, 1, 223);
        var versionAt = new ClaudeVersion(2, 1, 224);
        var versionAfter = new ClaudeVersion(2, 1, 225);

        // Act & Assert
        Assert.False(VersionGate.Supports(versionBefore, Feature.SubagentStartEvent));
        Assert.True(VersionGate.Supports(versionAt, Feature.SubagentStartEvent));
        Assert.True(VersionGate.Supports(versionAfter, Feature.SubagentStartEvent));
    }

    [Fact]
    public void Supports_SubagentStatusLineModelAndContextWindow_Threshold()
    {
        // Arrange
        var versionBefore = new ClaudeVersion(2, 1, 204);
        var versionAt = new ClaudeVersion(2, 1, 205);
        var versionAfter = new ClaudeVersion(2, 1, 206);

        // Act & Assert
        Assert.False(VersionGate.Supports(versionBefore, Feature.SubagentStatusLineModelAndContextWindow));
        Assert.True(VersionGate.Supports(versionAt, Feature.SubagentStatusLineModelAndContextWindow));
        Assert.True(VersionGate.Supports(versionAfter, Feature.SubagentStatusLineModelAndContextWindow));
    }

    [Fact]
    public void Supports_SubagentStatusLineEffort_Threshold()
    {
        // Arrange
        var versionBefore = new ClaudeVersion(2, 1, 213);
        var versionAt = new ClaudeVersion(2, 1, 214);
        var versionAfter = new ClaudeVersion(2, 1, 215);

        // Act & Assert
        Assert.False(VersionGate.Supports(versionBefore, Feature.SubagentStatusLineEffort));
        Assert.True(VersionGate.Supports(versionAt, Feature.SubagentStatusLineEffort));
        Assert.True(VersionGate.Supports(versionAfter, Feature.SubagentStatusLineEffort));
    }

    [Fact]
    public void Supports_ContextWindowCurrentNotCumulative_Threshold()
    {
        // Arrange
        var versionBefore = new ClaudeVersion(2, 1, 131);
        var versionAt = new ClaudeVersion(2, 1, 132);
        var versionAfter = new ClaudeVersion(2, 1, 133);

        // Act & Assert
        Assert.False(VersionGate.Supports(versionBefore, Feature.ContextWindowCurrentNotCumulative));
        Assert.True(VersionGate.Supports(versionAt, Feature.ContextWindowCurrentNotCumulative));
        Assert.True(VersionGate.Supports(versionAfter, Feature.ContextWindowCurrentNotCumulative));
    }

    [Fact]
    public void Supports_StatusLinePromptId_Threshold()
    {
        // Arrange
        var versionBefore = new ClaudeVersion(2, 1, 195);
        var versionAt = new ClaudeVersion(2, 1, 196);
        var versionAfter = new ClaudeVersion(2, 1, 197);

        // Act & Assert
        Assert.False(VersionGate.Supports(versionBefore, Feature.StatusLinePromptId));
        Assert.True(VersionGate.Supports(versionAt, Feature.StatusLinePromptId));
        Assert.True(VersionGate.Supports(versionAfter, Feature.StatusLinePromptId));
    }

    [Fact]
    public void Supports_AllFeaturesWithCurrentVersion()
    {
        // Arrange - use a version that should support all current features (2.1.224 from project.md)
        var currentVersion = new ClaudeVersion(2, 1, 224);

        // Act & Assert
        Assert.True(VersionGate.Supports(currentVersion, Feature.SubagentStartEvent));
        Assert.True(VersionGate.Supports(currentVersion, Feature.SubagentStatusLineModelAndContextWindow));
        Assert.True(VersionGate.Supports(currentVersion, Feature.SubagentStatusLineEffort));
        Assert.True(VersionGate.Supports(currentVersion, Feature.ContextWindowCurrentNotCumulative));
        Assert.True(VersionGate.Supports(currentVersion, Feature.StatusLinePromptId));
    }
}

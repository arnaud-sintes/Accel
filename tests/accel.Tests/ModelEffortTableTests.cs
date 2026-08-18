namespace Accel.Tests;

using Accel.Metrics;
using Xunit;

public class ModelEffortTableTests
{
    [Theory]
    [InlineData("Sonnet")]
    [InlineData("Opus")]
    [InlineData("Fable")]
    [InlineData("sonnet")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SomeUnknownFamily")]
    public void SupportsEffort_ByFamily_TrueForEverythingButHaiku(string? family)
    {
        Assert.True(ModelEffortTable.SupportsEffort(family));
    }

    [Theory]
    [InlineData("Haiku")]
    [InlineData("haiku")]
    [InlineData("HAIKU")]
    public void SupportsEffort_ByFamily_FalseForHaiku(string family)
    {
        Assert.False(ModelEffortTable.SupportsEffort(family));
    }

    [Fact]
    public void SupportsEffort_ByBadge_FalseForHaikuLetter()
    {
        var haikuBadge = ModelBadgeTable.Resolve("claude-haiku-4-5-20251001");
        Assert.True(haikuBadge.Matched);
        Assert.False(ModelEffortTable.SupportsEffort(haikuBadge));
    }

    [Theory]
    [InlineData("claude-sonnet-5")]
    [InlineData("claude-opus-5")]
    [InlineData("claude-fable-5")]
    public void SupportsEffort_ByBadge_TrueForOtherFamilies(string modelId)
    {
        var badge = ModelBadgeTable.Resolve(modelId);
        Assert.True(badge.Matched);
        Assert.True(ModelEffortTable.SupportsEffort(badge));
    }

    [Fact]
    public void SupportsEffort_ByBadge_TrueForUnmatchedBadge()
    {
        Assert.True(ModelEffortTable.SupportsEffort(ModelBadge.Unmatched));
    }
}

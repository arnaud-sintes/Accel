using Accel.Metrics;
using Xunit;

namespace Accel.Tests;

public class EffortBarLevelTests
{
    [Theory]
    [InlineData("low", 1)]
    [InlineData("minimal", 1)]
    [InlineData("LOW", 1)]
    [InlineData(" low ", 1)]
    [InlineData("medium", 2)]
    [InlineData("mid", 2)]
    [InlineData("high", 3)]
    [InlineData("xhigh", 4)]
    [InlineData("max", 5)]
    [InlineData("maximum", 5)]
    [InlineData("highest", 5)]
    public void RecognizedEffortStrings_ResolveToExpectedBarCount(string effort, int expected)
    {
        Assert.Equal(expected, EffortBarLevel.Resolve(effort));
    }

    [Theory]
    [InlineData("?")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void UnrecognizedOrMissing_ResolvesToZero_NoThrow(string? effort)
    {
        Assert.Equal(0, EffortBarLevel.Resolve(effort));
    }

    [Fact]
    public void MaxBarsConstant_Is5()
    {
        Assert.Equal(5, EffortBarLevel.MaxBars);
    }
}

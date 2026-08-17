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
    [InlineData("max", 4)]
    [InlineData("xhigh", 4)]
    [InlineData("maximum", 4)]
    [InlineData("highest", 4)]
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
    public void MaxBarsConstant_Is4()
    {
        Assert.Equal(4, EffortBarLevel.MaxBars);
    }
}

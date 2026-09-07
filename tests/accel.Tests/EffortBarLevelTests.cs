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
    [InlineData("ultracode", 6)]
    [InlineData("ultra", 6)]
    [InlineData("ULTRACODE", 6)]
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

    /// <summary>
    /// The invariant that actually matters, replacing the old "MaxBars is 5" assertion: the badge
    /// geometry divides by <see cref="EffortBarLevel.MaxBars"/>, so a tier added to
    /// <see cref="EffortBarLevel.Levels"/> without <c>MaxBars</c> following would render the top tier
    /// as a partial moon forever. Pinning the relationship rather than the number is what lets the
    /// vocabulary grow - which it did, when "ultracode" turned out to exist.
    /// </summary>
    [Fact]
    public void MaxBars_EqualsTheNumberOfKnownLevels()
    {
        Assert.Equal(EffortBarLevel.Levels.Count, EffortBarLevel.MaxBars);
    }

    /// <summary>Every canonical spelling must resolve, and resolve in strictly ascending order - the
    /// contract <c>EffortBarsControl</c> and the Create-session dialog's ordering both rely on.</summary>
    [Fact]
    public void Levels_ResolveToStrictlyAscendingBarCounts_StartingAtOne()
    {
        var previous = 0;
        foreach (var level in EffortBarLevel.Levels)
        {
            var resolved = EffortBarLevel.Resolve(level);
            Assert.True(resolved > previous, $"'{level}' resolved to {resolved}, expected greater than {previous}.");
            previous = resolved;
        }

        Assert.Equal(EffortBarLevel.MaxBars, previous);
    }

    [Fact]
    public void Levels_ContainsTheUltracodeTierAboveMax()
    {
        Assert.Contains("ultracode", EffortBarLevel.Levels);
        Assert.True(EffortBarLevel.Resolve("ultracode") > EffortBarLevel.Resolve("max"));
    }

    [Fact]
    public void FromBarCount_RoundTripsEveryLevel()
    {
        foreach (var level in EffortBarLevel.Levels)
        {
            Assert.Equal(level, EffortBarLevel.FromBarCount(EffortBarLevel.Resolve(level)));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(99)]
    public void FromBarCount_OutOfRange_IsNull(int barCount)
    {
        Assert.Null(EffortBarLevel.FromBarCount(barCount));
    }
}

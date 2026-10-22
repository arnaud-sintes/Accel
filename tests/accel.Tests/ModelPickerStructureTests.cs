namespace Accel.Tests;

using System.Linq;
using Accel.Orchestration;
using Xunit;

/// <summary>A picker screen has a shape; one that does not is residue from somewhere else, not a list to
/// take the first match of.</summary>
public class ModelPickerStructureTests
{
    private static ModelPickerScreen Parse(params string[] lines) => ModelPickerScreenParser.Parse(lines);

    [Fact]
    public void ARealPickerScreen_IsSound()
    {
        var screen = Parse(
            "Select model",
            "  1. Default (recommended)  Sonnet 5.5 · Org default",
            "❯ 2. Opus 4.8               Best for everyday tasks",
            "  3. Sonnet 5.5             Most efficient");

        Assert.True(screen.IsUsable);
        Assert.True(screen.IsSound);
    }

    [Fact]
    public void ANumberedLineFromElsewhere_RepeatingAnOrdinal_MakesTheScreenUnsound()
    {
        var screen = Parse(
            "Select model",
            "  1. Opus 4.8       Best",
            "  2. Sonnet 5.5     Efficient",
            "  1. Something from a hook notice");

        Assert.False(screen.IsSound);
        Assert.Contains(screen.StructuralProblems!, p => p.Contains("ordinal 1"));
    }

    [Fact]
    public void OrdinalsThatDoNotRunFromOne_MakeTheScreenUnsound()
    {
        var screen = Parse("Select model", "  2. Opus 4.8   Best", "  3. Sonnet 5.5   Efficient");

        Assert.False(screen.IsSound);
    }

    [Fact]
    public void AGapInTheOrdinals_MakesTheScreenUnsound()
    {
        var screen = Parse("Select model", "  1. Opus 4.8   Best", "  3. Sonnet 5.5   Efficient");

        Assert.False(screen.IsSound);
    }

    [Fact]
    public void TwoRowsUnderTheCursor_MakeTheScreenUnsound()
    {
        var screen = Parse(
            "Select model",
            "❯ 1. Opus 4.8       Best",
            "❯ 2. Sonnet 5.5     Efficient");

        Assert.False(screen.IsSound);
        Assert.Contains(screen.StructuralProblems!, p => p.Contains("2 rows"));
    }

    [Fact]
    public void ElevenRows_AreSound_AndKeepTheirOrder()
    {
        var lines = new[] { "Select model" }
            .Concat(Enumerable.Range(1, 11).Select(n => $"  {n}. Model {n}    desc"))
            .ToArray();

        var screen = Parse(lines);

        Assert.True(screen.IsSound);
        Assert.Equal(Enumerable.Range(1, 11), screen.Rows.Select(r => r.Number));
    }
}

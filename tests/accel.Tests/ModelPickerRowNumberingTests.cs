namespace Accel.Tests;

using System.Linq;
using Accel.Orchestration;
using Xunit;

public class ModelPickerRowNumberingTests
{
    private static ModelPickerScreen Parse(params string[] lines) => ModelPickerScreenParser.Parse(lines);

    [Fact]
    public void RowsTenAndAbove_KeepTheirOwnOrdinal_AndAreNotDroppedAsDuplicates()
    {
        var screen = Parse(
            "Select model",
            "  8. Opus 4.8       Best for everyday tasks",
            "  9. Sonnet 5.5     Most efficient",
            "  10. Fable 5.1     For your toughest challenges",
            "  11. Haiku 4.5     Fastest");

        Assert.Equal(new[] { 8, 9, 10, 11 }, screen.Rows.Select(r => r.Number));
        Assert.Equal("Fable 5.1", screen.Rows.Single(r => r.Number == 10).Label);
    }

    [Fact]
    public void ACursorGlyph_IsStillRecognised_OnSingleAndDoubleDigitRows()
    {
        var screen = Parse(
            "Select model",
            "  1. Opus 4.8       Best",
            "❯ 10. Fable 5.1     Toughest");

        Assert.False(screen.Rows.Single(r => r.Number == 1).Selected);
        Assert.True(screen.Rows.Single(r => r.Number == 10).Selected);
    }

    [Fact]
    public void ADigitIsNeverTakenForTheCursor()
    {
        var screen = Parse("Select model", "10. Fable 5.1     Toughest");

        var row = Assert.Single(screen.Rows);
        Assert.Equal(10, row.Number);
        Assert.False(row.Selected);
    }
}

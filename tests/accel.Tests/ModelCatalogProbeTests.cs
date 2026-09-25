namespace Accel.Tests;

using Accel.Orchestration;
using Xunit;

/// <summary>
/// Unit tests for the pure parts of <see cref="ModelCatalogProbe"/>. The PTY-driving part needs a
/// real `claude` and is covered by the <c>model-picker-probe</c> dev verb instead; what is testable
/// here is the translation from a picker label to a <c>--model</c> value, which is where a mistake is
/// both silent and consequential - a bad value is passed straight to the CLI on the next session
/// launch.
/// </summary>
public class ModelCatalogProbeTests
{
    [Theory]
    [InlineData("Sonnet", "Sonnet")]
    [InlineData("Haiku", "Haiku")]
    [InlineData("Opus (1M context)", "Opus")]
    [InlineData("Opus (1M context) ✦", "Opus")]
    public void ExtractCliValue_StripsVariantNotes(string label, string expected)
    {
        Assert.Equal(expected, ModelCatalogProbe.ExtractCliValue(label));
    }

    /// <summary>
    /// The regression this was rewritten for: the picker decorates some rows with a marker glyph, and
    /// trimming only the glyphs already seen produced <c>--model "Sonnet ✦"</c> - a value the CLI
    /// rejects - the first time an unlisted one appeared. Trimming by character class is what makes an
    /// unfamiliar glyph a non-event.
    /// </summary>
    [Theory]
    [InlineData("Sonnet ✦")]
    [InlineData("Sonnet ★")]
    [InlineData("Sonnet ⏎")]
    [InlineData("Sonnet *")]
    [InlineData("Sonnet ·")]
    [InlineData("  Sonnet  ")]
    public void ExtractCliValue_StripsAnyTrailingDecoration_NotJustKnownGlyphs(string label)
    {
        Assert.Equal("Sonnet", ModelCatalogProbe.ExtractCliValue(label));
    }

    [Fact]
    public void ExtractCliValue_KeepsTrailingDigits()
    {
        // A version-suffixed name must survive the trim, so the trim can only remove non-alphanumerics.
        Assert.Equal("Sonnet 4.5", ModelCatalogProbe.ExtractCliValue("Sonnet 4.5 ✦"));
    }

    /// <summary>
    /// Regression for a live-caught bug: on a machine running Claude Code 2.1.265, discovery scraped
    /// a Sonnet row as "Sonnet √ context" - a stray checkmark-like glyph followed by echoed "context"
    /// text, most likely repaint residue from <c>WalkRowsAsync</c>'s arrow-key navigation. Because that
    /// decoration sits in the *interior* of the label (the label ends in the letter "t"), trimming only
    /// leading/trailing non-alphanumerics left it untouched, and Accel launched sessions with
    /// <c>--model "Sonnet √ context"</c> - a value the CLI silently rejects, falling back to the
    /// account default instead of the model the user picked.
    /// </summary>
    [Theory]
    [InlineData("Sonnet √ context", "Sonnet")]
    [InlineData("Opus (1M context) √ context", "Opus")]
    public void ExtractCliValue_DropsInteriorEchoText_NotJustLeadingAndTrailing(string label, string expected)
    {
        Assert.Equal(expected, ModelCatalogProbe.ExtractCliValue(label));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("✦")]
    [InlineData("(only a note)")]
    public void ExtractCliValue_NothingUsable_IsEmpty(string label)
    {
        // An empty value is what IsNonModelRow uses to discard a row rather than launching with it.
        Assert.Equal(string.Empty, ModelCatalogProbe.ExtractCliValue(label));
    }

    /// <summary>
    /// Regression: <c>DisplayName</c> is read from the description's first "·"-separated segment
    /// ("Sonnet 5 · Best for everyday, complex tasks" -> "Sonnet 5"). If the picker ever renders a
    /// description without that separator, falling back to the raw description would show descriptive
    /// prose ("Best for everyday, complex tasks") in the dialog instead of a model name - it must fall
    /// back to the row's label instead.
    /// </summary>
    [Fact]
    public void ToEntry_DescriptionWithoutSeparator_FallsBackToLabel_NotRawDescription()
    {
        var row = new ModelPickerRow(1, "Opus", "Best for everyday, complex tasks", Selected: false);

        var entry = ModelCatalogProbe.ToEntry(row, tiers: []);

        Assert.Equal("Opus", entry.DisplayName);
    }

    [Fact]
    public void ToEntry_DescriptionWithSeparator_UsesFirstSegment()
    {
        var row = new ModelPickerRow(1, "Opus", "Opus 5 · Best for everyday, complex tasks", Selected: false);

        var entry = ModelCatalogProbe.ToEntry(row, tiers: []);

        Assert.Equal("Opus 5", entry.DisplayName);
    }
}

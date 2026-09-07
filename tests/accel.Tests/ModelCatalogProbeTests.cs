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
}

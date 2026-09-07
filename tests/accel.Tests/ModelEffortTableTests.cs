namespace Accel.Tests;

using System;
using System.Collections.Generic;
using Accel.Metrics;
using Xunit;

public class ModelEffortTableTests
{
    // ---------------------------------------------------------------------------------------------
    // The built-in rule, used when no catalog has been discovered.
    // ---------------------------------------------------------------------------------------------

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
    public void TiersFor_WithoutCatalog_IsTheFullLadderExceptForHaiku()
    {
        Assert.Equal(EffortBarLevel.Levels, ModelEffortTable.TiersFor("Opus"));
        Assert.Empty(ModelEffortTable.TiersFor("Haiku"));
    }

    // ---------------------------------------------------------------------------------------------
    // A discovered catalog overrides the rule - the whole point of discovery. A model whose real
    // ladder is shorter than Accel's built-in vocabulary must report the short one, not the long one.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SupportsEffort_WithCatalog_PrefersTheCatalogOverTheBuiltInRule()
    {
        // Deliberately inverted against the built-in rule on both rows, so a result matching the rule
        // cannot be mistaken for a result read from the catalog.
        var catalog = Catalog(
            new ModelCatalogEntry("Haiku", "Haiku 4.5", null, new[] { "low", "high" }),
            new ModelCatalogEntry("Opus", "Opus 5", null, Array.Empty<string>()));

        Assert.True(ModelEffortTable.SupportsEffort("Haiku", catalog));
        Assert.False(ModelEffortTable.SupportsEffort("Opus", catalog));
    }

    [Fact]
    public void TiersFor_WithCatalog_IsThatModelsOwnLadder()
    {
        var catalog = Catalog(
            new ModelCatalogEntry("Sonnet", "Sonnet 5", null, new[] { "low", "medium", "high" }),
            new ModelCatalogEntry("Opus", "Opus 5", null, new[] { "low", "medium", "high", "xhigh", "max", "ultracode" }));

        Assert.Equal(new[] { "low", "medium", "high" }, ModelEffortTable.TiersFor("Sonnet", catalog));
        Assert.Equal(6, ModelEffortTable.TiersFor("Opus", catalog).Count);
    }

    [Fact]
    public void TiersFor_ModelTheCatalogDoesNotKnow_FallsBackToTheBuiltInRule()
    {
        var catalog = Catalog(new ModelCatalogEntry("Sonnet", "Sonnet 5", null, new[] { "low" }));

        // Not in the catalog at all: offering the full ladder is the right bias, since a tier the CLI
        // rejects is clamped, whereas hiding a tier it would have accepted is unrecoverable in the UI.
        Assert.Equal(EffortBarLevel.Levels, ModelEffortTable.TiersFor("SomeNewModel", catalog));
        Assert.True(ModelEffortTable.SupportsEffort("SomeNewModel", catalog));
    }

    [Fact]
    public void SupportsEffort_ByFamily_IsCaseInsensitiveAgainstTheCatalog()
    {
        var catalog = Catalog(new ModelCatalogEntry("Opus", "Opus 5", null, Array.Empty<string>()));

        // settings.json, the picker and a stored selection disagree on casing, so the lookup must not.
        Assert.False(ModelEffortTable.SupportsEffort("opus", catalog));
        Assert.False(ModelEffortTable.SupportsEffort("OPUS", catalog));
    }

    // ---------------------------------------------------------------------------------------------
    // The badge overload (panel A/E), which is keyed on a resolved model id rather than a --model
    // value and therefore has no catalog to consult.
    // ---------------------------------------------------------------------------------------------

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

    private static ModelCatalog Catalog(params ModelCatalogEntry[] entries) =>
        new((IReadOnlyList<ModelCatalogEntry>)entries, ModelCatalogSource.Discovered);
}

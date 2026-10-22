namespace Accel.Tests;

using System;
using System.Linq;
using Accel.Metrics;
using Accel.Orchestration;
using Xunit;

public class ModelCatalogValidatorTests
{
    private static readonly string[] Ladder = { "low", "medium", "high", "xhigh", "max" };

    private static ModelCatalogEntry Entry(string cli, string name, string? description = null, string[]? tiers = null) =>
        new(cli, name, description, tiers ?? Ladder);

    private static ModelCatalog Catalog(params ModelCatalogEntry[] entries) =>
        new(entries, ModelCatalogSource.Discovered, "2.1.308", DateTimeOffset.UtcNow, null);

    private static ModelCatalog Healthy() => Catalog(
        Entry("claude-opus-4-8", "Opus 4.8", "Best for everyday, complex tasks"),
        Entry("claude-opus-5-5", "Opus 5.5", "For complex work and everyday tasks"),
        Entry("claude-sonnet-5-5", "Sonnet 5.5", "Most efficient for simpler tasks · Org default"),
        Entry("claude-fable-5-1", "Fable 5.1", "For your toughest challenges"),
        Entry("claude-haiku-4-5", "Haiku 4.5", "Fastest for quick answers", Array.Empty<string>()));

    [Fact]
    public void AHealthyCatalog_HasNoProblems()
    {
        Assert.Empty(ModelCatalogValidator.Validate(Healthy()));
    }

    [Fact]
    public void TheCatalogFoundOnARealMachine_IsRejected_WithAReasonForEachMangledField()
    {
        var corrupt = Catalog(
            Entry("claude-opus-4-8", "Opus 4.8"),
            Entry("claude-sonnet-5-5up", "Sonnet 5.5up"),
            Entry("claude-haiku-5-5g", "Haiku 5.5g"),
            Entry("claude-haiku-4-5", "Haiku 4.5", "~5% · ~49Fastest for quick answers", Array.Empty<string>()));

        var problems = ModelCatalogValidator.Validate(corrupt);

        Assert.Contains(problems, p => p.Contains("claude-sonnet-5-5up"));
        Assert.Contains(problems, p => p.Contains("claude-haiku-5-5g"));
        Assert.Contains(problems, p => p.Contains("Sonnet 5.5up"));
        Assert.Contains(problems, p => p.Contains("description"));
    }

    [Fact]
    public void AOneMillionContextVariant_IsADistinctValidEntry_NotADuplicate()
    {
        var catalog = Catalog(
            Entry("claude-opus-4-8", "Opus 4.8"),
            Entry("claude-opus-4-8[1m]", "Opus 4.8 (1M context)"));

        Assert.Empty(ModelCatalogValidator.Validate(catalog));
    }

    [Fact]
    public void TheSameLaunchValueTwice_IsRejected()
    {
        var catalog = Catalog(Entry("claude-opus-4-8", "Opus 4.8"), Entry("CLAUDE-OPUS-4-8", "Opus 4.8"));

        Assert.Contains(ModelCatalogValidator.Validate(catalog), p => p.Contains("appears 2 times"));
    }

    [Fact]
    public void ADisplayNameThatDisagreesWithItsLaunchValue_IsRejected()
    {
        var catalog = Catalog(Entry("claude-sonnet-5-5", "Haiku 4.5"));

        Assert.Contains(ModelCatalogValidator.Validate(catalog), p => p.Contains("does not match"));
    }

    [Theory]
    [InlineData("Sonnet 5.5up")]
    [InlineData("sonnet 5.5")]
    [InlineData("Sonnet 5.5 > Try \"fix lint errors\"")]
    [InlineData("")]
    public void ImplausibleDisplayNames_AreRejected(string name)
    {
        Assert.False(ModelCatalogValidator.IsPlausibleDisplayName(name));
    }

    [Theory]
    [InlineData("~5% · ~49Fastest for quick answers")]
    [InlineData("Best for everyday tasks > Try \"fix lint errors\"")]
    [InlineData("Fastest ▰▰▱▱ 47.9k")]
    [InlineData("")]
    public void ImplausibleDescriptions_AreRejected(string description)
    {
        Assert.False(ModelCatalogValidator.IsPlausibleDescription(description));
    }

    [Theory]
    [InlineData("Best for everyday, complex tasks")]
    [InlineData("Most efficient for simpler tasks · Org default")]
    [InlineData("Anthropic's most capable model (recommended)")]
    public void RealDescriptions_AreAccepted(string description)
    {
        Assert.True(ModelCatalogValidator.IsPlausibleDescription(description));
    }

    [Fact]
    public void EveryEffortCapableModelHavingOneTier_IsATruncatedWalk_AndIsRejected()
    {
        var truncated = Catalog(
            Entry("claude-opus-4-8", "Opus 4.8", tiers: new[] { "max" }),
            Entry("claude-sonnet-5-5", "Sonnet 5.5", tiers: new[] { "max" }),
            Entry("claude-haiku-4-5", "Haiku 4.5", tiers: Array.Empty<string>()));

        Assert.Contains(ModelCatalogValidator.Validate(truncated), p => p.Contains("single tier"));
    }

    [Fact]
    public void OneModelWithOneTier_IsFine()
    {
        var catalog = Catalog(
            Entry("claude-opus-4-8", "Opus 4.8"),
            Entry("claude-sonnet-5-5", "Sonnet 5.5", tiers: new[] { "high" }));

        Assert.Empty(ModelCatalogValidator.Validate(catalog));
    }

    [Theory]
    [InlineData("LOW")]
    [InlineData("hi gh")]
    [InlineData("xhigh2")]
    public void ImplausibleEffortTiers_AreRejected(string tier)
    {
        var catalog = Catalog(Entry("claude-opus-4-8", "Opus 4.8", tiers: new[] { "low", tier }));

        Assert.Contains(ModelCatalogValidator.Validate(catalog), p => p.Contains("effort tiers"));
    }

    [Fact]
    public void RepeatedTiers_AreRejected()
    {
        var catalog = Catalog(Entry("claude-opus-4-8", "Opus 4.8", tiers: new[] { "low", "high", "low" }));

        Assert.NotEmpty(ModelCatalogValidator.Validate(catalog));
    }

    [Fact]
    public void AnEmptyCatalog_IsRejected()
    {
        Assert.NotEmpty(ModelCatalogValidator.Validate(Catalog()));
    }

    [Fact]
    public void ADescriptionWithMeterText_IsDroppedWhenARowBecomesAnEntry_NotKeptAndNotFatal()
    {
        var entry = ModelCatalogProbe.ToEntry(
            new ModelPickerRow(6, "Haiku 4.5", "~5% · ~49Fastest for quick answers", false), Array.Empty<string>());

        Assert.Null(entry.Description);
        Assert.Equal("claude-haiku-4-5", entry.CliValue);
    }

    [Fact]
    public void AOneMillionContextRow_BecomesItsOwnLaunchValue()
    {
        var plain = ModelCatalogProbe.ToEntry(new ModelPickerRow(2, "Opus 4.8", "Best", false), Ladder);
        var variant = ModelCatalogProbe.ToEntry(new ModelPickerRow(3, "Opus 4.8 (1M context)", "Best", false), Ladder);

        Assert.Equal("claude-opus-4-8", plain.CliValue);
        Assert.Equal("claude-opus-4-8[1m]", variant.CliValue);
        Assert.Equal("Opus 4.8 (1M context)", variant.DisplayName);
        Assert.Empty(ModelCatalogValidator.Validate(Catalog(plain, variant)));
    }

    [Fact]
    public void EveryEntryOfTheBuiltInCatalog_PassesTheValidator()
    {
        // The fallback the app uses when a scrape is refused must itself be trustworthy.
        Assert.All(
            ModelCatalog.BuiltIn.Entries,
            entry => Assert.True(ModelLaunchValue.IsPlausible(entry.CliValue), entry.CliValue));
        Assert.Equal(ModelCatalog.BuiltIn.Entries.Count, ModelCatalog.BuiltIn.Entries.Select(e => e.CliValue).Distinct().Count());
    }
}

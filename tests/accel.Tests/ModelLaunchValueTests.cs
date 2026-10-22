namespace Accel.Tests;

using System;
using System.IO;
using Accel.Metrics;
using Accel.Orchestration;
using Accel.Settings;
using Xunit;

/// <summary>
/// Regression tests for a corrupted model catalog: the picker is scraped off a repainting terminal screen,
/// and "Sonnet 5.5" with a stray "up" stuck to the version shipped as <c>claude-sonnet-5-5up</c> and was
/// cached for two weeks. Residue is now REJECTED rather than repaired: a repaired "5.5" can be right, but a
/// repaired "5.54" cannot be told from a real version.
/// </summary>
public sealed class ModelLaunchValueTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "accel-model-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Temp cleanup only.
        }
    }

    [Theory]
    [InlineData("sonnet")]
    [InlineData("Opus")]
    [InlineData("claude-sonnet-5-5")]
    [InlineData("claude-opus-4-8")]
    [InlineData("claude-fable-5-1")]
    [InlineData("claude-3-5-sonnet-20241022")]
    [InlineData("claude-opus-4-8[1m]")]
    public void IsPlausible_AcceptsRealLaunchValues(string value)
    {
        Assert.True(ModelLaunchValue.IsPlausible(value));
    }

    [Theory]
    [InlineData("claude-sonnet-5-5up")]
    [InlineData("claude-haiku-5-5g")]
    [InlineData("claude-sonnet-")]
    [InlineData("claude sonnet 5")]
    [InlineData("claude-opus-4-8; calc")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsPlausible_RejectsMangledOrEmptyValues(string? value)
    {
        Assert.False(ModelLaunchValue.IsPlausible(value));
    }

    [Theory]
    [InlineData("Sonnet 5.5", "Sonnet 5.5")]
    [InlineData("Fable 5.1", "Fable 5.1")]
    [InlineData("Sonnet 4.5 ✦", "Sonnet 4.5")]
    [InlineData("Opus (1M context)", "Opus")]
    public void ExtractCliValue_KeepsACleanVersion(string label, string expected)
    {
        Assert.Equal(expected, ModelCatalogProbe.ExtractCliValue(label));
    }

    [Theory]
    [InlineData("Sonnet 5.5up")]
    [InlineData("Haiku 5.5g")]
    [InlineData("Opus 4.8x2")]
    [InlineData("Sonnet 5.5up 9")]
    [InlineData("Sonnet x2")]
    [InlineData("Sonnet 5.5~49")]
    public void ExtractCliValue_RefusesAVersionWithResidueStuckToIt_InsteadOfRepairingIt(string label)
    {
        Assert.Equal(string.Empty, ModelCatalogProbe.ExtractCliValue(label));
    }

    [Fact]
    public void Cache_IsAbsent_WhenAnEntryHoldsAMangledValue_AndASaveIsRefused()
    {
        string path = Path.Combine(_directory, "accel-model-catalog.json");
        var corrupt = new ModelCatalog(
            new[]
            {
                new ModelCatalogEntry("claude-opus-5-5", "Opus 5.5", null, new[] { "low", "high" }),
                new ModelCatalogEntry("claude-sonnet-5-5up", "Sonnet 5.5up", null, new[] { "low", "high" }),
            },
            ModelCatalogSource.Discovered,
            "2.1.308",
            DateTimeOffset.UtcNow,
            null);

        Assert.False(ModelCatalogCache.TrySave(corrupt, "identity", path));
        Assert.Null(ModelCatalogCache.TryLoad(path));
    }

    [Fact]
    public void Cache_IgnoresAFileWrittenByAnOlderVersionHoldingTheExactCorruptValues()
    {
        // The shape of the file found on a real machine: current-looking, schema 3, residue in three places.
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "accel-model-catalog.json");
        File.WriteAllText(path, $$"""
            {
              "SchemaVersion": 3,
              "CliBinaryIdentity": "id",
              "ClaudeCliVersion": "2.1.308",
              "DiscoveredAtUtc": "{{DateTimeOffset.UtcNow:O}}",
              "Models": [
                { "CliValue": "claude-sonnet-5-5up", "DisplayName": "Sonnet 5.5up", "Description": "Most efficient", "EffortTiers": ["low","high"] },
                { "CliValue": "claude-haiku-4-5", "DisplayName": "Haiku 4.5", "Description": "~5% · ~49Fastest for quick answers", "EffortTiers": [] }
              ]
            }
            """);

        Assert.Null(ModelCatalogCache.TryLoad(path, "id"));
    }

    [Fact]
    public void Cache_RejectsAFileWithNoTimestamp_InsteadOfNeverExpiringIt()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "accel-model-catalog.json");
        File.WriteAllText(path, """
            {
              "SchemaVersion": 4,
              "CliBinaryIdentity": "id",
              "Models": [ { "CliValue": "claude-opus-5-5", "DisplayName": "Opus 5.5", "EffortTiers": ["low","high"] } ]
            }
            """);

        Assert.Null(ModelCatalogCache.TryLoad(path, "id"));
    }

    [Fact]
    public void Cache_StillLoadsAHealthyCatalog()
    {
        string path = Path.Combine(_directory, "accel-model-catalog.json");
        var healthy = new ModelCatalog(
            new[]
            {
                new ModelCatalogEntry("claude-opus-5-5", "Opus 5.5", "For complex work", new[] { "low", "high" }),
                new ModelCatalogEntry("claude-sonnet-5-5", "Sonnet 5.5", null, new[] { "low", "high" }),
            },
            ModelCatalogSource.Discovered,
            "2.1.308",
            DateTimeOffset.UtcNow,
            null);
        Assert.True(ModelCatalogCache.TrySave(healthy, "identity", path));

        var loaded = ModelCatalogCache.TryLoad(path);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Entries.Count);
    }
}

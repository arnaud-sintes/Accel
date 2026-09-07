namespace Accel.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Accel.Metrics;
using Accel.Settings;
using Xunit;

/// <summary>
/// Unit tests for <see cref="ModelCatalog"/> and <see cref="ModelCatalogCache"/> - the pure and the
/// on-disk halves of model/effort discovery. Both are fully testable without a `claude` process;
/// only the probe that produces a catalog needs one (see <c>model-picker-probe</c>).
/// </summary>
public class ModelCatalogTests
{
    // ---------------------------------------------------------------------------------------------
    // The built-in fallback, which must stay consistent with the tables it is derived from rather
    // than being a third independently maintained list.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void BuiltIn_MatchesModelBadgeTablesFamilies()
    {
        Assert.Equal(
            ModelBadgeTable.Families,
            ModelCatalog.BuiltIn.Entries.Select(entry => entry.CliValue).ToArray());
    }

    [Fact]
    public void BuiltIn_GivesEveryEffortCapableModelTheFullLadder_AndHaikuNone()
    {
        Assert.Empty(ModelCatalog.BuiltIn.Find("Haiku")!.EffortTiers);
        Assert.Equal(EffortBarLevel.Levels, ModelCatalog.BuiltIn.Find("Sonnet")!.EffortTiers);
    }

    [Fact]
    public void BuiltIn_IsNotReportedAsDiscovered()
    {
        Assert.False(ModelCatalog.BuiltIn.IsDiscovered);
        Assert.Contains("built-in", ModelCatalog.BuiltIn.DescribeSource(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------------
    // Lookup. The same --model value arrives from the CLI's picker ("Sonnet"), a user's settings.json
    // ("opus") and a stored selection, so casing must never decide whether a model is found.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Sonnet")]
    [InlineData("sonnet")]
    [InlineData("SONNET")]
    [InlineData("  Sonnet  ")]
    public void Find_IsCaseAndWhitespaceInsensitive(string query)
    {
        Assert.NotNull(ModelCatalog.BuiltIn.Find(query));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NoSuchModel")]
    public void Find_UnknownOrBlank_IsNull(string? query)
    {
        Assert.Null(ModelCatalog.BuiltIn.Find(query));
    }

    [Fact]
    public void EffortTiersFor_ModelOutsideTheCatalog_FallsBackToTheFullLadder()
    {
        var catalog = Catalog(new ModelCatalogEntry("Sonnet", "Sonnet 5", null, new[] { "low" }));

        Assert.Equal(EffortBarLevel.Levels, catalog.EffortTiersFor("SomethingNew"));
    }

    // ---------------------------------------------------------------------------------------------
    // Default resolution.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ResolveDefaultModel_UsesTheHintMatchedOnDisplayName()
    {
        // The picker's "Default (recommended)" row describes itself by display name ("Sonnet 5"), not
        // by the --model value, so matching only on CliValue would silently miss it.
        var catalog = new ModelCatalog(
            new[]
            {
                new ModelCatalogEntry("Haiku", "Haiku 4.5", null, Array.Empty<string>()),
                new ModelCatalogEntry("Opus", "Opus 5", null, new[] { "low" }),
            },
            ModelCatalogSource.Discovered,
            DefaultModelHint: "Opus 5");

        Assert.Equal("Opus", catalog.ResolveDefaultModel());
    }

    [Fact]
    public void ResolveDefaultModel_UsesTheHintMatchedOnCliValue()
    {
        var catalog = new ModelCatalog(
            new[] { new ModelCatalogEntry("Opus", "Opus 5", null, new[] { "low" }) },
            ModelCatalogSource.Discovered,
            DefaultModelHint: "Opus");

        Assert.Equal("Opus", catalog.ResolveDefaultModel());
    }

    [Fact]
    public void ResolveDefaultModel_WithNoHint_DoesNotPickTheLeastCapableFirstEntry()
    {
        Assert.Equal("Sonnet", ModelCatalog.BuiltIn.ResolveDefaultModel());
        Assert.Equal("Haiku", ModelCatalog.BuiltIn.Entries[0].CliValue); // the entry it must NOT pick
    }

    [Fact]
    public void ResolveDefaultModel_WithNeitherHintNorFallbackPresent_FallsBackToTheFirstEntry()
    {
        var catalog = Catalog(
            new ModelCatalogEntry("Nimbus", "Nimbus 9", null, new[] { "low" }),
            new ModelCatalogEntry("Cirrus", "Cirrus 2", null, new[] { "low" }));

        Assert.Equal("Nimbus", catalog.ResolveDefaultModel());
    }

    [Fact]
    public void ResolveDefaultModel_EmptyCatalog_IsNull()
    {
        Assert.Null(Catalog().ResolveDefaultModel());
    }

    // ---------------------------------------------------------------------------------------------
    // Entry semantics: an empty ladder means "no effort control", which is deliberately a different
    // state from "tiers unknown".
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Entry_EmptyLadder_ReportsNoEffortSupportAndNoDefaultTier()
    {
        var entry = new ModelCatalogEntry("Haiku", "Haiku 4.5", null, Array.Empty<string>());

        Assert.False(entry.SupportsEffort);
        Assert.Null(entry.DefaultEffortTier);
    }

    [Fact]
    public void Entry_DefaultEffortTier_IsTheMiddleOfItsOwnLadder()
    {
        // Not index 0: a six-rung ladder must not open pinned to "low" just because it sorts first.
        var entry = new ModelCatalogEntry(
            "Opus", "Opus 5", null, new[] { "low", "medium", "high", "xhigh", "max", "ultracode" });

        Assert.Equal("xhigh", entry.DefaultEffortTier);
        Assert.Contains(entry.DefaultEffortTier, entry.EffortTiers);
    }

    // ---------------------------------------------------------------------------------------------
    // The cache. Every rejection path matters: a stale catalog silently offering models the installed
    // CLI no longer has is exactly the failure the cache key exists to prevent.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Cache_RoundTripsACatalog()
    {
        using var file = new TempFile();
        var catalog = new ModelCatalog(
            new[]
            {
                new ModelCatalogEntry("Opus", "Opus 5", "Opus 5 · best", new[] { "low", "max" }),
                new ModelCatalogEntry("Haiku", "Haiku 4.5", null, Array.Empty<string>()),
            },
            ModelCatalogSource.Discovered,
            "2.1.263",
            DateTimeOffset.UtcNow,
            "Opus 5");

        Assert.True(ModelCatalogCache.TrySave(catalog, "identity-a", file.Path));

        var loaded = ModelCatalogCache.TryLoad(file.Path, "identity-a");

        Assert.NotNull(loaded);
        Assert.True(loaded!.IsDiscovered);
        Assert.Equal("2.1.263", loaded.ClaudeCliVersion);
        Assert.Equal("Opus 5", loaded.DefaultModelHint);
        Assert.Equal(new[] { "low", "max" }, loaded.Find("Opus")!.EffortTiers);
        Assert.Empty(loaded.Find("Haiku")!.EffortTiers);
    }

    [Fact]
    public void Cache_DifferentCliBinaryIdentity_IsRejected()
    {
        using var file = new TempFile();
        ModelCatalogCache.TrySave(Discovered(), "identity-a", file.Path);

        Assert.Null(ModelCatalogCache.TryLoad(file.Path, "identity-b"));
    }

    [Fact]
    public void Cache_OlderThanMaxAge_IsRejected()
    {
        using var file = new TempFile();
        var stale = Discovered() with { DiscoveredAtUtc = DateTimeOffset.UtcNow - ModelCatalogCache.MaxAge * 2 };
        ModelCatalogCache.TrySave(stale, "identity-a", file.Path);

        Assert.Null(ModelCatalogCache.TryLoad(file.Path, "identity-a"));
        Assert.NotNull(ModelCatalogCache.TryLoad(
            file.Path, "identity-a", now: stale.DiscoveredAtUtc!.Value.AddDays(1)));
    }

    [Fact]
    public void Cache_MissingFile_IsNullNotAnError()
    {
        Assert.Null(ModelCatalogCache.TryLoad(
            Path.Combine(Path.GetTempPath(), $"accel-no-such-catalog-{Guid.NewGuid():N}.json")));
    }

    [Fact]
    public void Cache_CorruptFile_IsNullNotAnError()
    {
        using var file = new TempFile();
        File.WriteAllText(file.Path, "{ this is not json");

        Assert.Null(ModelCatalogCache.TryLoad(file.Path, "identity-a"));
    }

    [Fact]
    public void Cache_FileWithNoUsableModels_IsRejected()
    {
        using var file = new TempFile();
        File.WriteAllText(file.Path, """
            { "SchemaVersion": 1, "CliBinaryIdentity": "identity-a", "Models": [] }
            """);

        Assert.Null(ModelCatalogCache.TryLoad(file.Path, "identity-a"));
    }

    [Fact]
    public void Cache_UnknownSchemaVersion_IsRejected()
    {
        using var file = new TempFile();
        File.WriteAllText(file.Path, """
            { "SchemaVersion": 9999, "CliBinaryIdentity": "identity-a",
              "Models": [ { "CliValue": "Opus", "DisplayName": "Opus 5", "EffortTiers": ["low"] } ] }
            """);

        Assert.Null(ModelCatalogCache.TryLoad(file.Path, "identity-a"));
    }

    [Fact]
    public void DescribeCliBinary_MissingOrBlankPath_IsNull()
    {
        Assert.Null(ModelCatalogCache.DescribeCliBinary(null));
        Assert.Null(ModelCatalogCache.DescribeCliBinary("   "));
        Assert.Null(ModelCatalogCache.DescribeCliBinary(
            Path.Combine(Path.GetTempPath(), $"accel-no-such-exe-{Guid.NewGuid():N}.exe")));
    }

    [Fact]
    public void DescribeCliBinary_ChangesWhenTheFileChanges()
    {
        using var file = new TempFile();
        File.WriteAllText(file.Path, "one");
        var first = ModelCatalogCache.DescribeCliBinary(file.Path);

        File.WriteAllText(file.Path, "a longer body, so the size differs");
        var second = ModelCatalogCache.DescribeCliBinary(file.Path);

        Assert.NotNull(first);
        Assert.NotEqual(first, second);
    }

    // ---------------------------------------------------------------------------------------------
    // The user's settings.json defaults.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void UserModelDefaults_ReadsModelAndEffortLevel()
    {
        using var file = new TempFile();
        File.WriteAllText(file.Path, """
            { "model": "Opus", "effortLevel": "xhigh", "theme": "dark" }
            """);

        var defaults = UserModelDefaults.Read(file.Path);

        Assert.Equal("Opus", defaults.Model);
        Assert.Equal("xhigh", defaults.EffortLevel);
    }

    [Fact]
    public void UserModelDefaults_MissingKeysOrFile_AreNullNotAnError()
    {
        using var file = new TempFile();
        File.WriteAllText(file.Path, "{ \"theme\": \"dark\" }");

        Assert.Equal(UserModelDefaults.None, UserModelDefaults.Read(file.Path));
        Assert.Equal(
            UserModelDefaults.None,
            UserModelDefaults.Read(Path.Combine(Path.GetTempPath(), $"accel-no-settings-{Guid.NewGuid():N}.json")));
    }

    [Fact]
    public void UserModelDefaults_NonStringValues_AreTreatedAsAbsent()
    {
        using var file = new TempFile();
        File.WriteAllText(file.Path, """
            { "model": 42, "effortLevel": { "nested": true } }
            """);

        Assert.Equal(UserModelDefaults.None, UserModelDefaults.Read(file.Path));
    }

    private static ModelCatalog Catalog(params ModelCatalogEntry[] entries) =>
        new((IReadOnlyList<ModelCatalogEntry>)entries, ModelCatalogSource.Discovered);

    private static ModelCatalog Discovered() =>
        new(
            new[] { new ModelCatalogEntry("Opus", "Opus 5", null, new[] { "low", "max" }) },
            ModelCatalogSource.Discovered,
            "2.1.263",
            DateTimeOffset.UtcNow);

    private sealed class TempFile : IDisposable
    {
        public TempFile() =>
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"accel-model-catalog-test-{Guid.NewGuid():N}.json");

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}

namespace Accel.Tests;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Accel.App.Services;
using Accel.Metrics;
using Accel.Orchestration;
using Accel.Settings;
using Xunit;

/// <summary>The service is the last gate before a catalog reaches the cache or the UI: whatever produced it
/// (the real probe, or any other source), a catalog that fails validation must go no further.</summary>
public sealed class ModelCatalogServiceGateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "accel-service-gate-" + Guid.NewGuid().ToString("N"));
    private readonly string _claudeExe;

    public ModelCatalogServiceGateTests()
    {
        Directory.CreateDirectory(_directory);
        _claudeExe = Path.Combine(_directory, "claude.exe");
        File.WriteAllText(_claudeExe, "stand-in binary");
    }

    private string CachePath => Path.Combine(_directory, "accel-model-catalog.json");

    private string RejectedPath => Path.Combine(_directory, "accel-model-catalog.rejected.txt");

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

    private ModelCatalogService Service(Func<string, CancellationToken, Task<ModelCatalogProbeResult>> discover) =>
        new(discover, () => _claudeExe, CachePath, Path.Combine(_directory, "settings.json"));

    private static ModelCatalog Catalog(params ModelCatalogEntry[] entries) =>
        new(entries, ModelCatalogSource.Discovered, "2.1.308", DateTimeOffset.UtcNow, null);

    private static ModelCatalogEntry Good(string cli, string name) => new(cli, name, null, new[] { "low", "high" });

    private static Task<ModelCatalogProbeResult> Succeeded(ModelCatalog catalog) =>
        Task.FromResult(new ModelCatalogProbeResult(ModelCatalogProbeOutcome.Discovered, catalog, null, TimeSpan.Zero));

    [Fact]
    public async Task ACatalogThatFailsValidation_IsNotPublished_NotCached_AndIsReported()
    {
        using var service = Service((_, _) => Succeeded(Catalog(
            Good("claude-opus-4-8", "Opus 4.8"),
            Good("claude-sonnet-5-5up", "Sonnet 5.5up"))));
        bool changed = false;
        service.CatalogChanged += (_, _) => changed = true;

        var result = await service.RefreshAsync(_directory);

        Assert.False(result.Succeeded);
        Assert.Equal(ModelCatalogProbeOutcome.NotParseable, result.Outcome);
        Assert.False(changed);
        Assert.False(service.Current.IsDiscovered);
        Assert.False(File.Exists(CachePath));
        Assert.Contains("failed validation", service.LastRefreshDetail);
        Assert.Contains("claude-sonnet-5-5up", File.ReadAllText(RejectedPath));
    }

    [Fact]
    public async Task AGoodCatalog_IsPublishedAndCached_AndClearsAnEarlierRejectionReport()
    {
        File.WriteAllText(RejectedPath, "stale report");
        using var service = Service((_, _) => Succeeded(Catalog(
            Good("claude-opus-4-8", "Opus 4.8"),
            Good("claude-sonnet-5-5", "Sonnet 5.5"))));

        var result = await service.RefreshAsync(_directory);

        Assert.True(result.Succeeded);
        Assert.True(service.Current.IsDiscovered);
        Assert.True(File.Exists(CachePath));
        Assert.False(File.Exists(RejectedPath));
    }

    [Fact]
    public async Task ARejectedProbe_WritesItsReport_AndKeepsTheCurrentCatalog()
    {
        using var service = Service((_, _) => Task.FromResult(new ModelCatalogProbeResult(
            ModelCatalogProbeOutcome.NotParseable, null, "the two reads disagree", TimeSpan.Zero, "rejected: the two reads disagree")));

        var result = await service.RefreshAsync(_directory);

        Assert.False(result.Succeeded);
        Assert.False(service.Current.IsDiscovered);
        Assert.Contains("the two reads disagree", File.ReadAllText(RejectedPath));
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public async Task IfClaudeChangesWhileTheProbeRuns_TheResultIsUsedButNotCached()
    {
        using var service = Service((_, _) =>
        {
            // Claude Code auto-updates mid-probe: the binary on disk is no longer the one that was read.
            File.AppendAllText(_claudeExe, " ...updated");
            return Succeeded(Catalog(Good("claude-opus-4-8", "Opus 4.8"), Good("claude-sonnet-5-5", "Sonnet 5.5")));
        });

        var result = await service.RefreshAsync(_directory);

        Assert.True(result.Succeeded);
        Assert.True(service.Current.IsDiscovered);
        Assert.False(File.Exists(CachePath));
        Assert.Contains("changed", service.LastRefreshDetail);
    }
}

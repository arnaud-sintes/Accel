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

/// <summary>
/// A refused read of the model list must not turn into a ~45s blocking scan at every launch - but it must not
/// hide a fix either (an updated `claude.exe`, an expired cooldown), and an explicit retry always runs.
/// </summary>
public sealed class ModelCatalogFailureMemoTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "accel-failure-memo-" + Guid.NewGuid().ToString("N"));
    private readonly string _claudeExe;

    public ModelCatalogFailureMemoTests()
    {
        Directory.CreateDirectory(_directory);
        _claudeExe = Path.Combine(_directory, "claude.exe");
        File.WriteAllText(_claudeExe, "stand-in binary");
    }

    private string CachePath => Path.Combine(_directory, "accel-model-catalog.json");

    private string MemoPath => Path.Combine(_directory, "accel-model-catalog.failed.json");

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

    private static Task<ModelCatalogProbeResult> Outcome(ModelCatalogProbeOutcome outcome, string detail = "detail") =>
        Task.FromResult(new ModelCatalogProbeResult(outcome, null, detail, TimeSpan.Zero));

    private static Task<ModelCatalogProbeResult> Good() =>
        Task.FromResult(new ModelCatalogProbeResult(
            ModelCatalogProbeOutcome.Discovered,
            new ModelCatalog(
                new[]
                {
                    new ModelCatalogEntry("claude-opus-4-8", "Opus 4.8", null, new[] { "low", "high" }),
                    new ModelCatalogEntry("claude-sonnet-5-5", "Sonnet 5.5", null, new[] { "low", "high" }),
                },
                ModelCatalogSource.Discovered,
                "2.1.308",
                DateTimeOffset.UtcNow,
                null),
            null,
            TimeSpan.Zero));

    private string Identity => ModelCatalogCache.DescribeCliBinary(_claudeExe)!;

    // --- the memo ----------------------------------------------------------------------------------------------

    [Fact]
    public void ARecordedFailure_IsActiveForTheSameBinary_UntilTheCooldownEnds()
    {
        var failedAt = new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);
        ModelCatalogFailureMemo.Record(CachePath, "binary-1", "the two reads disagree", failedAt);

        var active = ModelCatalogFailureMemo.TryGetActive(CachePath, "binary-1", failedAt.AddHours(23));

        Assert.NotNull(active);
        Assert.Equal("the two reads disagree", active!.Reason);
        Assert.Equal(failedAt + ModelCatalogFailureMemo.Cooldown, active.RetryAtUtc);
        Assert.Null(ModelCatalogFailureMemo.TryGetActive(CachePath, "binary-1", failedAt.AddHours(25)));
    }

    [Fact]
    public void AFailureOfAnotherBinary_DoesNotApply()
    {
        ModelCatalogFailureMemo.Record(CachePath, "binary-1", "x");

        Assert.Null(ModelCatalogFailureMemo.TryGetActive(CachePath, "binary-2"));
    }

    [Fact]
    public void WithoutAnIdentity_NothingIsRemembered()
    {
        ModelCatalogFailureMemo.Record(CachePath, null, "x");

        Assert.False(File.Exists(MemoPath));
        Assert.Null(ModelCatalogFailureMemo.TryGetActive(CachePath, null));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"CliBinaryIdentity\":\"binary-1\"}")]
    public void AnUnreadableMemo_ReadsAsNoFailure(string content)
    {
        File.WriteAllText(MemoPath, content);

        Assert.Null(ModelCatalogFailureMemo.TryGetActive(CachePath, "binary-1"));
    }

    [Fact]
    public void Clear_ForgetsTheFailure()
    {
        ModelCatalogFailureMemo.Record(CachePath, "binary-1", "x");
        ModelCatalogFailureMemo.Clear(CachePath);

        Assert.Null(ModelCatalogFailureMemo.TryGetActive(CachePath, "binary-1"));
        Assert.False(File.Exists(MemoPath));
    }

    // --- the service -------------------------------------------------------------------------------------------

    [Fact]
    public async Task AfterARefusedRead_StartupNoLongerRescans_AndSaysWhy()
    {
        using var service = Service((_, _) => Outcome(ModelCatalogProbeOutcome.NotParseable, "the two reads disagree"));
        Assert.True(service.NeedsRefresh());

        await service.RefreshAsync(_directory);

        Assert.False(service.NeedsRefresh());
        Assert.Contains("try again", service.FallbackNote);
        Assert.Contains("could not be read", service.FallbackNote);
    }

    [Fact]
    public async Task AnUpdatedClaudeBinary_EndsTheCooldownAtOnce()
    {
        using var service = Service((_, _) => Outcome(ModelCatalogProbeOutcome.NotParseable));
        await service.RefreshAsync(_directory);
        Assert.False(service.NeedsRefresh());

        File.AppendAllText(_claudeExe, " ...updated");

        Assert.True(service.NeedsRefresh());
        Assert.Null(service.FallbackNote);
    }

    [Theory]
    [InlineData(ModelCatalogProbeOutcome.Blocked)]
    [InlineData(ModelCatalogProbeOutcome.CliUnavailable)]
    [InlineData(ModelCatalogProbeOutcome.Cancelled)]
    public async Task OnlyARefusedRead_IsRemembered_NotAGateAMissingCliOrACancel(ModelCatalogProbeOutcome outcome)
    {
        using var service = Service((_, _) => Outcome(outcome));

        await service.RefreshAsync(_directory);

        Assert.True(service.NeedsRefresh());
        Assert.False(File.Exists(MemoPath));
    }

    [Fact]
    public async Task AProbeThatThrows_IsRemembered_LikeAnyOtherRefusedRead()
    {
        using var service = Service((_, _) => throw new InvalidOperationException("boom"));

        await service.RefreshAsync(_directory);

        Assert.False(service.NeedsRefresh());
        Assert.True(File.Exists(MemoPath));
    }

    [Fact]
    public async Task AnExplicitRefresh_AlwaysRuns_EvenDuringTheCooldown()
    {
        var calls = 0;
        using var service = Service((_, _) =>
        {
            calls++;
            return Outcome(ModelCatalogProbeOutcome.NotParseable);
        });
        await service.RefreshAsync(_directory);
        Assert.False(service.NeedsRefresh());

        await service.RefreshAsync(_directory);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ASuccessfulRead_ClearsTheRememberedFailure()
    {
        ModelCatalogFailureMemo.Record(CachePath, Identity, "earlier failure");
        using var service = Service((_, _) => Good());

        var result = await service.RefreshAsync(_directory);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(MemoPath));
        Assert.Null(service.FallbackNote);
    }

    [Fact]
    public void ABuiltInListWithAFailureNote_SaysWhy_NotThatClaudeWasNeverAsked()
    {
        var text = ModelCatalog.BuiltIn.DescribeSource("Claude Code's model list could not be read yesterday.");

        Assert.Contains("could not be read", text);
        Assert.DoesNotContain("not been queried yet", text);
        Assert.Contains("not been queried yet", ModelCatalog.BuiltIn.DescribeSource());
    }
}

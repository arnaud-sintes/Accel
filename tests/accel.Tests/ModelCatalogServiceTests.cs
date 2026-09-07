namespace Accel.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Accel.App.Services;
using Accel.Metrics;
using Accel.Orchestration;
using Accel.Settings;
using Xunit;

/// <summary>
/// Unit tests for <see cref="ModelCatalogService"/> - the startup-facing owner of the model/effort
/// catalog. Every test drives the constructor's seams (<c>discover</c>, <c>cliPathResolver</c>,
/// <c>cachePath</c>, <c>settingsPath</c>) so nothing here launches a process, touches the user's real
/// <c>~/.claude</c>, or depends on whether `claude` is installed.
///
/// <para>The properties worth pinning are all about <i>not</i> doing things: the constructor must not
/// spawn or block, a fresh cache must suppress discovery entirely, and no failure may replace a
/// usable catalog with nothing. A regression in any of those turns a background nicety into a
/// startup cost or an empty model picker.</para>
/// </summary>
public class ModelCatalogServiceTests
{
    // ---------------------------------------------------------------------------------------------
    // Construction: synchronous, no process work, always leaves a usable catalog.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Constructor_NoCache_FallsBackToTheBuiltInCatalog()
    {
        using var fixture = new Fixture();
        using var service = fixture.CreateService();

        Assert.False(service.Current.IsDiscovered);
        Assert.Equal(ModelCatalog.BuiltIn.Entries.Count, service.Current.Entries.Count);
    }

    [Fact]
    public void Constructor_DoesNotRunDiscovery()
    {
        using var fixture = new Fixture();
        using var service = fixture.CreateService();

        // Startup must never pay for discovery: it drives a real TUI for ~15s.
        Assert.Equal(0, fixture.DiscoverCalls);
    }

    [Fact]
    public void Constructor_CacheMatchingTheCliBinary_IsAdoptedImmediately()
    {
        using var fixture = new Fixture();
        fixture.WriteCacheForCurrentBinary(Discovered("Opus", "low", "max"));

        using var service = fixture.CreateService();

        Assert.True(service.Current.IsDiscovered);
        Assert.Equal(new[] { "low", "max" }, service.Current.Find("Opus")!.EffortTiers);
        Assert.Equal(0, fixture.DiscoverCalls);
    }

    [Fact]
    public void Constructor_CacheFromADifferentCliBinary_IsIgnored()
    {
        using var fixture = new Fixture();
        ModelCatalogCache.TrySave(Discovered("Opus", "low"), "some-other-binary", fixture.CachePath);

        using var service = fixture.CreateService();

        Assert.False(service.Current.IsDiscovered);
    }

    [Fact]
    public void Constructor_ReadsTheUsersModelAndEffortDefaults()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.SettingsPath, """
            { "model": "Opus", "effortLevel": "xhigh" }
            """);

        using var service = fixture.CreateService();

        Assert.Equal("Opus", service.UserDefaults.Model);
        Assert.Equal("xhigh", service.UserDefaults.EffortLevel);
    }

    [Fact]
    public void Constructor_MissingSettingsFile_YieldsNoDefaults_NotAThrow()
    {
        using var fixture = new Fixture();

        using var service = fixture.CreateService();

        Assert.Equal(UserModelDefaults.None, service.UserDefaults);
    }

    [Fact]
    public void Constructor_UnresolvableCli_StillLeavesAUsableCatalog()
    {
        using var fixture = new Fixture();

        // `claude` not on PATH: the app must still open with a model picker that works.
        using var service = fixture.CreateService(cliPath: null);

        Assert.False(service.Current.IsDiscovered);
        Assert.NotEmpty(service.Current.Entries);
    }

    // ---------------------------------------------------------------------------------------------
    // Staleness.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void NeedsRefresh_TrueWithNoCache_FalseWithAMatchingOne()
    {
        using var fixture = new Fixture();
        using (var cold = fixture.CreateService())
        {
            Assert.True(cold.NeedsRefresh());
        }

        fixture.WriteCacheForCurrentBinary(Discovered("Opus", "low"));

        using var warm = fixture.CreateService();
        Assert.False(warm.NeedsRefresh());
    }

    [Fact]
    public void NeedsRefresh_TrueAgainAfterTheCliBinaryChanges()
    {
        using var fixture = new Fixture();
        fixture.WriteCacheForCurrentBinary(Discovered("Opus", "low"));

        using var service = fixture.CreateService();
        Assert.False(service.NeedsRefresh());

        // An in-place CLI upgrade rewrites claude.exe - which is exactly the cache key.
        fixture.TouchCliBinary();

        Assert.True(service.NeedsRefresh());
    }

    // ---------------------------------------------------------------------------------------------
    // RefreshAsync: publishing on success, and leaving everything alone on every failure.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_Success_ReplacesCurrent_RaisesTheEvent_AndWritesTheCache()
    {
        using var fixture = new Fixture();
        var discovered = Discovered("Nimbus", "low", "high");
        fixture.Result = new ModelCatalogProbeResult(
            ModelCatalogProbeOutcome.Discovered, discovered, null, TimeSpan.FromSeconds(9));

        using var service = fixture.CreateService();
        ModelCatalog? published = null;
        service.CatalogChanged += (_, catalog) => published = catalog;

        var result = await service.RefreshAsync(fixture.ProbeDirectory);

        Assert.True(result.Succeeded);
        Assert.Equal("Nimbus", service.Current.Find("Nimbus")!.CliValue);
        Assert.Same(discovered, published);
        Assert.Equal(ModelCatalogProbeOutcome.Discovered, service.LastRefreshOutcome);

        // Cached against the current binary, so the next start does no work at all.
        Assert.NotNull(ModelCatalogCache.TryLoad(fixture.CachePath, fixture.CliBinaryIdentity()));
    }

    [Theory]
    [InlineData(ModelCatalogProbeOutcome.Blocked)]
    [InlineData(ModelCatalogProbeOutcome.NotParseable)]
    [InlineData(ModelCatalogProbeOutcome.CliUnavailable)]
    [InlineData(ModelCatalogProbeOutcome.Cancelled)]
    public async Task RefreshAsync_Failure_LeavesTheExistingCatalogIntactAndWritesNoCache(
        ModelCatalogProbeOutcome outcome)
    {
        using var fixture = new Fixture();
        fixture.Result = new ModelCatalogProbeResult(outcome, null, "detail here", TimeSpan.Zero);

        using var service = fixture.CreateService();
        var before = service.Current;
        var eventRaised = false;
        service.CatalogChanged += (_, _) => eventRaised = true;

        var result = await service.RefreshAsync(fixture.ProbeDirectory);

        Assert.False(result.Succeeded);
        Assert.Same(before, service.Current);
        Assert.False(eventRaised);
        Assert.Equal(outcome, service.LastRefreshOutcome);
        Assert.Equal("detail here", service.LastRefreshDetail);
        Assert.False(File.Exists(fixture.CachePath));
    }

    [Fact]
    public async Task RefreshAsync_ProbeThrows_IsReportedNotPropagated()
    {
        using var fixture = new Fixture();
        fixture.Throw = new InvalidOperationException("probe blew up");

        using var service = fixture.CreateService();
        var before = service.Current;

        // A bug in the probe must not be able to take the app down, or empty the model picker.
        var result = await service.RefreshAsync(fixture.ProbeDirectory);

        Assert.Equal(ModelCatalogProbeOutcome.NotParseable, result.Outcome);
        Assert.Same(before, service.Current);
        Assert.Equal("probe blew up", service.LastRefreshDetail);
    }

    [Fact]
    public async Task RefreshAsync_Cancelled_IsReportedAsCancelled()
    {
        using var fixture = new Fixture();
        fixture.Throw = new OperationCanceledException();

        using var service = fixture.CreateService();

        var result = await service.RefreshAsync(fixture.ProbeDirectory);

        Assert.Equal(ModelCatalogProbeOutcome.Cancelled, result.Outcome);
    }

    // ---------------------------------------------------------------------------------------------
    // BeginRefreshIfStale: the actual startup call. Its whole job is to usually do nothing.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void BeginRefreshIfStale_FreshCache_DoesNothing()
    {
        using var fixture = new Fixture();
        fixture.WriteCacheForCurrentBinary(Discovered("Opus", "low"));

        using var service = fixture.CreateService();
        service.BeginRefreshIfStale(fixture.ProbeDirectory);

        Assert.False(fixture.DiscoverStarted.Wait(TimeSpan.FromMilliseconds(400)));
        Assert.Equal(0, fixture.DiscoverCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BeginRefreshIfStale_NoProbeDirectory_DoesNothing(string? directory)
    {
        using var fixture = new Fixture();

        using var service = fixture.CreateService();
        service.BeginRefreshIfStale(directory);

        Assert.False(fixture.DiscoverStarted.Wait(TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public void BeginRefreshIfStale_StaleCache_StartsDiscoveryInTheBackground()
    {
        using var fixture = new Fixture();
        fixture.Result = new ModelCatalogProbeResult(
            ModelCatalogProbeOutcome.Discovered, Discovered("Nimbus", "low"), null, TimeSpan.Zero);

        using var service = fixture.CreateService();
        service.BeginRefreshIfStale(fixture.ProbeDirectory);

        Assert.True(fixture.DiscoverStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(fixture.Published.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal("Nimbus", service.Current.Find("Nimbus")!.CliValue);
    }

    [Fact]
    public void BeginRefreshIfStale_CalledTwice_DoesNotStartASecondConcurrentDiscovery()
    {
        using var fixture = new Fixture();
        fixture.BlockDiscovery = true;

        using var service = fixture.CreateService();
        service.BeginRefreshIfStale(fixture.ProbeDirectory);
        Assert.True(fixture.DiscoverStarted.Wait(TimeSpan.FromSeconds(5)));

        // Two overlapping discoveries would mean two `claude` children fighting over one PTY-driven
        // picker, and two cache writes racing.
        service.BeginRefreshIfStale(fixture.ProbeDirectory);
        service.BeginRefreshIfStale(fixture.ProbeDirectory);

        fixture.ReleaseDiscovery();
        Assert.Equal(1, fixture.DiscoverCalls);
    }

    [Fact]
    public void BeginRefreshIfStale_PassesTheProbeDirectoryThrough()
    {
        using var fixture = new Fixture();

        using var service = fixture.CreateService();
        service.BeginRefreshIfStale(fixture.ProbeDirectory);

        Assert.True(fixture.DiscoverStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(fixture.ProbeDirectory, fixture.LastDirectory);
    }

    // ---------------------------------------------------------------------------------------------
    // Probe-directory selection: a directory Claude Code already trusts, or nothing.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ResolveProbeDirectory_PicksTheFirstRootThatExists()
    {
        using var fixture = new Fixture();

        var resolved = ModelCatalogService.ResolveProbeDirectory(new[]
        {
            Path.Combine(Path.GetTempPath(), $"accel-missing-{Guid.NewGuid():N}"),
            fixture.ProbeDirectory,
        });

        Assert.Equal(fixture.ProbeDirectory, resolved);
    }

    [Fact]
    public void ResolveProbeDirectory_NoUsableRoot_IsNull()
    {
        Assert.Null(ModelCatalogService.ResolveProbeDirectory(null));
        Assert.Null(ModelCatalogService.ResolveProbeDirectory(Array.Empty<string>()));
        Assert.Null(ModelCatalogService.ResolveProbeDirectory(new[] { string.Empty, "   " }));
        Assert.Null(ModelCatalogService.ResolveProbeDirectory(
            new[] { Path.Combine(Path.GetTempPath(), $"accel-missing-{Guid.NewGuid():N}") }));
    }

    // ---------------------------------------------------------------------------------------------

    private static ModelCatalog Discovered(string cliValue, params string[] tiers) =>
        new(
            new[] { new ModelCatalogEntry(cliValue, cliValue + " 5", null, tiers) },
            ModelCatalogSource.Discovered,
            "2.1.263",
            DateTimeOffset.UtcNow);

    /// <summary>
    /// A throwaway <c>~/.claude</c>-shaped world: a stand-in claude.exe (a real file, since the cache
    /// key is its size and last-write time), a cache path, a settings path, and a directory to
    /// "probe". Also the fake discovery function, with signals so a background refresh can be awaited
    /// deterministically instead of slept on.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly ManualResetEventSlim _release = new(true);
        private int _discoverCalls;

        public Fixture()
        {
            _root = Path.Combine(Path.GetTempPath(), $"accel-catalog-service-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);

            ProbeDirectory = Path.Combine(_root, "project");
            Directory.CreateDirectory(ProbeDirectory);

            CliPath = Path.Combine(_root, "claude.exe");
            File.WriteAllText(CliPath, "not a real binary");

            CachePath = Path.Combine(_root, "accel-model-catalog.json");
            SettingsPath = Path.Combine(_root, "settings.json");
        }

        public string ProbeDirectory { get; }

        public string CliPath { get; }

        public string CachePath { get; }

        public string SettingsPath { get; }

        /// <summary>What the fake discovery returns. Defaults to a failure, so a test that cares about
        /// success has to say so explicitly.</summary>
        public ModelCatalogProbeResult Result { get; set; } =
            new(ModelCatalogProbeOutcome.NotParseable, null, null, TimeSpan.Zero);

        /// <summary>When set, the fake discovery throws this instead of returning.</summary>
        public Exception? Throw { get; set; }

        /// <summary>When true, discovery blocks until <see cref="ReleaseDiscovery"/> is called.</summary>
        public bool BlockDiscovery
        {
            get => !_release.IsSet;
            set
            {
                if (value)
                {
                    _release.Reset();
                }
                else
                {
                    _release.Set();
                }
            }
        }

        public ManualResetEventSlim DiscoverStarted { get; } = new(false);

        public ManualResetEventSlim Published { get; } = new(false);

        public int DiscoverCalls => Volatile.Read(ref _discoverCalls);

        public string? LastDirectory { get; private set; }

        public string? CliBinaryIdentity() => ModelCatalogCache.DescribeCliBinary(CliPath);

        public void WriteCacheForCurrentBinary(ModelCatalog catalog) =>
            ModelCatalogCache.TrySave(catalog, CliBinaryIdentity(), CachePath);

        /// <summary>Rewrites the stand-in claude.exe so its identity changes, the way an in-place CLI
        /// upgrade would.</summary>
        public void TouchCliBinary() =>
            File.WriteAllText(CliPath, "a different, longer body so both size and mtime differ");

        public void ReleaseDiscovery() => _release.Set();

        public ModelCatalogService CreateService(string? cliPath = "")
        {
            // "" (the default) means "use the fixture's stand-in"; an explicit null means "claude is
            // not resolvable", which is a case the service has to survive.
            var resolved = cliPath == string.Empty ? CliPath : cliPath;

            var service = new ModelCatalogService(
                discover: Discover,
                cliPathResolver: () => resolved,
                cachePath: CachePath,
                settingsPath: SettingsPath);

            service.CatalogChanged += (_, _) => Published.Set();
            return service;
        }

        private Task<ModelCatalogProbeResult> Discover(string directory, CancellationToken token)
        {
            LastDirectory = directory;
            Interlocked.Increment(ref _discoverCalls);
            DiscoverStarted.Set();

            _release.Wait(TimeSpan.FromSeconds(10));

            if (Throw is not null)
            {
                throw Throw;
            }

            return Task.FromResult(Result);
        }

        public void Dispose()
        {
            _release.Set();
            DiscoverStarted.Dispose();
            Published.Dispose();
            _release.Dispose();

            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

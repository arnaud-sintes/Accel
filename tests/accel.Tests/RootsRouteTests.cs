using System.Net;
using System.Text.Json;
using Accel.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Accel.Tests;

/// <summary>
/// Integration tests for Phase UI-C's <c>GET /roots</c> route, against a real
/// <see cref="EventServer"/> bound to an ephemeral port - mirrors
/// <c>StateQueryRoutesTests</c>'s pattern. Uses <see cref="EventServer.Build"/>'s explicit
/// <c>roots</c> parameter to configure a fixture without touching real filesystem locations
/// (%USERPROFILE%, exe directory, process cwd).
/// </summary>
public class RootsRouteTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HttpClient? _client;
    private string? _tempConfigFile;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _client?.Dispose();

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_tempConfigFile is not null)
        {
            try { File.Delete(_tempConfigFile); } catch { /* best effort cleanup */ }
        }
    }

    private async Task StartServerAsync(string[]? roots)
    {
        _app = EventServer.Build(0, roots: roots);
        await _app.StartAsync();

        var addressesFeature = _app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();

        string address = addressesFeature!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    [Fact]
    public async Task GetRoots_WithConfiguredFixture_ReturnsExpectedArrayVerbatim()
    {
        await StartServerAsync(new[] { "C:/projects", "C:/other" });

        var response = await _client!.GetAsync("/roots");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var values = doc.RootElement.EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(new[] { "C:/projects", "C:/other" }, values);
    }

    [Fact]
    public async Task GetRoots_NoConfigAtAll_Returns200WithEmptyArray()
    {
        await StartServerAsync(Array.Empty<string>());

        var response = await _client!.GetAsync("/roots");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Empty(doc.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task GetRoots_FromRealFixtureFileThroughConfigLoader_ReturnsItsContents()
    {
        _tempConfigFile = Path.Combine(Path.GetTempPath(), $"accel-roots-fixture-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(_tempConfigFile, "[\"C:/from-fixture-file\"]");

        string[] loaded = RootFoldersConfig.Load(new[] { _tempConfigFile });
        await StartServerAsync(loaded);

        var response = await _client!.GetAsync("/roots");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var values = doc.RootElement.EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(new[] { "C:/from-fixture-file" }, values);
    }
}

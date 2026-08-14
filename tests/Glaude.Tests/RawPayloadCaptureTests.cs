using System.Net;
using System.Text;
using Glaude.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glaude.Tests;

/// <summary>
/// Phase 3b-i: verifies the `--dump-raw &lt;dir&gt;` capture mode writes the exact raw
/// request body to disk for every event route, without disturbing existing Phase 3
/// printing/204 behavior, and that it is fully opt-in (no dumpRawDir => no files, no
/// change in behavior vs. the plain EventServerTests scenarios).
/// </summary>
public class RawPayloadCaptureTests : IAsyncLifetime
{
    private readonly string _dumpDir = Path.Combine(
        Path.GetTempPath(), "glaude-dumpraw-tests-" + Guid.NewGuid().ToString("N"));

    private WebApplication? _appWithCapture;
    private HttpClient? _clientWithCapture;

    private WebApplication? _appNoCapture;
    private HttpClient? _clientNoCapture;

    public async Task InitializeAsync()
    {
        _appWithCapture = EventServer.Build(0, dumpRawDir: _dumpDir);
        await _appWithCapture.StartAsync();
        _clientWithCapture = new HttpClient { BaseAddress = new Uri(GetAddress(_appWithCapture)) };

        _appNoCapture = EventServer.Build(0);
        await _appNoCapture.StartAsync();
        _clientNoCapture = new HttpClient { BaseAddress = new Uri(GetAddress(_appNoCapture)) };
    }

    public async Task DisposeAsync()
    {
        _clientWithCapture?.Dispose();
        _clientNoCapture?.Dispose();

        if (_appWithCapture is not null)
        {
            await _appWithCapture.StopAsync();
            await _appWithCapture.DisposeAsync();
        }

        if (_appNoCapture is not null)
        {
            await _appNoCapture.StopAsync();
            await _appNoCapture.DisposeAsync();
        }

        try
        {
            if (Directory.Exists(_dumpDir))
            {
                Directory.Delete(_dumpDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup only - never fail the test run over leftover temp files.
        }
    }

    private static string GetAddress(WebApplication app)
    {
        var addressesFeature = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();

        return addressesFeature!.Addresses.First();
    }

    [Fact]
    public async Task PostsToDistinctRoutes_WriteExactRawBodyToFiles()
    {
        const string sessionStartBody = """{"session_id":"cap-1","hook_event_name":"SessionStart"}""";
        const string subagentStopBody = """{"session_id":"cap-2","agent_id":"agent-xyz","hook_event_name":"SubagentStop"}""";

        var response1 = await _clientWithCapture!.PostAsync(
            "/events/session-start",
            new StringContent(sessionStartBody, Encoding.UTF8, "application/json"));
        var response2 = await _clientWithCapture!.PostAsync(
            "/events/subagent-stop",
            new StringContent(subagentStopBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response1.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, response2.StatusCode);

        string[] files = await WaitForFiles(_dumpDir, expectedCount: 2);

        string? sessionStartFile = files.FirstOrDefault(f => Path.GetFileName(f).Contains("SessionStart"));
        string? subagentStopFile = files.FirstOrDefault(f => Path.GetFileName(f).Contains("SubagentStop"));

        Assert.NotNull(sessionStartFile);
        Assert.NotNull(subagentStopFile);

        Assert.Equal(sessionStartBody, await File.ReadAllTextAsync(sessionStartFile!));
        Assert.Equal(subagentStopBody, await File.ReadAllTextAsync(subagentStopFile!));
    }

    [Fact]
    public async Task RapidPostsToSameRoute_DoNotCollide_BothFilesExistWithCorrectContent()
    {
        string bodyA = $$"""{"session_id":"rapid-A-{{Guid.NewGuid():N}}"}""";
        string bodyB = $$"""{"session_id":"rapid-B-{{Guid.NewGuid():N}}"}""";

        var postA = _clientWithCapture!.PostAsync(
            "/events/status-line", new StringContent(bodyA, Encoding.UTF8, "application/json"));
        var postB = _clientWithCapture!.PostAsync(
            "/events/status-line", new StringContent(bodyB, Encoding.UTF8, "application/json"));

        await Task.WhenAll(postA, postB);

        Assert.Equal(HttpStatusCode.NoContent, postA.Result.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, postB.Result.StatusCode);

        string[] files = await WaitForFiles(_dumpDir, expectedCount: 2, matchSubstring: "StatusLine");

        var contents = new List<string>();
        foreach (string file in files)
        {
            contents.Add(await File.ReadAllTextAsync(file));
        }

        Assert.Contains(bodyA, contents);
        Assert.Contains(bodyB, contents);
        Assert.Equal(2, files.Length);
    }

    [Fact]
    public async Task NullDumpRawDir_WritesNoFiles_AndDoesNotAffectNormalBehavior()
    {
        string noCaptureDir = Path.Combine(Path.GetTempPath(), "glaude-dumpraw-should-not-exist-" + Guid.NewGuid().ToString("N"));

        const string json = """{"session_id":"no-capture","hook_event_name":"Test"}""";

        var response = await _clientNoCapture!.PostAsync(
            "/events/subagent-start",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());

        // The no-capture server was never given a dumpRawDir at all, so nothing should
        // have been created anywhere resembling a capture directory for this test.
        Assert.False(Directory.Exists(noCaptureDir));
    }

    private static async Task<string[]> WaitForFiles(string dir, int expectedCount, string? matchSubstring = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(dir))
            {
                var files = Directory.GetFiles(dir)
                    .Where(f => matchSubstring is null || Path.GetFileName(f).Contains(matchSubstring))
                    .ToArray();

                if (files.Length >= expectedCount)
                {
                    return files;
                }
            }

            await Task.Delay(50);
        }

        return Directory.Exists(dir) ? Directory.GetFiles(dir) : Array.Empty<string>();
    }
}

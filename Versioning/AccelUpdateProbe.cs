namespace Accel.Versioning;

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

/// <summary>A newer release found on GitHub, with just what a "download this" prompt needs.</summary>
public sealed record AccelUpdateInfo(Version Version, string HtmlUrl);

/// <summary>
/// Checks GitHub's "latest release" endpoint for a newer Accel version than the one currently
/// running. Never throws: network failure, rate-limiting, an unparseable tag - all degrade to "no
/// update found" rather than surfacing an error, the same posture as <see cref="ClaudeVersionProbe"/>.
/// </summary>
public static class AccelUpdateProbe
{
    private const string ReleasesUrl = "https://api.github.com/repos/arnaud-sintes/Accel/releases/latest";

    // GitHub's REST API rejects any request with no User-Agent (returns 403), so one is always sent.
    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Accel", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>
    /// Gets the latest published (non-draft, non-prerelease - GitHub's "latest release" endpoint
    /// already excludes both) release, or null if it can't be determined for any reason.
    /// </summary>
    public static async Task<AccelUpdateInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Http.GetAsync(ReleasesUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var release = await response.Content
                .ReadFromJsonAsync<GitHubRelease>(cancellationToken)
                .ConfigureAwait(false);

            if (release?.TagName is not { } tag)
            {
                return null;
            }

            // Tags/release titles are published as plain "X.Y.Z" - the same text as accel.csproj's
            // <Version> (see publish.ps1's Get-AccelVersion and past releases, e.g. tag "0.4.0", title
            // "Accel 0.4.0" - never "v0.4.0"). A leading "v" is stripped anyway, defensively, in case
            // that convention ever changes.
            var versionPart = tag.StartsWith('v') ? tag[1..] : tag;
            if (!Version.TryParse(versionPart, out var version))
            {
                return null;
            }

            return new AccelUpdateInfo(version, release.HtmlUrl ?? ReleasesPageUrl);
        }
        catch
        {
            return null;
        }
    }

    private const string ReleasesPageUrl = "https://github.com/arnaud-sintes/Accel/releases";

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }
}

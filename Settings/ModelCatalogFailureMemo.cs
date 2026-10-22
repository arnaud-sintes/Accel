namespace Accel.Settings;

using System;
using System.IO;
using System.Text.Json;

/// <summary>
/// Remembers that reading the model list from a given `claude.exe` was tried and refused, so the next
/// launch does not repeat a ~45-second blocking scan that is going to fail the same way.
///
/// <para>Why it is needed: a scrape that fails validation caches nothing, so without a memory the cache stays
/// "absent" and every start would show the blocking update dialog again. With one, startup skips the scan
/// while the failure is recent <b>and</b> the binary is the same one that failed - an update to Claude Code
/// (the likeliest thing to fix a layout the probe cannot read) changes the binary's identity and ends the
/// cooldown immediately, and so does the cooldown running out.</para>
///
/// <para>Only a refused read is remembered. A login or folder-trust gate is something the user can fix and
/// is retried next launch; a missing `claude` is not a failure of the probe at all.</para>
///
/// <para>Never throws: an unreadable or malformed memo reads as "no failure remembered", and a failed write
/// is swallowed - the worst outcome of either is one more scan.</para>
/// </summary>
public static class ModelCatalogFailureMemo
{
    /// <summary>How long a failed read suppresses the automatic scan for the same binary.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>The memo sits next to the catalog cache: <c>accel-model-catalog.failed.json</c>.</summary>
    public static string PathFor(string? cachePath)
    {
        var cache = cachePath ?? ModelCatalogCache.DefaultPath();
        var directory = Path.GetDirectoryName(Path.GetFullPath(cache));
        return Path.Combine(directory ?? ".", Path.GetFileNameWithoutExtension(cache) + ".failed.json");
    }

    /// <summary>Notes that reading the models from the binary identified by <paramref name="cliBinaryIdentity"/>
    /// was refused for <paramref name="reason"/>. A null identity cannot be matched later, so nothing is kept.</summary>
    public static void Record(string? cachePath, string? cliBinaryIdentity, string? reason, DateTimeOffset? now = null)
    {
        if (cliBinaryIdentity is null)
        {
            return;
        }

        try
        {
            var file = PathFor(cachePath);
            Directory.CreateDirectory(Path.GetDirectoryName(file) ?? ".");
            File.WriteAllText(
                file,
                JsonSerializer.Serialize(
                    new Memo
                    {
                        CliBinaryIdentity = cliBinaryIdentity,
                        FailedAtUtc = now ?? DateTimeOffset.UtcNow,
                        Reason = reason,
                    },
                    SerializerOptions));
        }
        catch
        {
            // Failing to remember a failure costs one more scan, nothing else.
        }
    }

    /// <summary>The remembered failure for <paramref name="cliBinaryIdentity"/>, or null when there is none,
    /// it is for a different binary, or its cooldown has run out.</summary>
    public static ModelCatalogFailure? TryGetActive(string? cachePath, string? cliBinaryIdentity, DateTimeOffset? now = null)
    {
        if (cliBinaryIdentity is null)
        {
            return null;
        }

        try
        {
            var file = PathFor(cachePath);
            if (!File.Exists(file))
            {
                return null;
            }

            var memo = JsonSerializer.Deserialize<Memo>(File.ReadAllText(file), SerializerOptions);
            if (memo?.FailedAtUtc is not { } failedAt ||
                !string.Equals(memo.CliBinaryIdentity, cliBinaryIdentity, StringComparison.Ordinal))
            {
                return null;
            }

            var retryAt = failedAt + Cooldown;
            return (now ?? DateTimeOffset.UtcNow) < retryAt
                ? new ModelCatalogFailure(failedAt, retryAt, memo.Reason)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Forgets any remembered failure (a read has since succeeded).</summary>
    public static void Clear(string? cachePath)
    {
        try
        {
            var file = PathFor(cachePath);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch
        {
            // A stale memo is harmless: it only applies to the binary that failed, for a day.
        }
    }

    private sealed class Memo
    {
        public string? CliBinaryIdentity { get; set; }

        public DateTimeOffset? FailedAtUtc { get; set; }

        public string? Reason { get; set; }
    }
}

/// <summary>A remembered, still-active failure to read the model list.</summary>
public sealed record ModelCatalogFailure(DateTimeOffset FailedAtUtc, DateTimeOffset RetryAtUtc, string? Reason);

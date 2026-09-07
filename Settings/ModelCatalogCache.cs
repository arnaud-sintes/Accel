namespace Accel.Settings;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Accel.Metrics;

/// <summary>
/// Persists a discovered <see cref="ModelCatalog"/> next to Accel's other state, and decides whether
/// a cached one is still good.
///
/// <para><b>Why the cache key is the executable's identity, not a version string.</b> The model
/// lineup only changes when Claude Code itself changes, so the natural key is "which claude.exe is
/// this". Asking it (<c>claude --version</c>) would mean spawning a process on every single Accel
/// start just to decide whether to spawn another one; stat-ing the file costs nothing and answers
/// the same question, since an in-place upgrade rewrites it. The version string the picker banner
/// reports is still stored, but only as provenance for the UI - never as the freshness test.</para>
///
/// <para><b>Staleness is a fallback, not the mechanism.</b> <see cref="MaxAge"/> exists only so a
/// catalog cannot outlive a CLI that changed its lineup without changing its own binary (an entitlement
/// change, an org policy change - neither of which touches claude.exe). It is deliberately long:
/// discovery drives a real TUI, so it should be rare.</para>
///
/// <para>All reads are defensive. A corrupt, unreadable, or schema-mismatched cache is not an error
/// worth reporting - it degrades to "no cache", which degrades to
/// <see cref="ModelCatalog.BuiltIn"/>. Same posture as <see cref="SettingsFile"/>'s load statuses,
/// minus the repair path: there is nothing here worth repairing, only re-discovering.</para>
/// </summary>
public static class ModelCatalogCache
{
    /// <summary>How long a cached catalog stays usable even when claude.exe has not changed.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// <c>%USERPROFILE%\.claude\accel-model-catalog.json</c> - alongside <c>accel-state.json</c> and
    /// <c>accel-folders.json</c>, the same directory Accel already owns files in.
    /// </summary>
    public static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "accel-model-catalog.json");

    /// <summary>
    /// The identity of the `claude` binary at <paramref name="claudeExePath"/>: its full path, size
    /// and last-write time, as one comparable string. Null when the file cannot be stat-ed, which
    /// forces a re-discovery rather than trusting a cache against an unknown binary.
    /// </summary>
    public static string? DescribeCliBinary(string? claudeExePath)
    {
        if (string.IsNullOrWhiteSpace(claudeExePath))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(claudeExePath);
            if (!info.Exists)
            {
                return null;
            }

            return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the cached catalog, or null when there is none, it cannot be parsed, it was written by an
    /// incompatible schema, it was discovered against a different `claude` binary than
    /// <paramref name="cliBinaryIdentity"/>, or it is older than <see cref="MaxAge"/>. Never throws.
    /// </summary>
    public static ModelCatalog? TryLoad(string? path = null, string? cliBinaryIdentity = null, DateTimeOffset? now = null)
    {
        var file = path ?? DefaultPath();
        CachedCatalog? cached;
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            cached = JsonSerializer.Deserialize<CachedCatalog>(File.ReadAllText(file), SerializerOptions);
        }
        catch (Exception)
        {
            // Corrupt or unreadable - indistinguishable from absent, and treated the same.
            return null;
        }

        if (cached is null || cached.SchemaVersion != CachedCatalog.CurrentSchemaVersion || cached.Models is null)
        {
            return null;
        }

        // An identity of null on either side means "cannot compare", which must not silently pass:
        // a catalog discovered from a claude.exe we can no longer identify is not known to be current.
        if (cliBinaryIdentity is not null &&
            !string.Equals(cached.CliBinaryIdentity, cliBinaryIdentity, StringComparison.Ordinal))
        {
            return null;
        }

        if (cached.DiscoveredAtUtc is { } discoveredAt &&
            (now ?? DateTimeOffset.UtcNow) - discoveredAt > MaxAge)
        {
            return null;
        }

        var entries = new List<ModelCatalogEntry>(cached.Models.Count);
        foreach (var model in cached.Models)
        {
            if (string.IsNullOrWhiteSpace(model.CliValue))
            {
                continue;
            }

            entries.Add(new ModelCatalogEntry(
                model.CliValue,
                string.IsNullOrWhiteSpace(model.DisplayName) ? model.CliValue : model.DisplayName,
                model.Description,
                (IReadOnlyList<string>?)model.EffortTiers ?? Array.Empty<string>()));
        }

        if (entries.Count == 0)
        {
            return null;
        }

        return new ModelCatalog(
            entries,
            ModelCatalogSource.Discovered,
            cached.ClaudeCliVersion,
            cached.DiscoveredAtUtc,
            cached.DefaultModelHint);
    }

    /// <summary>
    /// Writes <paramref name="catalog"/> to <paramref name="path"/>, stamped with
    /// <paramref name="cliBinaryIdentity"/>. Returns false on any failure - a cache that cannot be
    /// written costs a re-discovery next start, which is not worth surfacing to the user.
    /// </summary>
    public static bool TrySave(
        ModelCatalog catalog, string? cliBinaryIdentity, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var file = path ?? DefaultPath();
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(file));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var payload = new CachedCatalog
            {
                SchemaVersion = CachedCatalog.CurrentSchemaVersion,
                CliBinaryIdentity = cliBinaryIdentity,
                ClaudeCliVersion = catalog.ClaudeCliVersion,
                DefaultModelHint = catalog.DefaultModelHint,
                DiscoveredAtUtc = catalog.DiscoveredAtUtc ?? DateTimeOffset.UtcNow,
                Models = new List<CachedModel>(),
            };

            foreach (var entry in catalog.Entries)
            {
                payload.Models.Add(new CachedModel
                {
                    CliValue = entry.CliValue,
                    DisplayName = entry.DisplayName,
                    Description = entry.Description,
                    EffortTiers = new List<string>(entry.EffortTiers),
                });
            }

            // Written via a temp file and a move, so a crash mid-write cannot leave a half-file that
            // the next start would read as corrupt (and then silently discard).
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(payload, SerializerOptions));
            File.Move(temp, file, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>On-disk shape. Versioned so a future change to what discovery records invalidates old
    /// files by construction instead of mis-reading them.</summary>
    private sealed class CachedCatalog
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; }

        public string? CliBinaryIdentity { get; set; }

        public string? ClaudeCliVersion { get; set; }

        public string? DefaultModelHint { get; set; }

        public DateTimeOffset? DiscoveredAtUtc { get; set; }

        public List<CachedModel>? Models { get; set; }
    }

    private sealed class CachedModel
    {
        public string? CliValue { get; set; }

        public string? DisplayName { get; set; }

        public string? Description { get; set; }

        public List<string>? EffortTiers { get; set; }
    }
}

namespace Accel.Orchestration;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Accel.Settings;

/// <summary>
/// P2-T7: persists <c>accel-sessions.json</c>, the on-disk PID registry backing orphan
/// reconciliation on next startup (locked-in decision 7 / risk register item 5). Wiring this
/// into an actual startup-reconciliation UI is P3-T4's job; this class is only the primitive it
/// will need — a tolerant, atomic-write store plus a pure staleness check.
///
/// <para><b>Shape on disk:</b> a JSON array of entries, each
/// <c>{"sessionId","pid","processStartTimeUtc","cwd","launchedAtUtc"}</c> — mirrors the shape
/// called out in the plan for <c>accel-sessions.json</c>, sibling to <c>accel-state.json</c>
/// under <c>%USERPROFILE%\.claude\</c>.</para>
///
/// <para><b>Write mechanism:</b> reuses <see cref="SettingsFile"/>'s atomic temp-file-plus-backup
/// write (the same pattern <see cref="Accel.Server.RootFoldersConfig"/> already reuses for its
/// own v2 schema) rather than hand-rolling a second file writer.</para>
///
/// <para><b>Error tolerance:</b> matches this codebase's established convention (see
/// <see cref="Accel.Server.RootFoldersConfig.LoadFull(IReadOnlyList{string})"/> and
/// <see cref="Accel.Cli.FileBackedStatusLineChainStore"/>) — a missing, empty, or malformed file
/// degrades to an empty registry. <see cref="LoadAll()"/> never throws.</para>
/// </summary>
public sealed class PtyPidRegistry
{
    public const string DefaultFileName = "accel-sessions.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string path;

    public PtyPidRegistry(string path)
    {
        this.path = path ?? throw new ArgumentNullException(nameof(path));
    }

    /// <summary>The default on-disk location, alongside <c>accel-state.json</c>/<c>accel-folders.json</c>.</summary>
    public static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            DefaultFileName);

    /// <summary>
    /// Loads every entry currently on disk. Never throws — a missing file yields an empty list;
    /// a malformed file (unparseable JSON, non-array root, or an array containing anything that
    /// isn't a well-formed entry object) also degrades to an empty list rather than a partial
    /// read, matching <see cref="Accel.Server.RootFoldersConfig"/>'s "found it, but it's broken
    /// -> empty" rule.
    /// </summary>
    public IReadOnlyList<PtyPidEntry> LoadAll()
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<PtyPidEntry>();
            }

            string text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<PtyPidEntry>();
            }

            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<PtyPidEntry>();
            }

            var result = new List<PtyPidEntry>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (TryParseEntry(element, out var entry))
                {
                    result.Add(entry);
                }
            }

            return result;
        }
        catch
        {
            // Malformed/unreadable file - degrade to empty rather than throw or partially load.
            return Array.Empty<PtyPidEntry>();
        }
    }

    /// <summary>
    /// Adds (or replaces, if one already exists for the same <see cref="PtyPidEntry.SessionId"/>)
    /// an entry, then writes the whole registry back atomically. Best-effort: a write failure is
    /// swallowed rather than thrown, matching <see cref="Accel.Cli.FileBackedStatusLineChainStore"/>'s
    /// convention that persistence of this kind of side-table must never abort the caller's real
    /// work (spawning/tearing down a PTY session).
    /// </summary>
    public void Add(PtyPidEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            var entries = new List<PtyPidEntry>(LoadAll());
            entries.RemoveAll(e => string.Equals(e.SessionId, entry.SessionId, StringComparison.Ordinal));
            entries.Add(entry);
            WriteAtomic(entries);
        }
        catch
        {
            // Best effort only - see class remarks.
        }
    }

    /// <summary>Removes the entry for <paramref name="sessionId"/>, if any, and writes the registry back atomically.</summary>
    public void Remove(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);

        try
        {
            var entries = new List<PtyPidEntry>(LoadAll());
            int removed = entries.RemoveAll(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal));
            if (removed == 0)
            {
                return;
            }

            WriteAtomic(entries);
        }
        catch
        {
            // Best effort only - see class remarks.
        }
    }

    /// <summary>
    /// Pure staleness check (the PID-reuse guard from locked-in decision 7 / risk register item
    /// 5): an entry is stale if either its PID no longer identifies a live process, or a live
    /// process exists at that PID but its actual start time does not match the recorded
    /// <see cref="PtyPidEntry.ProcessStartTimeUtc"/> (i.e. the PID has been reused by an unrelated
    /// process since the entry was written).
    ///
    /// <para>Takes <paramref name="isProcessAlive"/>/<paramref name="getProcessStartTimeUtc"/> as
    /// injected functions (rather than calling <c>System.Diagnostics.Process</c> directly) so this
    /// is a pure, unit-testable function with no real process spawning required. Wiring this to
    /// the real OS process table, and to any reconciliation UI, is P3-T4's job.</para>
    /// </summary>
    /// <param name="entries">Registry entries to check, typically the result of <see cref="LoadAll"/>.</param>
    /// <param name="isProcessAlive">Returns whether a process with the given PID currently exists.</param>
    /// <param name="getProcessStartTimeUtc">
    /// Returns the actual UTC start time of the live process with the given PID, or
    /// <see langword="null"/> if it cannot be determined (treated the same as a mismatch: stale).
    /// </param>
    public static IReadOnlyList<PtyPidEntry> Reconcile(
        IReadOnlyList<PtyPidEntry> entries,
        Func<int, bool> isProcessAlive,
        Func<int, DateTime?> getProcessStartTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(isProcessAlive);
        ArgumentNullException.ThrowIfNull(getProcessStartTimeUtc);

        var stale = new List<PtyPidEntry>();
        foreach (var entry in entries)
        {
            if (!isProcessAlive(entry.Pid))
            {
                stale.Add(entry);
                continue;
            }

            var actualStart = getProcessStartTimeUtc(entry.Pid);
            if (actualStart is null || !StartTimesMatch(actualStart.Value, entry.ProcessStartTimeUtc))
            {
                stale.Add(entry);
            }
        }

        return stale;
    }

    private static bool StartTimesMatch(DateTime a, DateTime b)
    {
        // Process start times as reported by the OS are not sub-second-precise in every code
        // path; allow a small tolerance rather than requiring bit-exact equality.
        return (a.ToUniversalTime() - b.ToUniversalTime()).Duration() < TimeSpan.FromSeconds(2);
    }

    private static bool TryParseEntry(JsonElement element, out PtyPidEntry entry)
    {
        entry = null!;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!element.TryGetProperty("sessionId", out var sessionIdEl) || sessionIdEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (!element.TryGetProperty("pid", out var pidEl) || pidEl.ValueKind != JsonValueKind.Number || !pidEl.TryGetInt32(out int pid))
        {
            return false;
        }

        if (!element.TryGetProperty("processStartTimeUtc", out var startEl) ||
            startEl.ValueKind != JsonValueKind.String ||
            !TryParseDateTime(startEl.GetString(), out var startTime))
        {
            return false;
        }

        string cwd = element.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String
            ? cwdEl.GetString() ?? string.Empty
            : string.Empty;

        DateTime launchedAt = DateTime.MinValue;
        if (element.TryGetProperty("launchedAtUtc", out var launchedEl) &&
            launchedEl.ValueKind == JsonValueKind.String &&
            TryParseDateTime(launchedEl.GetString(), out var parsedLaunched))
        {
            launchedAt = parsedLaunched;
        }

        entry = new PtyPidEntry(sessionIdEl.GetString() ?? string.Empty, pid, startTime, cwd, launchedAt);
        return true;
    }

    private static bool TryParseDateTime(string? text, out DateTime result)
    {
        result = default;
        if (text is null)
        {
            return false;
        }

        return DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out result);
    }

    private void WriteAtomic(IReadOnlyList<PtyPidEntry> entries)
    {
        var array = new JsonArray();
        foreach (var entry in entries)
        {
            array.Add(new JsonObject
            {
                ["sessionId"] = entry.SessionId,
                ["pid"] = entry.Pid,
                ["processStartTimeUtc"] = entry.ProcessStartTimeUtc.ToUniversalTime().ToString("o"),
                ["cwd"] = entry.Cwd,
                ["launchedAtUtc"] = entry.LaunchedAtUtc.ToUniversalTime().ToString("o"),
            });
        }

        // SettingsFile is used purely as an atomic-write handle here (temp-file-then-replace,
        // plus a one-time .accel.bak snapshot of whatever previously lived at `path`) - the
        // same reuse RootFoldersConfig.Save already makes for its own, differently-shaped file.
        SettingsFile.Load(path).Save(array);
    }
}

/// <summary>One PID-registry entry: a `claude` process launched by Accel, tracked for orphan reconciliation.</summary>
/// <param name="SessionId">The Accel/Claude Code session id (GUID) this process was launched for.</param>
/// <param name="Pid">The OS process id at launch time.</param>
/// <param name="ProcessStartTimeUtc">
/// The process's actual start time (e.g. <c>Process.StartTime</c>, converted to UTC) as observed
/// at launch - the PID-reuse guard: a later PID match alone is not proof it's the same process.
/// </param>
/// <param name="Cwd">The working directory the process was launched with.</param>
/// <param name="LaunchedAtUtc">Wall-clock time Accel issued the launch.</param>
public sealed record PtyPidEntry(string SessionId, int Pid, DateTime ProcessStartTimeUtc, string Cwd, DateTime LaunchedAtUtc);

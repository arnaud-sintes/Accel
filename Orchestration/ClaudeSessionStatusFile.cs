namespace Accel.Orchestration;

using System;
using System.IO;
using System.Text;
using System.Text.Json;

/// <summary>
/// One <c>~/.claude/sessions/&lt;pid&gt;.json</c> snapshot - the live-status side-channel Claude Code
/// itself maintains per process (distinct from <see cref="PtyPidRegistry"/>'s <c>accel-sessions.json</c>,
/// which is Accel's own launch registry). P4-T1/T2 read this to gate slash-command injection on the
/// session actually being idle.
/// </summary>
/// <param name="Pid">The process id this snapshot was read for.</param>
/// <param name="SessionId">The Claude Code session GUID, if present.</param>
/// <param name="Name">The session's current display name, if present.</param>
/// <param name="NameSource">How <see cref="Name"/> was derived (e.g. <c>"derived"</c>), if present.</param>
/// <param name="Status">The raw status literal (e.g. <see cref="ClaudeSessionStatusFile.StatusIdle"/>/
/// <see cref="ClaudeSessionStatusFile.StatusBusy"/>), if present.</param>
/// <param name="UpdatedAt">The file's own <c>updatedAt</c> epoch-millis field, if present.</param>
public sealed record ClaudeSessionStatusSnapshot(
    int Pid,
    string? SessionId,
    string? Name,
    string? NameSource,
    string? Status,
    long? UpdatedAt);

/// <summary>
/// Tolerant reader for <c>~/.claude/sessions/&lt;pid&gt;.json</c>. Matches this codebase's established
/// convention (<see cref="PtyPidRegistry"/>, <see cref="Accel.Server.RootFoldersConfig"/>,
/// <see cref="Accel.Cli.FileBackedStatusLineChainStore"/>) - missing/empty/malformed degrades to
/// <see langword="null"/> rather than throwing, since this file is written by a live, external process
/// and can legitimately be caught mid-write.
/// </summary>
public static class ClaudeSessionStatusFile
{
    /// <summary>
    /// The literal Claude Code writes to the <c>status</c> field while not processing a turn.
    /// Confirmed against a real, live <c>sessions/&lt;pid&gt;.json</c> on this machine (the plan's addendum
    /// had only ever observed <c>"busy"</c> on disk and left <c>"idle"</c> as an assumption - now verified).
    /// </summary>
    public const string StatusIdle = "idle";

    /// <summary>The literal Claude Code writes to the <c>status</c> field while processing a turn.</summary>
    public const string StatusBusy = "busy";

    /// <summary>The default on-disk location for one pid's status file.</summary>
    public static string DefaultPath(int pid, string? homeDirOverride = null) =>
        Path.Combine(
            homeDirOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "sessions",
            $"{pid}.json");

    /// <summary>
    /// Reads and parses the status file for <paramref name="pid"/>. Never throws: a missing file, an
    /// empty file, a non-object root, or unparseable JSON all yield <see langword="null"/> - the same
    /// "gate must fail closed" rule P4-T2 applies on top of this (unknown status refuses injection, it
    /// never falls back to "assume idle").
    /// </summary>
    public static ClaudeSessionStatusSnapshot? TryRead(int pid, string? homeDirOverride = null)
    {
        try
        {
            string path = DefaultPath(pid, homeDirOverride);
            if (!File.Exists(path))
            {
                return null;
            }

            string text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new ClaudeSessionStatusSnapshot(
                pid,
                GetString(root, "sessionId"),
                GetString(root, "name"),
                GetString(root, "nameSource"),
                GetString(root, "status"),
                GetLong(root, "updatedAt"));
        }
        catch
        {
            // Malformed/unreadable/mid-write - degrade to "unknown" rather than throw or guess.
            return null;
        }
    }

    /// <summary>
    /// The rename/injection gate: <see langword="true"/> only when a snapshot was actually read and its
    /// <c>status</c> is exactly <see cref="StatusIdle"/>. Missing file, unreadable file, or any other
    /// status value (including an unrecognized future literal) all fail closed to
    /// <see langword="false"/> - never "not busy therefore idle".
    /// </summary>
    public static bool IsIdle(ClaudeSessionStatusSnapshot? snapshot) =>
        snapshot is not null && string.Equals(snapshot.Status, StatusIdle, StringComparison.Ordinal);

    private static string? GetString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static long? GetLong(JsonElement root, string property) =>
        root.TryGetProperty(property, out var element) &&
        element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt64(out long value)
            ? value
            : null;
}

namespace Glaude.Orchestration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

/// <summary>What one <see cref="SessionRemovalTarget"/> is.</summary>
public enum SessionRemovalTargetKind
{
    /// <summary>A whole directory tree to be removed (<c>file-history/&lt;S&gt;/</c>, <c>tasks/&lt;S&gt;/</c>,
    /// <c>session-env/&lt;S&gt;/</c>, <c>projects/&lt;slug&gt;/&lt;S&gt;/</c> - the sub-agent transcript
    /// directory that sits alongside the main transcript file).</summary>
    Directory,

    /// <summary>The session's main transcript file (<c>projects/&lt;slug&gt;/&lt;S&gt;.jsonl</c>).
    /// Locked-in decision 4 requires this be deleted <b>last</b> of everything in
    /// <see cref="SessionRemovalPlan.Targets"/> - see that property's remarks.</summary>
    TranscriptFile,
}

/// <summary>
/// One location the plan wants to remove. <see cref="Exists"/>/<see cref="SizeBytes"/>/<see cref="FileCount"/>
/// describe what is actually on disk right now (a target the plan has already validated as safe can still
/// legitimately not exist - e.g. a session that never wrote to <c>session-env</c>); the executor
/// (P4-T3b) re-checks existence itself immediately before acting, since this snapshot can go stale the
/// moment it is produced.
/// </summary>
public sealed record SessionRemovalTarget(
    string Description,
    SessionRemovalTargetKind Kind,
    string Path,
    bool Exists,
    long SizeBytes,
    int FileCount);

/// <summary>
/// The result of <see cref="SessionRemover.Plan"/>: pure data, no I/O mutation, matching this codebase's
/// convention (<c>PtyCloseResult</c>, <c>PtyOrphanReport</c>) of reporting an operation's shape as data
/// rather than acting on it inline.
/// </summary>
/// <param name="SessionId">The session id, normalized to <c>Guid.ToString("D")</c> form.</param>
/// <param name="Targets">
/// Every location that passed validation, in the order the executor must act on them: everything except
/// the transcript first (in the order listed on <see cref="SessionRemovalTargetKind"/>), then
/// <see cref="SessionRemovalTargetKind.TranscriptFile"/> last. Deleting the transcript last means every
/// other artifact is already gone by the time the one file panel A's tree is actually keyed on
/// disappears - a crash or kill partway through leaves an orphaned-but-still-discoverable session rather
/// than a live-looking session with its supporting data half gone.
/// </param>
/// <param name="HistoryFilePath">The shared, concurrently-appended <c>history.jsonl</c> path.</param>
/// <param name="HistoryFileExists">Whether that file currently exists.</param>
/// <param name="HistoryLinesToRemove">
/// How many lines in it currently reference this session id - informational only (a plan-time count,
/// not a claim about what the executor's rewrite will remove, since the file can grow between planning
/// and execution - see P4-T3b's own line-filter, which re-reads and re-matches at execution time).
/// </param>
/// <param name="TotalBytes">Sum of every existing target's <see cref="SessionRemovalTarget.SizeBytes"/>.</param>
/// <param name="IsSafe">
/// <see langword="false"/> if the session id itself was not a well-formed GUID, or if any candidate
/// target failed validation (see <see cref="SessionRemover.ValidateTarget"/>) - path escape, wrong/mismatched
/// leaf, or a reparse point anywhere between the <c>.claude</c> home and the target. A plan with
/// <see cref="IsSafe"/> false must never be handed to the executor; a failed target is never silently
/// added to <see cref="Targets"/> regardless - <see cref="IsSafe"/> exists so a caller does not have to
/// diff target counts to notice something was rejected.
/// </param>
/// <param name="Warnings">Human-readable reasons for anything rejected, empty when <see cref="IsSafe"/>
/// is true.</param>
public sealed record SessionRemovalPlan(
    string SessionId,
    IReadOnlyList<SessionRemovalTarget> Targets,
    string HistoryFilePath,
    bool HistoryFileExists,
    int HistoryLinesToRemove,
    long TotalBytes,
    bool IsSafe,
    IReadOnlyList<string> Warnings)
{
    /// <summary>The subset of <see cref="Targets"/> that actually exist on disk right now.</summary>
    public IEnumerable<SessionRemovalTarget> ExistingTargets => Targets.Where(t => t.Exists);
}

/// <summary>The result of validating one candidate path.</summary>
internal readonly record struct TargetValidation(bool IsValid, string? RejectionReason);

/// <summary>
/// P4-T3, planner half: builds a <see cref="SessionRemovalPlan"/> for one session id. Pure - never
/// deletes, moves, or renames anything; the only I/O here is read-only (existence checks, directory
/// enumeration for size, reading <c>history.jsonl</c> to count matching lines). The plan is the only
/// source of paths the executor (P4-T3b) is allowed to act on: everything that could make a delete target
/// escape <c>%USERPROFILE%\.claude\</c> is rejected <b>here</b>, so the executor's own job is narrowed to
/// "act on exactly what was already proven safe", not "prove safety while also deleting things".
///
/// <para><b>Do-not-touch, by omission.</b> <c>shell-snapshots/*</c>, <c>paste-cache/*</c>, and
/// <c>.claude.json</c> (locked-in decision 4's explicit exclusions) are never candidates in the first
/// place - there is no code path here that can produce a target under any of those three.</para>
/// </summary>
public static class SessionRemover
{
    private const string HistoryFileName = "history.jsonl";

    /// <summary>
    /// Builds the plan. Never throws for a malformed/missing session id or projectDir, or for any I/O
    /// error while measuring a candidate (a directory that vanishes mid-scan, a file that cannot be
    /// opened) - those degrade to "0 bytes / not counted" rather than aborting the whole plan, since a
    /// best-effort size estimate is all this is ever used for.
    /// </summary>
    /// <param name="sessionId">The session GUID, in any <see cref="Guid"/>-parseable form.</param>
    /// <param name="projectDir">
    /// The project slug this session's transcript lives under (<c>projects/&lt;projectDir&gt;/</c>) -
    /// e.g. <c>SessionTreeDto.ProjectDir</c>, already known by any caller that found this session via
    /// <see cref="Glaude.Metrics.RootsTreeBuilder"/>. Deliberately taken as an input rather than
    /// discovered by scanning every slug directory for a matching id: a caller that already knows which
    /// project a session belongs to should say so, rather than this method trusting a filesystem-wide
    /// search to find the one true match.
    /// </param>
    /// <param name="homeDirOverride">Test seam: overrides <c>%USERPROFILE%</c>. Production callers leave
    /// this null. <b>Tests must always pass a fixture directory here - never the real profile.</b></param>
    public static SessionRemovalPlan Plan(string sessionId, string projectDir, string? homeDirOverride = null)
    {
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(sessionId) || !Guid.TryParseExact(sessionId.Trim(), "D", out var parsedId))
        {
            warnings.Add($"'{sessionId}' is not a well-formed GUID (expected dashed form); refusing to plan any removal.");
            return new SessionRemovalPlan(sessionId ?? string.Empty, Array.Empty<SessionRemovalTarget>(), string.Empty, false, 0, 0, false, warnings);
        }

        if (string.IsNullOrWhiteSpace(projectDir))
        {
            warnings.Add("projectDir is blank; refusing to plan any removal (the transcript location cannot be determined).");
            return new SessionRemovalPlan(parsedId.ToString("D"), Array.Empty<SessionRemovalTarget>(), string.Empty, false, 0, 0, false, warnings);
        }

        string normalizedId = parsedId.ToString("D");
        string homeDir = homeDirOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string claudeHome = Path.Combine(homeDir, ".claude");
        string fullClaudeHome = Path.GetFullPath(claudeHome).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var candidates = new (string Description, SessionRemovalTargetKind Kind, string Path)[]
        {
            ("File history", SessionRemovalTargetKind.Directory, Path.Combine(claudeHome, "file-history", normalizedId)),
            ("Tasks", SessionRemovalTargetKind.Directory, Path.Combine(claudeHome, "tasks", normalizedId)),
            ("Session environment", SessionRemovalTargetKind.Directory, Path.Combine(claudeHome, "session-env", normalizedId)),
            ("Sub-agent transcripts", SessionRemovalTargetKind.Directory, Path.Combine(claudeHome, "projects", projectDir, normalizedId)),
            ("Transcript", SessionRemovalTargetKind.TranscriptFile, Path.Combine(claudeHome, "projects", projectDir, normalizedId + ".jsonl")),
        };

        var targets = new List<SessionRemovalTarget>();
        bool safe = true;

        // Non-transcript targets first (any order among themselves), transcript strictly last -
        // locked-in decision 4 / SessionRemovalPlan.Targets's own contract.
        foreach (var candidate in candidates.OrderBy(c => c.Kind == SessionRemovalTargetKind.TranscriptFile ? 1 : 0))
        {
            var validation = ValidateTarget(candidate.Path, fullClaudeHome, normalizedId);
            if (!validation.IsValid)
            {
                warnings.Add($"{candidate.Description}: {validation.RejectionReason}");
                safe = false;
                continue;
            }

            bool exists = candidate.Kind == SessionRemovalTargetKind.Directory
                ? Directory.Exists(candidate.Path)
                : File.Exists(candidate.Path);

            long size = 0;
            int fileCount = 0;
            if (exists)
            {
                if (candidate.Kind == SessionRemovalTargetKind.Directory)
                {
                    (size, fileCount) = MeasureDirectory(candidate.Path);
                }
                else
                {
                    size = SafeFileLength(candidate.Path);
                    fileCount = 1;
                }
            }

            targets.Add(new SessionRemovalTarget(candidate.Description, candidate.Kind, candidate.Path, exists, size, fileCount));
        }

        string historyPath = Path.Combine(claudeHome, HistoryFileName);
        bool historyExists = File.Exists(historyPath);
        int historyLinesToRemove = historyExists ? CountMatchingHistoryLines(historyPath, normalizedId) : 0;

        long totalBytes = targets.Where(t => t.Exists).Sum(t => t.SizeBytes);

        return new SessionRemovalPlan(normalizedId, targets, historyPath, historyExists, historyLinesToRemove, totalBytes, safe, warnings);
    }

    /// <summary>
    /// The safety gate every candidate path must pass before it can appear in a plan: (1) resolves,
    /// under <see cref="Path.GetFullPath(string)"/>, to somewhere strictly inside
    /// <paramref name="fullClaudeHome"/> - rejects <c>..</c> segments and absolute paths that happen to
    /// be handed in as a "relative" candidate; (2) its leaf (filename without extension, so the
    /// transcript's <c>.jsonl</c> is stripped before comparing) is <i>exactly</i>
    /// <paramref name="expectedGuid"/>'s dashed form, case-insensitively - never a prefix/substring
    /// match; (3) no path component between <paramref name="fullClaudeHome"/> and the target - inclusive
    /// of the target itself - is a reparse point (symlink, junction, or mount point), which is what
    /// closes the "an ancestor directory was replaced with a junction pointing outside .claude" class of
    /// escape. Only checks components that currently exist: a target that does not exist yet has nothing
    /// to walk, and is otherwise valid on path shape alone (a session that never wrote to
    /// <c>session-env</c>, for instance, must still plan as "not present", not fail validation for it).
    /// </summary>
    internal static TargetValidation ValidateTarget(string candidatePath, string fullClaudeHome, string expectedGuid)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            return new TargetValidation(false, $"path could not be resolved ({ex.Message})");
        }

        bool insideHome =
            fullPath.StartsWith(fullClaudeHome + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(fullClaudeHome + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!insideHome)
        {
            return new TargetValidation(false, "target does not resolve to a location under the .claude home directory");
        }

        string leaf = Path.GetFileNameWithoutExtension(fullPath);
        if (!Guid.TryParseExact(leaf, "D", out var parsedLeaf) ||
            !string.Equals(parsedLeaf.ToString("D"), expectedGuid, StringComparison.OrdinalIgnoreCase))
        {
            return new TargetValidation(false, "target's leaf is not an exact match for the session id");
        }

        if (TryFindReparsePoint(fullClaudeHome, fullPath, out string? offendingPath))
        {
            return new TargetValidation(false, $"'{offendingPath}' is a reparse point (symlink/junction/mount) - refusing to act through it");
        }

        return new TargetValidation(true, null);
    }

    /// <summary>Walks from <paramref name="fullPath"/> up to (not including) <paramref name="fullClaudeHome"/>,
    /// returning true at the first existing reparse point found.</summary>
    private static bool TryFindReparsePoint(string fullClaudeHome, string fullPath, out string? offendingPath)
    {
        string? current = fullPath;
        while (current is not null && !string.Equals(current, fullClaudeHome, StringComparison.OrdinalIgnoreCase))
        {
            FileAttributes attributes;
            try
            {
                if (Directory.Exists(current))
                {
                    attributes = new DirectoryInfo(current).Attributes;
                }
                else if (File.Exists(current))
                {
                    attributes = new FileInfo(current).Attributes;
                }
                else
                {
                    // Doesn't exist (yet) at this level - nothing to check, keep walking up in case a
                    // parent that DOES exist has been turned into a reparse point.
                    current = Path.GetDirectoryName(current);
                    continue;
                }
            }
            catch
            {
                // Unreadable attributes - treat as "cannot prove safe" rather than "safe".
                offendingPath = current;
                return true;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                offendingPath = current;
                return true;
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break; // reached a root without ever matching fullClaudeHome - stop rather than loop forever.
            }

            current = parent;
        }

        offendingPath = null;
        return false;
    }

    private static (long SizeBytes, int FileCount) MeasureDirectory(string path)
    {
        long size = 0;
        int count = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                size += SafeFileLength(file);
                count++;
            }
        }
        catch
        {
            // Best-effort only - see this class's remarks.
        }

        return (size, count);
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Counts <c>history.jsonl</c> lines whose <c>sessionId</c> field matches - tolerant of a partial
    /// last line (a concurrent writer mid-append) and of individually malformed lines, matching this
    /// codebase's established "one bad line never aborts the whole read" convention. Informational only;
    /// see <see cref="SessionRemovalPlan.HistoryLinesToRemove"/>.
    /// </summary>
    internal static int CountMatchingHistoryLines(string historyPath, string sessionId)
    {
        int count = 0;
        try
        {
            foreach (string line in File.ReadLines(historyPath, Encoding.UTF8))
            {
                if (LineMatchesSession(line, sessionId))
                {
                    count++;
                }
            }
        }
        catch
        {
            // Best-effort only.
        }

        return count;
    }

    internal static bool LineMatchesSession(string line, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return doc.RootElement.TryGetProperty("sessionId", out var idElement) &&
                   idElement.ValueKind == JsonValueKind.String &&
                   string.Equals(idElement.GetString(), sessionId, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // Malformed/partial line (e.g. a concurrent writer mid-append caught this file at a bad
            // moment) - never matches, never counted, and never throws.
            return false;
        }
    }
}

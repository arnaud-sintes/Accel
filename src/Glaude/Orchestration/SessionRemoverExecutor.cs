namespace Glaude.Orchestration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>Whether a delete goes to the recycle bin (recoverable) or is permanent.</summary>
public enum SessionRemovalMode
{
    /// <summary>Default per locked-in decision 4: <c>SHFileOperationW</c> with <c>FOF_ALLOWUNDO</c> - the
    /// exact same "Delete" a user gets from Explorer, so an accidental removal is recoverable from the
    /// recycle bin.</summary>
    RecycleBin,

    /// <summary>Explicit opt-in, bypassing the recycle bin entirely (<c>Directory.Delete</c>/<c>File.Delete</c>).
    /// Irrecoverable - a caller offering this in the UI should make that unambiguous.</summary>
    PermanentDelete,
}

/// <summary>What happened to one <see cref="SessionRemovalTarget"/> during execution.</summary>
public enum SessionRemovalStepOutcome
{
    /// <summary>Deleted (or moved to the recycle bin) successfully.</summary>
    Removed,

    /// <summary>Nothing was there to remove - not a failure, matches <see cref="SessionRemovalTarget.Exists"/>
    /// being false at plan time or the target having vanished on its own since.</summary>
    NotPresent,

    /// <summary>
    /// Refused before acting: either the target failed re-validation (a plan is never trusted blindly -
    /// see this file's remarks), or the session was observed live at the liveness re-check performed
    /// immediately before this step, or the whole run had already aborted for an earlier target.
    /// </summary>
    Skipped,

    /// <summary>Deletion (or the recycle-bin move) was attempted and threw.</summary>
    Failed,
}

/// <summary>One line of the executor's audit trail - "every path acted on" per the plan's own requirement,
/// recorded for every target regardless of outcome (including a skip or a not-present), never only for
/// successes.</summary>
public sealed record SessionRemovalStepResult(
    string Description,
    string Path,
    SessionRemovalMode? ModeUsed,
    SessionRemovalStepOutcome Outcome,
    string? Detail,
    Exception? Failure);

/// <summary>Why a run stopped early, before every target could be processed. <see cref="None"/> means it
/// ran to completion (which does not by itself mean every target was actually removed - see
/// <see cref="SessionRemovalExecutionResult.FullyRemoved"/>).</summary>
public enum SessionRemovalAbortReason
{
    /// <summary>Ran to completion; nothing aborted it early.</summary>
    None,

    /// <summary><paramref name="isSessionLive"/> (see <see cref="SessionRemoverExecutor.Execute"/>)
    /// reported the session live at a liveness re-check.</summary>
    SessionLive,

    /// <summary>
    /// A target's re-validation failed at execution time even though the plan claimed
    /// <see cref="SessionRemovalPlan.IsSafe"/> - i.e. the plan was tampered with in memory, or the
    /// filesystem shifted under it between planning and execution (a symlink/junction planted after the
    /// plan was built). Distinct from <see cref="SessionLive"/> deliberately: conflating the two under
    /// one flag would make "a session came back to life mid-teardown" indistinguishable from "someone/something
    /// handed the executor a corrupted plan" - very different failure modes for a caller (or an incident
    /// review) to reason about.
    /// </summary>
    RevalidationFailed,
}

/// <summary>The full result of one <see cref="SessionRemoverExecutor.Execute"/> call.</summary>
public sealed record SessionRemovalExecutionResult(
    string SessionId,
    IReadOnlyList<SessionRemovalStepResult> Steps,
    bool HistoryRewriteAttempted,
    bool HistoryRewriteSucceeded,
    int HistoryLinesRemoved,
    SessionRemovalAbortReason AbortReason)
{
    /// <summary>True only when <see cref="AbortReason"/> is <see cref="SessionRemovalAbortReason.SessionLive"/>
    /// - kept as a named property (rather than making every call site compare the enum itself) because it
    /// is the one abort reason a caller is expected to react to directly (e.g. "session became active
    /// again - stop and refresh the UI"), whereas <see cref="SessionRemovalAbortReason.RevalidationFailed"/>
    /// is closer to an integrity-violation bug report.</summary>
    public bool AbortedForLiveness => AbortReason == SessionRemovalAbortReason.SessionLive;

    /// <summary>True only if every step that was attempted succeeded (or correctly found nothing to do)
    /// and nothing was skipped for liveness/validation - i.e. the session's data is now fully gone.</summary>
    public bool FullyRemoved =>
        AbortReason == SessionRemovalAbortReason.None &&
        Steps.All(s => s.Outcome is SessionRemovalStepOutcome.Removed or SessionRemovalStepOutcome.NotPresent) &&
        (!HistoryRewriteAttempted || HistoryRewriteSucceeded);
}

/// <summary>
/// P4-T3b: the executor half of session removal - "the single most dangerous task in the plan" per its
/// own audit note, because unlike the planner (P4-T3, pure and read-only) every method here can
/// irreversibly destroy real user data. Two structural constraints exist specifically to narrow that risk:
///
/// <list type="number">
/// <item><b>The only input is a <see cref="SessionRemovalPlan"/>, and it is never trusted blindly.</b>
/// <see cref="Execute"/> throws immediately if <see cref="SessionRemovalPlan.IsSafe"/> is false, and
/// re-runs <see cref="SessionRemover.ValidateTarget"/> against every target's path again right before
/// acting on it - the planner's own validation is not re-derived here, it is re-invoked, so a plan that
/// was somehow mutated in memory between planning and execution cannot smuggle an unsafe path through.</item>
/// <item><b>A liveness re-check happens immediately before every single delete</b>, not just once at the
/// start - <paramref name="isSessionLive"/> is called before each target and again before the
/// <c>history.jsonl</c> rewrite. The moment it ever reports true, every remaining step is recorded as
/// <see cref="SessionRemovalStepOutcome.Skipped"/> and nothing further is touched: a session that started
/// running again mid-teardown (resumed from another tab, a stale UI command racing a fresh launch) must
/// never have its live data pulled out from under it.</item>
/// </list>
///
/// <para><b>Deliberately no rollback of already-completed steps</b> on a later failure or a live-session
/// abort: a recycle-bin move is itself the undo mechanism (locked-in decision 4's whole reason for
/// defaulting to it over a hard delete), and re-creating an already-deleted directory would be a second,
/// independently risky mutation for no real benefit. A partial run is reported precisely via
/// <see cref="SessionRemovalExecutionResult.Steps"/> and <see cref="SessionRemovalExecutionResult.FullyRemoved"/>
/// rather than hidden behind an all-or-nothing transaction this class cannot actually provide.</para>
/// </summary>
public static class SessionRemoverExecutor
{
    /// <summary>
    /// Executes <paramref name="plan"/>: for every target (directories first, transcript last - the
    /// plan's own ordering is preserved, never re-sorted here), re-validates, liveness-checks, re-checks
    /// existence, then deletes per <paramref name="mode"/>. Once every target has been processed (or the
    /// run aborted early for liveness), rewrites <c>history.jsonl</c> to drop every line matching this
    /// session id - atomically, and tolerant of a concurrently-appended partial last line (see
    /// <see cref="RewriteHistoryFile"/>).
    /// </summary>
    /// <param name="plan">Must have <see cref="SessionRemovalPlan.IsSafe"/> true - see this class's
    /// remarks on why a plan is never trusted blindly regardless.</param>
    /// <param name="mode">Recycle-bin (default, recoverable) or permanent.</param>
    /// <param name="isSessionLive">
    /// Returns whether the session is currently considered live (a running process, a live PID-registry
    /// entry - whatever the caller's own liveness authority is; this class deliberately takes no
    /// dependency on <see cref="PtyRegistry"/> or <see cref="PtyPidRegistry"/> itself, matching
    /// <see cref="PtyOrphanProbes"/>'s own injected-probe pattern). Called immediately before every
    /// target and again before the history rewrite. A null delegate is treated as "never live" - callers
    /// MUST supply a real check in production; leaving it null is only ever correct in a test that has
    /// already guaranteed there is nothing live to race against.
    /// </param>
    /// <param name="homeDirOverride">Test seam, mirrors <see cref="SessionRemover.Plan"/>'s own parameter.
    /// <b>Tests must always pass a fixture directory here - never the real profile.</b></param>
    /// <exception cref="ArgumentException"><paramref name="plan"/>.IsSafe is false.</exception>
    public static SessionRemovalExecutionResult Execute(
        SessionRemovalPlan plan,
        SessionRemovalMode mode = SessionRemovalMode.RecycleBin,
        Func<bool>? isSessionLive = null,
        string? homeDirOverride = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsSafe)
        {
            throw new ArgumentException(
                "Refusing to execute an unsafe plan (SessionRemovalPlan.IsSafe is false) - see its Warnings.",
                nameof(plan));
        }

        string homeDir = homeDirOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string fullClaudeHome = Path.GetFullPath(Path.Combine(homeDir, ".claude"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var steps = new List<SessionRemovalStepResult>();
        var abortReason = SessionRemovalAbortReason.None;

        foreach (var target in plan.Targets)
        {
            if (abortReason != SessionRemovalAbortReason.None)
            {
                steps.Add(new SessionRemovalStepResult(target.Description, target.Path, null,
                    SessionRemovalStepOutcome.Skipped, $"run already aborted ({abortReason})", null));
                continue;
            }

            var revalidation = SessionRemover.ValidateTarget(target.Path, fullClaudeHome, plan.SessionId);
            if (!revalidation.IsValid)
            {
                // The plan already claimed IsSafe=true, so a re-validation failure here means the plan
                // was tampered with (or the filesystem shifted under it, e.g. a symlink planted between
                // planning and execution) - never act on it, and stop the whole run rather than silently
                // skip just this one target.
                steps.Add(new SessionRemovalStepResult(target.Description, target.Path, null,
                    SessionRemovalStepOutcome.Skipped, $"re-validation failed: {revalidation.RejectionReason}", null));
                abortReason = SessionRemovalAbortReason.RevalidationFailed;
                continue;
            }

            if (isSessionLive?.Invoke() == true)
            {
                steps.Add(new SessionRemovalStepResult(target.Description, target.Path, null,
                    SessionRemovalStepOutcome.Skipped, "session is live - refusing to delete", null));
                abortReason = SessionRemovalAbortReason.SessionLive;
                continue;
            }

            steps.Add(DeleteTarget(target, mode));
        }

        bool historyAttempted = false;
        bool historyOk = false;
        int historyRemoved = 0;

        if (abortReason == SessionRemovalAbortReason.None && plan.HistoryFileExists)
        {
            if (isSessionLive?.Invoke() == true)
            {
                abortReason = SessionRemovalAbortReason.SessionLive;
            }
            else
            {
                historyAttempted = true;
                (historyOk, historyRemoved) = RewriteHistoryFile(plan.HistoryFilePath, plan.SessionId);
            }
        }

        return new SessionRemovalExecutionResult(plan.SessionId, steps, historyAttempted, historyOk, historyRemoved, abortReason);
    }

    private static SessionRemovalStepResult DeleteTarget(SessionRemovalTarget target, SessionRemovalMode mode)
    {
        // Re-check existence right before acting - the plan's snapshot can go stale the instant it was
        // taken, and "nothing to delete" is not a failure.
        bool exists = target.Kind == SessionRemovalTargetKind.Directory
            ? Directory.Exists(target.Path)
            : File.Exists(target.Path);

        if (!exists)
        {
            return new SessionRemovalStepResult(target.Description, target.Path, mode, SessionRemovalStepOutcome.NotPresent, null, null);
        }

        try
        {
            if (mode == SessionRemovalMode.RecycleBin)
            {
                RecycleBin.Delete(target.Path);
            }
            else if (target.Kind == SessionRemovalTargetKind.Directory)
            {
                Directory.Delete(target.Path, recursive: true);
            }
            else
            {
                File.Delete(target.Path);
            }

            return new SessionRemovalStepResult(target.Description, target.Path, mode, SessionRemovalStepOutcome.Removed, null, null);
        }
        catch (Exception ex)
        {
            return new SessionRemovalStepResult(target.Description, target.Path, mode, SessionRemovalStepOutcome.Failed, ex.Message, ex);
        }
    }

    /// <summary>
    /// Atomically rewrites <c>history.jsonl</c>, dropping every line <see cref="SessionRemover.LineMatchesSession"/>
    /// matches for <paramref name="sessionId"/>. "Atomic" here means: write the filtered content to a
    /// fresh temp file in the same directory (same volume, so the final swap is a rename rather than a
    /// copy), then <see cref="File.Replace(string, string, string?)"/> it over the original - a reader or
    /// concurrent appender never observes a half-written file, only the old content or the new content.
    ///
    /// <para><b>Tolerant of a concurrently-appended partial last line</b> by construction rather than by
    /// special-casing it: <see cref="SessionRemover.LineMatchesSession"/> returns <see langword="false"/>
    /// for anything it cannot parse as a JSON object with a matching <c>sessionId</c>, so an in-progress
    /// write from another process is preserved verbatim in the output rather than dropped or corrupted -
    /// the same "never delete something we cannot positively identify" rule the GUID-exact leaf check
    /// applies on the filesystem side.</para>
    ///
    /// <para><b>Known residual risk (left for P4-T3c):</b> the read and the replace are not one atomic
    /// unit against a writer appending between them - a line appended after this method's read but before
    /// its <see cref="File.Replace(string, string, string?)"/> is not present in the read and is therefore
    /// silently dropped by the swap, exactly as if the rewrite had raced a concurrent append. There is no
    /// OS-level lock this JSONL file's own append-only writer (Claude Code itself) participates in, so
    /// closing this fully would require either a shared lock protocol neither side currently has, or
    /// re-reading and re-diffing the file immediately before the swap - deliberately not implemented on
    /// the assumption that a rename that lands mid-append is rare and the plan's own audit checklist
    /// (P4-T3c) is exactly the place to decide whether that residual window is acceptable.</para>
    /// </summary>
    private static (bool Success, int LinesRemoved) RewriteHistoryFile(string historyPath, string sessionId)
    {
        string? directory = Path.GetDirectoryName(historyPath);
        if (directory is null)
        {
            return (false, 0);
        }

        string tempPath = Path.Combine(directory, $".{Path.GetFileName(historyPath)}.glaude-remove-{Guid.NewGuid():N}.tmp");

        try
        {
            int removed = 0;
            using (var reader = new StreamReader(historyPath, Encoding.UTF8))
            using (var writer = new StreamWriter(tempPath, append: false, Encoding.UTF8))
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (SessionRemover.LineMatchesSession(line, sessionId))
                    {
                        removed++;
                        continue;
                    }

                    writer.WriteLine(line);
                }
            }

            File.Replace(tempPath, historyPath, destinationBackupFileName: null);
            return (true, removed);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best-effort cleanup of our own temp file - the original history.jsonl is untouched
                // either way, since File.Replace never ran.
            }

            return (false, 0);
        }
    }
}

/// <summary>
/// Thin, correctly-marshalled wrapper over <c>SHFileOperationW</c>'s recycle-bin move - isolated in its
/// own type so the marshalling footgun the plan calls out by name (a double-NUL-terminated path list that
/// ordinary <see langword="string"/> field marshalling truncates at the <i>first</i> embedded NUL) stays
/// in exactly one place. <see cref="Delete"/> only ever moves a single path per call, which sidesteps the
/// multi-path list footgun entirely rather than needing a manually-allocated unmanaged buffer: the
/// marshaller appends its own terminating NUL to an <c>LPWSTR</c> field automatically, so appending one
/// more explicit <c>'\0'</c> in the managed string is sufficient to produce the double-NUL-terminated
/// buffer <c>SHFILEOPSTRUCT.pFrom</c> requires - there is no second path for a stray truncation to corrupt.
/// </summary>
internal static class RecycleBin
{
    private const int FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_NO_UI = FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT fileOp);

    /// <summary>
    /// Moves <paramref name="path"/> (file or directory) to the recycle bin. Throws <see cref="IOException"/>
    /// on failure - <c>SHFileOperationW</c> returns a nonzero result code rather than setting
    /// <c>GetLastError</c>, and <c>fAnyOperationsAborted</c> is checked separately since a "successful"
    /// zero return can still mean nothing happened if the operation was aborted partway through.
    /// </summary>
    public static void Delete(string path)
    {
        var fileOp = new SHFILEOPSTRUCT
        {
            hwnd = IntPtr.Zero,
            wFunc = FO_DELETE,
            // The explicit trailing '\0' plus the marshaller's own implicit terminator produces the
            // double-NUL-terminated single-entry list SHFILEOPSTRUCT.pFrom requires - see this class's
            // remarks. A single path never contains an embedded NUL, so there is nothing for the
            // marshaller's "stop at the first NUL" behaviour to truncate prematurely.
            pFrom = path + "\0",
            pTo = null,
            fFlags = FOF_ALLOWUNDO | FOF_NO_UI,
            fAnyOperationsAborted = 0,
            hNameMappings = IntPtr.Zero,
            lpszProgressTitle = null,
        };

        int result = SHFileOperationW(ref fileOp);
        if (result != 0)
        {
            throw new IOException($"SHFileOperationW failed to move '{path}' to the recycle bin (result 0x{result:X}).");
        }

        if (fileOp.fAnyOperationsAborted != 0)
        {
            throw new IOException($"SHFileOperationW reported the move of '{path}' to the recycle bin was aborted.");
        }
    }
}

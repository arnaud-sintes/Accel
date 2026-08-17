namespace Accel.Orchestration;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

/// <summary>How <see cref="PtyOrphanReconciler"/> classified one <c>accel-sessions.json</c> entry.</summary>
public enum PtyOrphanKind
{
    /// <summary>
    /// The recorded PID is alive <b>and</b> its actual start time matches the recorded one, so this really is
    /// the same <c>claude</c> process a previous Accel run launched, still running now. An
    /// <i>adoptable orphan</i>: the decision point (kill / adopt as detached) applies to it.
    /// </summary>
    Adoptable,

    /// <summary>
    /// The recorded PID is dead, or a live process holds that PID but with a different start time (the PID was
    /// recycled). Either way there is nothing left of that session to reconcile against, so the entry is junk
    /// and gets deleted from the registry file. This is the arm that closes risk register item 5: without the
    /// start-time half of the check, a recycled PID would be reported as a live orphan and could be offered up
    /// for killing - i.e. Accel offering to kill an unrelated process.
    /// </summary>
    Stale,
}

/// <summary>One classified registry entry, with a human-readable reason (for logging/UI, never parsed).</summary>
/// <param name="Entry">The <c>accel-sessions.json</c> entry.</param>
/// <param name="Kind">Adoptable (live, identity-confirmed) or stale (dead or PID-reused).</param>
/// <param name="Reason">Why it was classified that way.</param>
/// <param name="ActualStartTimeUtc">The live process's actual start time, when one could be read - the value the
/// PID-reuse check compared against <see cref="PtyPidEntry.ProcessStartTimeUtc"/>.</param>
public sealed record PtyOrphanClassification(
    PtyPidEntry Entry,
    PtyOrphanKind Kind,
    string Reason,
    DateTime? ActualStartTimeUtc);

/// <summary>
/// The result of a startup reconciliation pass. Pure data - producing it never touches a process and never
/// writes a file (see <see cref="PtyOrphanReconciler.Classify"/>); the file-mutating wrapper is
/// <see cref="PtyOrphanReconciler.ReconcileAtStartup"/>, which reports what it removed in
/// <see cref="StaleEntriesRemoved"/>.
/// </summary>
/// <param name="Classifications">Every entry that was read, in file order, with its verdict.</param>
/// <param name="StaleEntriesRemoved">Whether the stale entries were actually deleted from the registry file
/// (true only for <see cref="PtyOrphanReconciler.ReconcileAtStartup"/>).</param>
public sealed record PtyOrphanReport(
    IReadOnlyList<PtyOrphanClassification> Classifications,
    bool StaleEntriesRemoved)
{
    /// <summary>The live, identity-confirmed orphans - the ones a future UI offers "kill / adopt as detached" for.</summary>
    public IReadOnlyList<PtyPidEntry> Adoptable =>
        Classifications.Where(c => c.Kind == PtyOrphanKind.Adoptable).Select(c => c.Entry).ToArray();

    /// <summary>The junk entries (dead PID or reused PID) - deleted from the registry file, nothing to act on.</summary>
    public IReadOnlyList<PtyPidEntry> Stale =>
        Classifications.Where(c => c.Kind == PtyOrphanKind.Stale).Select(c => c.Entry).ToArray();

    /// <summary>Whether there is a decision point to surface at all.</summary>
    public bool HasAdoptableOrphans => Classifications.Any(c => c.Kind == PtyOrphanKind.Adoptable);

    /// <summary>One-line summary for a log/console line.</summary>
    public string Summarize() =>
        $"{Classifications.Count} registry entr{(Classifications.Count == 1 ? "y" : "ies")}: " +
        $"{Adoptable.Count} adoptable orphan(s), {Stale.Count} stale" +
        (StaleEntriesRemoved ? " (removed)" : " (not removed)");
}

/// <summary>What <see cref="PtyOrphanReconciler.KillOrphan"/>/<see cref="PtyOrphanReconciler.AdoptAsDetached"/> did.</summary>
public enum PtyOrphanActionOutcome
{
    /// <summary>The PID is not alive any more - nothing to do, and the entry was dropped from the registry.</summary>
    AlreadyGone,

    /// <summary>The process was killed (with its descendants) and the entry was dropped from the registry.</summary>
    Killed,

    /// <summary>
    /// The kill was <b>deliberately not attempted</b>: the PID is alive but its start time no longer matches
    /// the recorded one, so the entry has gone stale between the reconciliation report and the action, and the
    /// PID now belongs to some unrelated process. The registry entry is dropped; nothing is killed. This is the
    /// re-check that makes the action safe even against a report the user has been staring at for ten minutes.
    /// </summary>
    RefusedIdentityMismatch,

    /// <summary>Kill was attempted and threw, or the process was still observable afterwards.</summary>
    KillFailed,

    /// <summary>"Adopt as detached": the process is left running, and Accel stopped tracking it.</summary>
    Detached,
}

/// <summary>The outcome of acting on one orphan. Never thrown - reported as data, like <see cref="PtyCloseResult"/>.</summary>
public sealed record PtyOrphanActionResult(
    PtyPidEntry Entry,
    PtyOrphanActionOutcome Outcome,
    string Detail,
    Exception? Failure);

/// <summary>
/// The OS-probing seam used by <see cref="PtyOrphanReconciler"/>: liveness, start time, and kill. Exists so
/// every branch of the classification and of the actions is unit-testable with fakes, exactly the way
/// <see cref="PtyPidRegistry.Reconcile"/> takes its probes as parameters. <see cref="Real"/> is the production
/// instance backed by <see cref="Process"/>.
/// </summary>
/// <param name="IsAlive">Whether a process with that PID currently exists.</param>
/// <param name="GetStartTimeUtc">That process's actual UTC start time, or null if unreadable.</param>
/// <param name="KillTree">Terminates that process and its descendants. Throws on failure.</param>
public sealed record PtyOrphanProbes(
    Func<int, bool> IsAlive,
    Func<int, DateTime?> GetStartTimeUtc,
    Action<int> KillTree)
{
    /// <summary>The real OS process table. All three members are exception-tolerant except
    /// <see cref="KillTree"/>, which is allowed to throw so the caller can report
    /// <see cref="PtyOrphanActionOutcome.KillFailed"/>.</summary>
    public static PtyOrphanProbes Real { get; } = new(
        PtyOrphanReconciler.IsProcessAlive,
        PtyOrphanReconciler.GetProcessStartTimeUtc,
        PtyOrphanReconciler.KillProcessTree);
}

/// <summary>
/// P3-T4, second half: <b>startup orphan reconciliation</b>. Reads <c>accel-sessions.json</c> (via
/// <see cref="PtyPidRegistry.LoadAll"/>), classifies every entry against the real OS process table, deletes the
/// junk, and reports the live ones as a decision point. Closes risk register item 5 (ProcessId ownership across
/// restarts / PID reuse) together with <see cref="PtyRegistry"/>'s in-process PID pinning.
///
/// <para><b>This is a thin orchestration layer over <see cref="PtyPidRegistry.Reconcile"/>, not a second
/// implementation of it.</b> The PID+start-time pairing - the actual PID-reuse guard - lives there and is called
/// from <see cref="Classify"/>; everything added here is (a) turning "stale set" into a two-way classification
/// with reasons, (b) the file cleanup, and (c) the two action primitives a future UI needs. Nothing about the
/// staleness rule is re-derived.</para>
///
/// <para><b>What an adoptable orphan actually means.</b> A live, identity-confirmed entry is a <c>claude</c>
/// process a previous Accel run launched that outlived that run. Every session is assigned to
/// <see cref="AccelJobObject"/> with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, and that fires when the
/// <i>job handle</i> closes - which includes a hard crash or a Task-Manager kill of Accel, since the kernel
/// closes every handle of a dying process. So an orphan surviving is <i>not</i> the ordinary crash case; it means
/// something upstream of kill-on-close did not hold. The realistic causes, and the reason this check exists
/// rather than trusting the job object:
/// <list type="bullet">
/// <item>the job assignment or its rooting failed for that session (see <see cref="AccelJobObject"/>'s
/// "must stay rooted" caveat - a collected job handle closes early, and one that was never assigned never
/// applies), so the child was never in the job at all;</item>
/// <item>the child escaped the job (a nested job, or a descendant created with
/// <c>CREATE_BREAKAWAY_FROM_JOB</c>);</item>
/// <item>the registry file was written but the process outlived Accel for any reason the job could not cover -
/// including a machine where job objects are unavailable/restricted.</item>
/// </list>
/// In all of those, the on-disk PID registry is the only remaining trace, which is exactly why it is written
/// on spawn rather than derived at exit.</para>
///
/// <para><b>Default policy for live orphans: report, never touch (<see cref="ReconcileAtStartup"/>).</b> Stale
/// entries are deleted from the registry file - they are provably junk, and leaving them would make the file grow
/// without bound and keep re-reporting dead PIDs. Adoptable orphans are <i>left alone</i>: left running, and left
/// in the registry file. Two reasons. First, silently killing one would destroy a live session the user may still
/// be using (possibly attached to from another Accel instance - nothing here can tell the difference), and a
/// tool that kills user work at startup without asking is hostile; the plan's own framing is a
/// <i>decision point</i> ("offer kill orphan / adopt as detached"), i.e. the user decides. Second, keeping the
/// entry means the offer survives until it is acted on, instead of being lost the moment the report is closed.
/// The cost of the default is a possible leftover process the user can see and act on - strictly better than an
/// unrecoverable one Accel deleted the record of, or a killed session.</para>
///
/// <para><b>The decision point's primitives</b> are <see cref="KillOrphan"/> ("kill orphan") and
/// <see cref="AdoptAsDetached"/> ("adopt as detached"). No UI is built here - P3-T4 scopes the reconciliation
/// logic and these primitives; the interactive surface belongs to whichever later task owns the shell. Both
/// re-verify identity (or refuse) at the moment of action rather than trusting the report, because an arbitrary
/// amount of time may pass between the two.</para>
///
/// <para><b>Not read here:</b> <c>~/.claude/sessions/&lt;pid&gt;.json</c>. The plan mentions it alongside
/// <c>accel-sessions.json</c>, but it is Claude Code's own per-PID status file (the rename gate in P4-T2), it has
/// no reader anywhere in this codebase yet, and it cannot contribute to the ownership question this task settles:
/// it records no start time, so a <c>&lt;pid&gt;.json</c> match is exactly the PID-only evidence that risk
/// register item 5 is about not trusting. <see cref="PtyPidEntry.ProcessStartTimeUtc"/> - captured by
/// <see cref="PtySession.ProcessStartTimeUtc"/> while the child was still suspended - is the authoritative half,
/// so reconciliation uses <c>accel-sessions.json</c> alone. Cross-reading the status file would only add "and
/// Claude Code also thinks it is alive", which is weaker evidence than what is already in hand.</para>
/// </summary>
public static class PtyOrphanReconciler
{
    /// <summary>
    /// Pure classification: no file writes, no process kills, probes injected. Delegates the staleness rule
    /// itself to <see cref="PtyPidRegistry.Reconcile"/> and then splits the entries into adoptable/stale,
    /// attaching a reason to each.
    /// </summary>
    /// <param name="entries">Entries as read from <c>accel-sessions.json</c>.</param>
    /// <param name="probes">OS probes; only <see cref="PtyOrphanProbes.IsAlive"/> and
    /// <see cref="PtyOrphanProbes.GetStartTimeUtc"/> are used.</param>
    public static PtyOrphanReport Classify(IReadOnlyList<PtyPidEntry> entries, PtyOrphanProbes probes)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(probes);

        // The PID-reuse guard is NOT reimplemented here - this is P2-T7's primitive, called with real probes.
        // Wrapped in the exception-tolerant adapters so a probe that throws (an unreadable process, a hostile test
        // double) degrades that one entry to "stale" - the safe direction - instead of aborting the whole pass.
        // A startup reconciliation must not be able to abort startup.
        var stale = PtyPidRegistry.Reconcile(
            entries,
            pid => SafeIsAlive(probes, pid),
            pid => SafeStartTime(probes, pid));

        // PtyPidEntry is a record, so this is value-equality membership. That is deliberate and sufficient: a
        // file containing two value-equal entries describes the same process twice and must get the same verdict
        // twice, which is exactly what value equality produces.
        var staleSet = new HashSet<PtyPidEntry>(stale);

        var classifications = new List<PtyOrphanClassification>(entries.Count);
        foreach (var entry in entries)
        {
            // Re-probed only to *describe* the verdict. The verdict itself is Reconcile's, so even if a probe
            // answered differently between the two calls, the classification cannot contradict it - at worst the
            // reason text is a moment out of date.
            var alive = SafeIsAlive(probes, entry.Pid);
            var actualStart = alive ? SafeStartTime(probes, entry.Pid) : null;

            if (staleSet.Contains(entry))
            {
                var reason = !alive
                    ? $"pid {entry.Pid} is not alive"
                    : actualStart is null
                        ? $"pid {entry.Pid} is alive but its start time could not be read (treated as reused)"
                        : $"pid {entry.Pid} is alive but started {actualStart:o}, not {entry.ProcessStartTimeUtc:o} (pid reused)";
                classifications.Add(new PtyOrphanClassification(entry, PtyOrphanKind.Stale, reason, actualStart));
            }
            else
            {
                classifications.Add(new PtyOrphanClassification(
                    entry,
                    PtyOrphanKind.Adoptable,
                    $"pid {entry.Pid} is alive and its start time matches {entry.ProcessStartTimeUtc:o}",
                    actualStart));
            }
        }

        return new PtyOrphanReport(classifications, StaleEntriesRemoved: false);
    }

    /// <summary>
    /// The startup pass: load, classify, delete the stale entries from the registry file, and hand back the
    /// report. Live orphans are left running and left in the file - see the class remarks on the default policy.
    ///
    /// <para>Never throws: <see cref="PtyPidRegistry.LoadAll"/> already degrades a missing/malformed file to an
    /// empty list, and <see cref="PtyPidRegistry.Remove"/> is best-effort. A startup diagnostic must not be able
    /// to abort startup.</para>
    /// </summary>
    /// <param name="registry">The PID registry to read and prune (<see cref="PtyPidRegistry.DefaultPath"/> in
    /// production; a temp path in tests/diagnostics - never the real profile from a test).</param>
    /// <param name="probes">Defaults to <see cref="PtyOrphanProbes.Real"/>.</param>
    public static PtyOrphanReport ReconcileAtStartup(PtyPidRegistry registry, PtyOrphanProbes? probes = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var report = Classify(registry.LoadAll(), probes ?? PtyOrphanProbes.Real);

        foreach (var entry in report.Stale)
        {
            registry.Remove(entry.SessionId);
        }

        // True regardless of whether there was anything to remove: it records that this pass *applied* the
        // cleanup policy, which is what distinguishes it from a bare Classify.
        return new PtyOrphanReport(report.Classifications, StaleEntriesRemoved: true);
    }

    /// <summary>
    /// Decision-point primitive #1 - <b>"kill orphan"</b>: terminates the orphan and its descendants, then drops
    /// the entry from the registry.
    ///
    /// <para>Identity is re-verified immediately before the kill (alive + start time still matching) and the kill
    /// is <b>refused</b> if it no longer holds - killing by a PID whose identity cannot be proven is the exact
    /// failure risk register item 5 describes. <c>entireProcessTree</c> because a <c>claude</c> process has
    /// children of its own (node, git, shells) that would otherwise be reparented and left running.</para>
    /// </summary>
    /// <param name="entry">The orphan, as reported by <see cref="ReconcileAtStartup"/>.</param>
    /// <param name="registry">Registry to drop the entry from once it is gone, or null to leave the file alone.</param>
    /// <param name="probes">Defaults to <see cref="PtyOrphanProbes.Real"/>.</param>
    public static PtyOrphanActionResult KillOrphan(
        PtyPidEntry entry,
        PtyPidRegistry? registry = null,
        PtyOrphanProbes? probes = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        probes ??= PtyOrphanProbes.Real;

        if (!SafeIsAlive(probes, entry.Pid))
        {
            Forget(registry, entry);
            return new PtyOrphanActionResult(
                entry,
                PtyOrphanActionOutcome.AlreadyGone,
                $"pid {entry.Pid} had already exited; registry entry dropped",
                null);
        }

        var actualStart = SafeStartTime(probes, entry.Pid);
        if (actualStart is null || !StartTimesMatch(actualStart.Value, entry.ProcessStartTimeUtc))
        {
            // Do not touch it. The registry entry is still dropped: whatever it described is provably gone.
            Forget(registry, entry);
            return new PtyOrphanActionResult(
                entry,
                PtyOrphanActionOutcome.RefusedIdentityMismatch,
                $"pid {entry.Pid} is alive but is not this session (start time {actualStart?.ToString("o") ?? "unreadable"} != {entry.ProcessStartTimeUtc:o}); nothing killed, registry entry dropped",
                null);
        }

        try
        {
            probes.KillTree(entry.Pid);
        }
        catch (Exception ex) when (!SafeIsAlive(probes, entry.Pid))
        {
            // Benign race: it exited between the identity check and the kill.
            Forget(registry, entry);
            return new PtyOrphanActionResult(
                entry,
                PtyOrphanActionOutcome.AlreadyGone,
                $"pid {entry.Pid} exited while being killed ({ex.GetType().Name}); registry entry dropped",
                null);
        }
        catch (Exception ex)
        {
            return new PtyOrphanActionResult(
                entry,
                PtyOrphanActionOutcome.KillFailed,
                $"killing pid {entry.Pid} threw; registry entry kept so it can be retried",
                ex);
        }

        if (SafeIsAlive(probes, entry.Pid))
        {
            return new PtyOrphanActionResult(
                entry,
                PtyOrphanActionOutcome.KillFailed,
                $"pid {entry.Pid} is still observable after the kill; registry entry kept",
                null);
        }

        Forget(registry, entry);
        return new PtyOrphanActionResult(
            entry,
            PtyOrphanActionOutcome.Killed,
            $"pid {entry.Pid} and its descendants were terminated; registry entry dropped",
            null);
    }

    /// <summary>
    /// Decision-point primitive #2 - <b>"adopt as detached"</b>: leaves the process running and stops tracking
    /// it, i.e. removes the registry entry without touching the process.
    ///
    /// <para>"Detached" is the honest description of what Accel can offer today: the process is not in this
    /// run's <see cref="AccelJobObject"/> and there is no ConPTY handle to reattach to (a pseudoconsole cannot be
    /// re-opened by a new parent), so it cannot become a live tab. Dropping the entry stops it being re-reported
    /// at every future startup; the session's own transcript is untouched, so it stays resumable through
    /// <c>claude --resume</c> (P4-T4) once it ends.</para>
    /// </summary>
    public static PtyOrphanActionResult AdoptAsDetached(PtyPidEntry entry, PtyPidRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Forget(registry, entry);
        return new PtyOrphanActionResult(
            entry,
            PtyOrphanActionOutcome.Detached,
            $"pid {entry.Pid} left running; registry entry dropped so it is not re-reported",
            null);
    }

    /// <summary>Real liveness probe. Never throws; an unreadable process is reported as not alive.</summary>
    public static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception)
        {
            // ArgumentException = no such process; anything else = not knowable, which is the same answer for
            // reconciliation purposes (we will not act on it).
            return false;
        }
    }

    /// <summary>Real start-time probe, in UTC. Null when the process is gone or its start time is unreadable
    /// (e.g. access denied on a process owned by another user) - which
    /// <see cref="PtyPidRegistry.Reconcile"/> treats as stale, the safe direction.</summary>
    public static DateTime? GetProcessStartTimeUtc(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Real kill probe. Throws on failure, by contract - <see cref="KillOrphan"/> converts that into
    /// <see cref="PtyOrphanActionOutcome.KillFailed"/>.</summary>
    public static void KillProcessTree(int pid)
    {
        using var process = Process.GetProcessById(pid);
        process.Kill(entireProcessTree: true);
        process.WaitForExit(TimeSpan.FromSeconds(5));
    }

    /// <summary>Same tolerance as <see cref="PtyPidRegistry"/>'s own comparison - OS start times are not
    /// sub-second-precise in every code path.</summary>
    private static bool StartTimesMatch(DateTime a, DateTime b) =>
        (a.ToUniversalTime() - b.ToUniversalTime()).Duration() < TimeSpan.FromSeconds(2);

    private static void Forget(PtyPidRegistry? registry, PtyPidEntry entry)
    {
        try
        {
            registry?.Remove(entry.SessionId);
        }
        catch (Exception)
        {
            // PtyPidRegistry.Remove is already best-effort; belt and braces so an action never throws.
        }
    }

    private static bool SafeIsAlive(PtyOrphanProbes probes, int pid)
    {
        try
        {
            return probes.IsAlive(pid);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static DateTime? SafeStartTime(PtyOrphanProbes probes, int pid)
    {
        try
        {
            return probes.GetStartTimeUtc(pid);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

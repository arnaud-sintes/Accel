using Accel.Orchestration;
using Xunit;

namespace Accel.Tests;

/// <summary>
/// P3-T4 (Half B): unit coverage for <see cref="PtyOrphanReconciler"/> - the adoptable-vs-stale classification,
/// the stale-cleanup / leave-live-orphans-alone default policy, and the two decision-point primitives.
///
/// <para>Everything here uses fake liveness/start-time/kill probes, the same way
/// <see cref="PtyPidRegistryTests"/> tests <see cref="PtyPidRegistry.Reconcile"/> (which this class is a thin
/// orchestration layer over, not a reimplementation of). Real processes are exercised by the
/// <c>pty-shutdown-orphan-test</c> diagnostic verb instead. File-touching tests use a temp path - never the real
/// <c>~/.claude</c> profile.</para>
/// </summary>
public class PtyOrphanReconcilerTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch { /* best effort cleanup */ }
            try { File.Delete(path + Accel.Settings.SettingsFile.BackupSuffix); } catch { /* best effort cleanup */ }
        }
    }

    private string NewTempPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"accel-orphan-test-{Guid.NewGuid():N}.json");
        _tempFiles.Add(path);
        return path;
    }

    private static PtyPidEntry Entry(string sessionId, int pid, DateTime? startTime = null) =>
        new(sessionId, pid, startTime ?? Start, @"C:\projects\Accel", Start.AddMinutes(-1));

    /// <summary>Probes over a fixed pid -> start-time map: a pid in the map is alive with that start time,
    /// anything else is dead. Kills are recorded, not performed.</summary>
    private static PtyOrphanProbes Probes(
        IDictionary<int, DateTime?> live,
        List<int>? killed = null,
        Action<int>? onKill = null) =>
        new(
            IsAlive: pid => live.ContainsKey(pid),
            GetStartTimeUtc: pid => live.TryGetValue(pid, out var start) ? start : null,
            KillTree: pid =>
            {
                killed?.Add(pid);
                onKill?.Invoke(pid);
            });

    // --- Classify ------------------------------------------------------------------------------------------

    [Fact]
    public void Classify_LivePidMatchingStartTime_IsAdoptable()
    {
        var entry = Entry("live", 100);

        var report = PtyOrphanReconciler.Classify(
            new[] { entry },
            Probes(new Dictionary<int, DateTime?> { [100] = Start }));

        Assert.Equal(new[] { entry }, report.Adoptable);
        Assert.Empty(report.Stale);
        Assert.True(report.HasAdoptableOrphans);
        Assert.Equal(PtyOrphanKind.Adoptable, report.Classifications[0].Kind);
        Assert.Equal(Start, report.Classifications[0].ActualStartTimeUtc);
    }

    [Fact]
    public void Classify_DeadPid_IsStale()
    {
        var entry = Entry("dead", 100);

        var report = PtyOrphanReconciler.Classify(new[] { entry }, Probes(new Dictionary<int, DateTime?>()));

        Assert.Equal(new[] { entry }, report.Stale);
        Assert.Empty(report.Adoptable);
        Assert.False(report.HasAdoptableOrphans);
        Assert.Contains("not alive", report.Classifications[0].Reason);
    }

    [Fact]
    public void Classify_ReusedPid_IsStale_NotAdoptable()
    {
        // The whole point of risk register item 5: alive, same pid number, different process.
        var entry = Entry("reused", 100);

        var report = PtyOrphanReconciler.Classify(
            new[] { entry },
            Probes(new Dictionary<int, DateTime?> { [100] = Start.AddHours(3) }));

        Assert.Equal(new[] { entry }, report.Stale);
        Assert.Empty(report.Adoptable);
        Assert.Contains("reused", report.Classifications[0].Reason);
    }

    [Fact]
    public void Classify_LivePidUnreadableStartTime_IsStale()
    {
        var entry = Entry("unreadable", 100);

        var report = PtyOrphanReconciler.Classify(
            new[] { entry },
            Probes(new Dictionary<int, DateTime?> { [100] = null }));

        Assert.Equal(new[] { entry }, report.Stale);
    }

    [Fact]
    public void Classify_StartTimeWithinTolerance_IsAdoptable()
    {
        // OS start times are not sub-second-precise in every code path, so a small skew must not be read as a
        // reused pid - matching PtyPidRegistry.Reconcile's own tolerance.
        var entry = Entry("skewed", 100);

        var report = PtyOrphanReconciler.Classify(
            new[] { entry },
            Probes(new Dictionary<int, DateTime?> { [100] = Start.AddMilliseconds(900) }));

        Assert.Single(report.Adoptable);
    }

    [Fact]
    public void Classify_MixedEntries_SplitsBothWays_AndPreservesFileOrder()
    {
        var live = Entry("live", 1);
        var dead = Entry("dead", 2);
        var reused = Entry("reused", 3);

        var report = PtyOrphanReconciler.Classify(
            new[] { live, dead, reused },
            Probes(new Dictionary<int, DateTime?> { [1] = Start, [3] = Start.AddDays(1) }));

        Assert.Equal(new[] { live }, report.Adoptable);
        Assert.Equal(new[] { dead, reused }, report.Stale);
        Assert.Equal(new[] { live, dead, reused }, report.Classifications.Select(c => c.Entry));
    }

    [Fact]
    public void Classify_EmptyRegistry_IsEmptyReport()
    {
        var report = PtyOrphanReconciler.Classify(Array.Empty<PtyPidEntry>(), PtyOrphanProbes.Real);

        Assert.Empty(report.Classifications);
        Assert.False(report.HasAdoptableOrphans);
        Assert.False(report.StaleEntriesRemoved);
    }

    [Fact]
    public void Classify_ThrowingProbes_DegradeToStale_WithoutEscaping()
    {
        var probes = new PtyOrphanProbes(
            IsAlive: _ => throw new InvalidOperationException("boom"),
            GetStartTimeUtc: _ => throw new InvalidOperationException("boom"),
            KillTree: _ => { });
        var entry = Entry("x", 1);

        // A startup reconciliation must never abort startup, and "cannot probe it" must never be mistaken for
        // "it is a live orphan we could offer to kill".
        var report = PtyOrphanReconciler.Classify(new[] { entry }, probes);

        Assert.Equal(new[] { entry }, report.Stale);
        Assert.Empty(report.Adoptable);
    }

    // --- ReconcileAtStartup (the policy) -------------------------------------------------------------------

    [Fact]
    public void ReconcileAtStartup_RemovesStaleEntries_KeepsLiveOrphans()
    {
        var registry = new PtyPidRegistry(NewTempPath());
        registry.Add(Entry("live", 1));
        registry.Add(Entry("dead", 2));

        var report = PtyOrphanReconciler.ReconcileAtStartup(
            registry,
            Probes(new Dictionary<int, DateTime?> { [1] = Start }));

        Assert.True(report.StaleEntriesRemoved);
        Assert.Single(report.Adoptable);
        Assert.Single(report.Stale);

        // The default policy: junk is deleted, the live orphan is left on disk so the decision point survives
        // until somebody acts on it.
        var onDisk = registry.LoadAll();
        Assert.Single(onDisk);
        Assert.Equal("live", onDisk[0].SessionId);
    }

    [Fact]
    public void ReconcileAtStartup_NeverKillsLiveOrphans()
    {
        var registry = new PtyPidRegistry(NewTempPath());
        registry.Add(Entry("live", 1));
        var killed = new List<int>();

        PtyOrphanReconciler.ReconcileAtStartup(
            registry,
            Probes(new Dictionary<int, DateTime?> { [1] = Start }, killed));

        Assert.Empty(killed);
    }

    [Fact]
    public void ReconcileAtStartup_MissingFile_IsEmptyAndDoesNotThrow()
    {
        var registry = new PtyPidRegistry(NewTempPath());

        var report = PtyOrphanReconciler.ReconcileAtStartup(registry, PtyOrphanProbes.Real);

        Assert.Empty(report.Classifications);
    }

    [Fact]
    public void ReconcileAtStartup_AllStale_EmptiesTheFile()
    {
        var registry = new PtyPidRegistry(NewTempPath());
        registry.Add(Entry("dead-1", 1));
        registry.Add(Entry("dead-2", 2));

        var report = PtyOrphanReconciler.ReconcileAtStartup(registry, Probes(new Dictionary<int, DateTime?>()));

        Assert.Equal(2, report.Stale.Count);
        Assert.Empty(registry.LoadAll());
    }

    // --- decision-point primitives ------------------------------------------------------------------------

    [Fact]
    public void KillOrphan_LiveMatchingEntry_KillsTree_AndDropsRegistryEntry()
    {
        var registry = new PtyPidRegistry(NewTempPath());
        var entry = Entry("live", 100);
        registry.Add(entry);
        var live = new Dictionary<int, DateTime?> { [100] = Start };
        var killed = new List<int>();

        var result = PtyOrphanReconciler.KillOrphan(
            entry,
            registry,
            Probes(live, killed, onKill: pid => live.Remove(pid)));

        Assert.Equal(PtyOrphanActionOutcome.Killed, result.Outcome);
        Assert.Equal(new[] { 100 }, killed);
        Assert.Empty(registry.LoadAll());
    }

    [Fact]
    public void KillOrphan_ReusedPid_RefusesToKill()
    {
        // The load-bearing safety property: a pid whose identity no longer checks out must never be killed, even
        // though a reconciliation report once listed it.
        var entry = Entry("reused", 100);
        var killed = new List<int>();

        var result = PtyOrphanReconciler.KillOrphan(
            entry,
            registry: null,
            Probes(new Dictionary<int, DateTime?> { [100] = Start.AddHours(5) }, killed));

        Assert.Equal(PtyOrphanActionOutcome.RefusedIdentityMismatch, result.Outcome);
        Assert.Empty(killed);
    }

    [Fact]
    public void KillOrphan_ReusedPid_StillDropsTheJunkEntry()
    {
        var registry = new PtyPidRegistry(NewTempPath());
        var entry = Entry("reused", 100);
        registry.Add(entry);

        PtyOrphanReconciler.KillOrphan(
            entry,
            registry,
            Probes(new Dictionary<int, DateTime?> { [100] = Start.AddHours(5) }));

        Assert.Empty(registry.LoadAll());
    }

    [Fact]
    public void KillOrphan_UnreadableStartTime_RefusesToKill()
    {
        var killed = new List<int>();

        var result = PtyOrphanReconciler.KillOrphan(
            Entry("unreadable", 100),
            registry: null,
            Probes(new Dictionary<int, DateTime?> { [100] = null }, killed));

        Assert.Equal(PtyOrphanActionOutcome.RefusedIdentityMismatch, result.Outcome);
        Assert.Empty(killed);
    }

    [Fact]
    public void KillOrphan_AlreadyDeadPid_IsAlreadyGone_AndDropsEntry()
    {
        var registry = new PtyPidRegistry(NewTempPath());
        var entry = Entry("dead", 100);
        registry.Add(entry);
        var killed = new List<int>();

        var result = PtyOrphanReconciler.KillOrphan(entry, registry, Probes(new Dictionary<int, DateTime?>(), killed));

        Assert.Equal(PtyOrphanActionOutcome.AlreadyGone, result.Outcome);
        Assert.Empty(killed);
        Assert.Empty(registry.LoadAll());
    }

    [Fact]
    public void KillOrphan_KillThrowsAndProcessSurvives_ReportsKillFailed_AndKeepsEntryForRetry()
    {
        var registry = new PtyPidRegistry(NewTempPath());
        var entry = Entry("stubborn", 100);
        registry.Add(entry);
        var failure = new InvalidOperationException("access denied");

        var result = PtyOrphanReconciler.KillOrphan(
            entry,
            registry,
            new PtyOrphanProbes(
                IsAlive: _ => true,
                GetStartTimeUtc: _ => Start,
                KillTree: _ => throw failure));

        Assert.Equal(PtyOrphanActionOutcome.KillFailed, result.Outcome);
        Assert.Same(failure, result.Failure);
        Assert.Single(registry.LoadAll());
    }

    [Fact]
    public void KillOrphan_KillThrowsButProcessIsGone_IsAlreadyGone()
    {
        // Benign race: it exited between the identity check and the kill.
        var alive = true;
        var result = PtyOrphanReconciler.KillOrphan(
            Entry("racer", 100),
            registry: null,
            new PtyOrphanProbes(
                IsAlive: _ => alive,
                GetStartTimeUtc: _ => Start,
                KillTree: _ =>
                {
                    alive = false;
                    throw new InvalidOperationException("process has exited");
                }));

        Assert.Equal(PtyOrphanActionOutcome.AlreadyGone, result.Outcome);
    }

    [Fact]
    public void KillOrphan_KillSucceedsButProcessStillObservable_ReportsKillFailed()
    {
        var result = PtyOrphanReconciler.KillOrphan(
            Entry("immortal", 100),
            registry: null,
            Probes(new Dictionary<int, DateTime?> { [100] = Start }));

        Assert.Equal(PtyOrphanActionOutcome.KillFailed, result.Outcome);
    }

    [Fact]
    public void AdoptAsDetached_LeavesProcessAlone_AndDropsRegistryEntry()
    {
        var registry = new PtyPidRegistry(NewTempPath());
        var entry = Entry("live", 100);
        registry.Add(entry);
        registry.Add(Entry("other", 200));

        var result = PtyOrphanReconciler.AdoptAsDetached(entry, registry);

        Assert.Equal(PtyOrphanActionOutcome.Detached, result.Outcome);
        Assert.Equal(new[] { "other" }, registry.LoadAll().Select(e => e.SessionId));
    }

    [Fact]
    public void AdoptAsDetached_WithoutRegistry_DoesNotThrow()
    {
        var result = PtyOrphanReconciler.AdoptAsDetached(Entry("live", 100));

        Assert.Equal(PtyOrphanActionOutcome.Detached, result.Outcome);
    }

    // --- real OS probes (no process spawning: this process and an impossible pid) --------------------------

    [Fact]
    public void RealProbes_ThisProcess_IsAliveWithAReadableStartTime()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();

        Assert.True(PtyOrphanReconciler.IsProcessAlive(self.Id));
        var start = PtyOrphanReconciler.GetProcessStartTimeUtc(self.Id);
        Assert.NotNull(start);
        Assert.True((start!.Value - self.StartTime.ToUniversalTime()).Duration() < TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RealProbes_InvalidPid_IsNotAliveAndHasNoStartTime(int pid)
    {
        Assert.False(PtyOrphanReconciler.IsProcessAlive(pid));
        Assert.Null(PtyOrphanReconciler.GetProcessStartTimeUtc(pid));
    }

    [Fact]
    public void RealProbes_ThisProcessRecordedWithAWrongStartTime_ClassifiesAsStale()
    {
        // End-to-end over the real probes, without spawning anything: a genuinely live pid plus a wrong start
        // time is the exact shape of a recycled pid, and it must never come back as adoptable.
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var entry = Entry("recycled", self.Id, self.StartTime.ToUniversalTime().AddHours(-1));

        var report = PtyOrphanReconciler.Classify(new[] { entry }, PtyOrphanProbes.Real);

        Assert.Equal(new[] { entry }, report.Stale);
        Assert.Empty(report.Adoptable);
    }
}

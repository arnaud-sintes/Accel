using Glaude.Orchestration;
using Xunit;

namespace Glaude.Tests;

/// <summary>
/// P2-T7: unit coverage for <see cref="PtyPidRegistry"/> - round-trip persistence (reusing
/// <see cref="Glaude.Settings.SettingsFile"/>'s atomic writer, not a second mechanism),
/// tolerance of a missing/malformed file, and the pure <see cref="PtyPidRegistry.Reconcile"/>
/// staleness check (fake isProcessAlive/getProcessStartTimeUtc, no real process spawning).
/// </summary>
public class PtyPidRegistryTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch { /* best effort cleanup */ }
            try { File.Delete(path + Glaude.Settings.SettingsFile.BackupSuffix); } catch { /* best effort cleanup */ }
        }
    }

    private string NewTempPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"glaude-sessions-test-{Guid.NewGuid():N}.json");
        _tempFiles.Add(path);
        return path;
    }

    private static PtyPidEntry MakeEntry(string sessionId = "session-1", int pid = 1234) =>
        new(
            SessionId: sessionId,
            Pid: pid,
            ProcessStartTimeUtc: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            Cwd: @"C:\projects\Glaude",
            LaunchedAtUtc: new DateTime(2026, 1, 1, 11, 59, 0, DateTimeKind.Utc));

    [Fact]
    public void LoadAll_MissingFile_ReturnsEmpty()
    {
        var registry = new PtyPidRegistry(NewTempPath());

        var result = registry.LoadAll();

        Assert.Empty(result);
    }

    [Fact]
    public void LoadAll_MalformedFile_ReturnsEmpty()
    {
        string path = NewTempPath();
        File.WriteAllText(path, "{ not valid json at all");
        var registry = new PtyPidRegistry(path);

        var result = registry.LoadAll();

        Assert.Empty(result);
    }

    [Fact]
    public void LoadAll_NonArrayRoot_ReturnsEmpty()
    {
        string path = NewTempPath();
        File.WriteAllText(path, "{\"sessionId\":\"x\"}");
        var registry = new PtyPidRegistry(path);

        var result = registry.LoadAll();

        Assert.Empty(result);
    }

    [Fact]
    public void LoadAll_EmptyFile_ReturnsEmpty()
    {
        string path = NewTempPath();
        File.WriteAllText(path, string.Empty);
        var registry = new PtyPidRegistry(path);

        var result = registry.LoadAll();

        Assert.Empty(result);
    }

    [Fact]
    public void AddThenLoadAll_RoundTripsEntry()
    {
        string path = NewTempPath();
        var registry = new PtyPidRegistry(path);
        var entry = MakeEntry();

        registry.Add(entry);
        var result = registry.LoadAll();

        Assert.Single(result);
        Assert.Equal(entry.SessionId, result[0].SessionId);
        Assert.Equal(entry.Pid, result[0].Pid);
        Assert.Equal(entry.ProcessStartTimeUtc, result[0].ProcessStartTimeUtc);
        Assert.Equal(entry.Cwd, result[0].Cwd);
        Assert.Equal(entry.LaunchedAtUtc, result[0].LaunchedAtUtc);
    }

    [Fact]
    public void Add_ReplacesExistingEntryForSameSessionId()
    {
        string path = NewTempPath();
        var registry = new PtyPidRegistry(path);

        registry.Add(MakeEntry(sessionId: "session-1", pid: 111));
        registry.Add(MakeEntry(sessionId: "session-1", pid: 222));

        var result = registry.LoadAll();

        Assert.Single(result);
        Assert.Equal(222, result[0].Pid);
    }

    [Fact]
    public void Add_MultipleSessions_AllPersist()
    {
        string path = NewTempPath();
        var registry = new PtyPidRegistry(path);

        registry.Add(MakeEntry(sessionId: "session-1"));
        registry.Add(MakeEntry(sessionId: "session-2"));

        var result = registry.LoadAll();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Remove_RemovesOnlyMatchingSessionId()
    {
        string path = NewTempPath();
        var registry = new PtyPidRegistry(path);
        registry.Add(MakeEntry(sessionId: "session-1"));
        registry.Add(MakeEntry(sessionId: "session-2"));

        registry.Remove("session-1");
        var result = registry.LoadAll();

        Assert.Single(result);
        Assert.Equal("session-2", result[0].SessionId);
    }

    [Fact]
    public void Remove_NonExistentSessionId_NoThrow_LeavesRegistryUnchanged()
    {
        string path = NewTempPath();
        var registry = new PtyPidRegistry(path);
        registry.Add(MakeEntry(sessionId: "session-1"));

        var ex = Record.Exception(() => registry.Remove("does-not-exist"));

        Assert.Null(ex);
        Assert.Single(registry.LoadAll());
    }

    [Fact]
    public void Remove_OnMissingFile_DoesNotThrow()
    {
        var registry = new PtyPidRegistry(NewTempPath());

        var ex = Record.Exception(() => registry.Remove("session-1"));

        Assert.Null(ex);
    }

    // --- Reconcile ---

    [Fact]
    public void Reconcile_DeadPid_IsStale()
    {
        var entry = MakeEntry(pid: 999);

        var stale = PtyPidRegistry.Reconcile(
            new[] { entry },
            isProcessAlive: _ => false,
            getProcessStartTimeUtc: _ => null);

        Assert.Equal(new[] { entry }, stale);
    }

    [Fact]
    public void Reconcile_LivePidMismatchedStartTime_IsStale()
    {
        var entry = MakeEntry(pid: 999);
        var differentStart = entry.ProcessStartTimeUtc.AddHours(1);

        var stale = PtyPidRegistry.Reconcile(
            new[] { entry },
            isProcessAlive: pid => pid == 999,
            getProcessStartTimeUtc: pid => pid == 999 ? differentStart : null);

        Assert.Equal(new[] { entry }, stale);
    }

    [Fact]
    public void Reconcile_LivePidMatchingStartTime_IsNotStale()
    {
        var entry = MakeEntry(pid: 999);

        var stale = PtyPidRegistry.Reconcile(
            new[] { entry },
            isProcessAlive: pid => pid == 999,
            getProcessStartTimeUtc: pid => pid == 999 ? entry.ProcessStartTimeUtc : null);

        Assert.Empty(stale);
    }

    [Fact]
    public void Reconcile_LivePidUnknownStartTime_IsStale()
    {
        var entry = MakeEntry(pid: 999);

        var stale = PtyPidRegistry.Reconcile(
            new[] { entry },
            isProcessAlive: pid => pid == 999,
            getProcessStartTimeUtc: _ => null);

        Assert.Equal(new[] { entry }, stale);
    }

    [Fact]
    public void Reconcile_MixedEntries_ReturnsOnlyStaleOnes()
    {
        var liveMatching = MakeEntry(sessionId: "live", pid: 1);
        var deadEntry = MakeEntry(sessionId: "dead", pid: 2);
        var reusedPid = MakeEntry(sessionId: "reused", pid: 3);

        var stale = PtyPidRegistry.Reconcile(
            new[] { liveMatching, deadEntry, reusedPid },
            isProcessAlive: pid => pid == 1 || pid == 3,
            getProcessStartTimeUtc: pid => pid switch
            {
                1 => liveMatching.ProcessStartTimeUtc,
                3 => reusedPid.ProcessStartTimeUtc.AddDays(1),
                _ => null,
            });

        Assert.Equal(2, stale.Count);
        Assert.Contains(deadEntry, stale);
        Assert.Contains(reusedPid, stale);
        Assert.DoesNotContain(liveMatching, stale);
    }
}

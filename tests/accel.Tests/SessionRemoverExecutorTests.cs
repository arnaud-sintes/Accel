namespace Accel.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text;
using Accel.Orchestration;
using Xunit;

/// <summary>
/// P4-T3b: unit tests for <see cref="SessionRemoverExecutor.Execute"/> - the dangerous half. Every test
/// builds a fixture <c>.claude</c> tree under a fresh temp directory; <b>none of these ever touch the
/// real user profile</b>. <see cref="SessionRemovalMode.PermanentDelete"/> is used for most assertions so
/// tests don't have to reach into the real Windows recycle bin to verify anything; a small, dedicated
/// group at the bottom exercises the real <see cref="SessionRemovalMode.RecycleBin"/> path end-to-end
/// (against fixture files only) to prove the Win32 marshalling actually works.
/// </summary>
public class SessionRemoverExecutorTests : IDisposable
{
    private readonly string _homeDir;
    private readonly string _claudeHome;

    public SessionRemoverExecutorTests()
    {
        _homeDir = Path.Combine(Path.GetTempPath(), "accel-session-remover-exec-test-" + Guid.NewGuid());
        _claudeHome = Path.Combine(_homeDir, ".claude");
        Directory.CreateDirectory(_claudeHome);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_homeDir, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private string WriteFixtureFile(string relativePath, string content)
    {
        string fullPath = Path.Combine(_claudeHome, Path.Combine(relativePath.Split('/')));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return fullPath;
    }

    private string CreateFixtureDir(string relativePath)
    {
        string fullPath = Path.Combine(_claudeHome, Path.Combine(relativePath.Split('/')));
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    private void PopulateFullFixture(string sessionId, string slug, out string transcriptPath, out string historyPath)
    {
        CreateFixtureDir($"file-history/{sessionId}");
        WriteFixtureFile($"file-history/{sessionId}/a.txt", "a");
        CreateFixtureDir($"tasks/{sessionId}");
        WriteFixtureFile($"tasks/{sessionId}/b.txt", "b");
        CreateFixtureDir($"session-env/{sessionId}");
        WriteFixtureFile($"session-env/{sessionId}/c.txt", "c");
        CreateFixtureDir($"projects/{slug}/{sessionId}");
        WriteFixtureFile($"projects/{slug}/{sessionId}/agent.jsonl", "agent-data");
        transcriptPath = WriteFixtureFile($"projects/{slug}/{sessionId}.jsonl", "transcript-data");
        historyPath = WriteFixtureFile("history.jsonl",
            $"{{\"sessionId\":\"{sessionId}\",\"display\":\"x\"}}\n" +
            "{\"sessionId\":\"unrelated\",\"display\":\"y\"}\n");
    }

    // ---------------------------------------------------------------------------------------------
    // Guard: never executes an unsafe plan.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Execute_ThrowsImmediately_ForAnUnsafePlan()
    {
        var unsafePlan = SessionRemover.Plan("not-a-guid", "C--projects", _homeDir);
        Assert.False(unsafePlan.IsSafe);

        Assert.Throws<ArgumentException>(() => SessionRemoverExecutor.Execute(unsafePlan, isSessionLive: () => false, homeDirOverride: _homeDir));
    }

    // ---------------------------------------------------------------------------------------------
    // Happy path: permanent delete of a fully populated fixture, everything gone, transcript last.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Execute_PermanentDelete_RemovesEveryTargetAndRewritesHistory()
    {
        string sessionId = "11111111-1111-1111-1111-111111111111";
        const string slug = "C--projects";
        PopulateFullFixture(sessionId, slug, out string transcriptPath, out string historyPath);

        var plan = SessionRemover.Plan(sessionId, slug, _homeDir);
        Assert.True(plan.IsSafe);

        var result = SessionRemoverExecutor.Execute(plan, mode: SessionRemovalMode.PermanentDelete, isSessionLive: () => false, homeDirOverride: _homeDir);

        Assert.True(result.FullyRemoved);
        Assert.False(result.AbortedForLiveness);
        Assert.All(result.Steps, s => Assert.Equal(SessionRemovalStepOutcome.Removed, s.Outcome));

        // Transcript really was the last step, and every location is actually gone from disk.
        Assert.Equal(transcriptPath, result.Steps[^1].Path);
        Assert.False(File.Exists(transcriptPath));
        Assert.False(Directory.Exists(Path.Combine(_claudeHome, "file-history", sessionId)));
        Assert.False(Directory.Exists(Path.Combine(_claudeHome, "tasks", sessionId)));
        Assert.False(Directory.Exists(Path.Combine(_claudeHome, "session-env", sessionId)));
        Assert.False(Directory.Exists(Path.Combine(_claudeHome, "projects", slug, sessionId)));

        // history.jsonl still exists, still has the unrelated line, no longer has this session's line.
        Assert.True(File.Exists(historyPath));
        string remaining = File.ReadAllText(historyPath);
        Assert.DoesNotContain(sessionId, remaining);
        Assert.Contains("unrelated", remaining);
        Assert.True(result.HistoryRewriteAttempted);
        Assert.True(result.HistoryRewriteSucceeded);
        Assert.Equal(1, result.HistoryLinesRemoved);
    }

    [Fact]
    public void Execute_NothingOnDisk_EveryStepIsNotPresent_AndNothingThrows()
    {
        string sessionId = "22222222-2222-2222-2222-222222222222";
        var plan = SessionRemover.Plan(sessionId, "C--projects", _homeDir);

        var result = SessionRemoverExecutor.Execute(plan, mode: SessionRemovalMode.PermanentDelete, isSessionLive: () => false, homeDirOverride: _homeDir);

        Assert.True(result.FullyRemoved);
        Assert.All(result.Steps, s => Assert.Equal(SessionRemovalStepOutcome.NotPresent, s.Outcome));
        Assert.False(result.HistoryRewriteAttempted); // history.jsonl didn't exist either
    }

    // ---------------------------------------------------------------------------------------------
    // Liveness gate: the defining safety property of the executor.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Execute_SessionLiveFromTheStart_SkipsEveryTargetAndNeverTouchesDisk()
    {
        string sessionId = "33333333-3333-3333-3333-333333333333";
        const string slug = "C--projects";
        PopulateFullFixture(sessionId, slug, out string transcriptPath, out string historyPath);

        var plan = SessionRemover.Plan(sessionId, slug, _homeDir);
        var result = SessionRemoverExecutor.Execute(plan, mode: SessionRemovalMode.PermanentDelete, isSessionLive: () => true, homeDirOverride: _homeDir);

        Assert.True(result.AbortedForLiveness);
        Assert.False(result.FullyRemoved);
        Assert.All(result.Steps, s => Assert.Equal(SessionRemovalStepOutcome.Skipped, s.Outcome));
        Assert.False(result.HistoryRewriteAttempted);

        // Nothing was actually deleted.
        Assert.True(File.Exists(transcriptPath));
        Assert.True(File.Exists(historyPath));
        Assert.True(Directory.Exists(Path.Combine(_claudeHome, "tasks", sessionId)));
    }

    [Fact]
    public void Execute_SessionBecomesLiveMidway_StopsAndSkipsEverythingAfter()
    {
        string sessionId = "44444444-4444-4444-4444-444444444444";
        const string slug = "C--projects";
        PopulateFullFixture(sessionId, slug, out string transcriptPath, out _);

        var plan = SessionRemover.Plan(sessionId, slug, _homeDir);
        Assert.True(plan.Targets.Count >= 3);

        int callCount = 0;
        bool ReportLiveAfterFirstTarget()
        {
            callCount++;
            return callCount > 1; // live from the second liveness check onward
        }

        var result = SessionRemoverExecutor.Execute(plan, mode: SessionRemovalMode.PermanentDelete, isSessionLive: ReportLiveAfterFirstTarget, homeDirOverride: _homeDir);

        Assert.True(result.AbortedForLiveness);
        Assert.Equal(SessionRemovalStepOutcome.Removed, result.Steps[0].Outcome);
        Assert.All(result.Steps.Skip(1), s => Assert.Equal(SessionRemovalStepOutcome.Skipped, s.Outcome));

        // The transcript (last target) must never have been touched once the run aborted.
        Assert.True(File.Exists(transcriptPath));
    }

    /// <summary>
    /// Regression test for a CONFIRMED finding from the P4-T3c adversarial review: a target delete that
    /// throws (a locked file, a permissions error) did not previously abort the run, so the loop fell
    /// through to still delete the transcript last as if nothing had gone wrong - verified by the review
    /// to leave the transcript gone while an earlier location partially survived, exactly the "half gone
    /// but no longer discoverable" state the transcript-last ordering exists to prevent. This test forces
    /// a real delete failure (a file held open with no sharing) on the very first target and proves the
    /// transcript step is now skipped, not removed.
    /// </summary>
    [Fact]
    public void Execute_AFailedDelete_AbortsTheRun_AndNeverReachesTheTranscript()
    {
        string sessionId = "99999999-8888-7777-6666-555555555555";
        const string slug = "C--projects";
        PopulateFullFixture(sessionId, slug, out string transcriptPath, out _);

        string lockedFile = Path.Combine(_claudeHome, "file-history", sessionId, "a.txt");
        using var lockHandle = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None);

        var plan = SessionRemover.Plan(sessionId, slug, _homeDir);
        var firstTarget = plan.Targets[0];
        Assert.Equal("File history", firstTarget.Description); // sanity: this is the target we locked

        var result = SessionRemoverExecutor.Execute(plan, mode: SessionRemovalMode.PermanentDelete, isSessionLive: () => false, homeDirOverride: _homeDir);

        Assert.Equal(SessionRemovalAbortReason.TargetDeleteFailed, result.AbortReason);
        Assert.False(result.FullyRemoved);
        Assert.Equal(SessionRemovalStepOutcome.Failed, result.Steps[0].Outcome);
        Assert.All(result.Steps.Skip(1), s => Assert.Equal(SessionRemovalStepOutcome.Skipped, s.Outcome));
        Assert.False(result.HistoryRewriteAttempted);

        // The transcript must still be there - the whole point of the ordering.
        Assert.True(File.Exists(transcriptPath));
    }

    // ---------------------------------------------------------------------------------------------
    // Re-validation: the executor never trusts a plan blindly, even one that claimed IsSafe=true.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Execute_RefusesATamperedTargetPath_EvenInsideAPlanThatClaimedSafe()
    {
        string sessionId = "55555555-5555-5555-5555-555555555555";
        const string slug = "C--projects";
        PopulateFullFixture(sessionId, slug, out string transcriptPath, out _);

        var plan = SessionRemover.Plan(sessionId, slug, _homeDir);
        Assert.True(plan.IsSafe);

        // Simulate a tampered/corrupted plan in memory: swap one target's path for something outside
        // .claude entirely, while leaving IsSafe (a get-only computed flag on the record as constructed)
        // still true - exactly the scenario the executor's own re-validation exists to catch.
        var tamperedTargets = plan.Targets.ToArray();
        string outsidePath = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid());
        Directory.CreateDirectory(outsidePath);
        File.WriteAllText(Path.Combine(outsidePath, "victim.txt"), "must never be deleted");
        tamperedTargets[0] = tamperedTargets[0] with { Path = outsidePath };
        var tamperedPlan = plan with { Targets = tamperedTargets };

        try
        {
            var result = SessionRemoverExecutor.Execute(tamperedPlan, mode: SessionRemovalMode.PermanentDelete, isSessionLive: () => false, homeDirOverride: _homeDir);

            Assert.True(result.AbortedForLiveness == false); // aborted for re-validation, not liveness
            Assert.Equal(SessionRemovalStepOutcome.Skipped, result.Steps[0].Outcome);
            Assert.Contains("re-validation failed", result.Steps[0].Detail, StringComparison.OrdinalIgnoreCase);
            Assert.All(result.Steps.Skip(1), s => Assert.Equal(SessionRemovalStepOutcome.Skipped, s.Outcome));

            // The tampered-in outside path was never touched, and neither was the real transcript.
            Assert.True(Directory.Exists(outsidePath));
            Assert.True(File.Exists(Path.Combine(outsidePath, "victim.txt")));
            Assert.True(File.Exists(transcriptPath));
        }
        finally
        {
            Directory.Delete(outsidePath, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // history.jsonl rewrite: atomic, tolerant of a partial last line, never touches unrelated lines.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Execute_HistoryRewrite_PreservesAPartialConcurrentlyAppendedLastLine()
    {
        string sessionId = "66666666-6666-6666-6666-666666666666";
        const string slug = "C--projects";
        WriteFixtureFile("history.jsonl",
            $"{{\"sessionId\":\"{sessionId}\",\"display\":\"a\"}}\n" +
            "{\"sessionId\":\"other-session\",\"display\":\"b\"}\n" +
            "{\"sessionId\":\"trunc"); // partial last line, as if caught mid-append

        var plan = SessionRemover.Plan(sessionId, slug, _homeDir);
        var result = SessionRemoverExecutor.Execute(plan, mode: SessionRemovalMode.PermanentDelete, isSessionLive: () => false, homeDirOverride: _homeDir);

        Assert.True(result.HistoryRewriteSucceeded);
        Assert.Equal(1, result.HistoryLinesRemoved);

        string[] remainingLines = File.ReadAllLines(Path.Combine(_claudeHome, "history.jsonl"));
        Assert.Equal(2, remainingLines.Length);
        Assert.Contains("other-session", remainingLines[0]);
        Assert.Equal("{\"sessionId\":\"trunc", remainingLines[1]); // preserved verbatim, never dropped
    }

    [Fact]
    public void Execute_HistoryRewrite_NoTempFileLeftBehindOnSuccess()
    {
        string sessionId = "77777777-7777-7777-7777-777777777777";
        WriteFixtureFile("history.jsonl", $"{{\"sessionId\":\"{sessionId}\"}}\n");

        var plan = SessionRemover.Plan(sessionId, "C--projects", _homeDir);
        SessionRemoverExecutor.Execute(plan, mode: SessionRemovalMode.PermanentDelete, isSessionLive: () => false, homeDirOverride: _homeDir);

        var leftoverTempFiles = Directory.GetFiles(_claudeHome, "*.tmp", SearchOption.TopDirectoryOnly);
        Assert.Empty(leftoverTempFiles);
    }

    // ---------------------------------------------------------------------------------------------
    // Real recycle-bin path (fixture files only) - proves the Win32 marshalling actually works.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Execute_RecycleBinMode_ActuallyRemovesTheFixtureFileFromItsOriginalLocation()
    {
        string sessionId = "88888888-8888-8888-8888-888888888888";
        const string slug = "C--projects";
        CreateFixtureDir($"tasks/{sessionId}");
        string fixtureFile = WriteFixtureFile($"tasks/{sessionId}/only.txt", "recycle me");

        var plan = SessionRemover.Plan(sessionId, slug, _homeDir);
        var target = plan.Targets.Single(t => t.Description == "Tasks");
        Assert.True(target.Exists);

        var result = SessionRemoverExecutor.Execute(plan, mode: SessionRemovalMode.RecycleBin, isSessionLive: () => false, homeDirOverride: _homeDir);

        var taskStep = result.Steps.Single(s => s.Description == "Tasks");
        Assert.Equal(SessionRemovalStepOutcome.Removed, taskStep.Outcome);
        Assert.False(Directory.Exists(Path.Combine(_claudeHome, "tasks", sessionId)));
        Assert.False(File.Exists(fixtureFile));
    }
}

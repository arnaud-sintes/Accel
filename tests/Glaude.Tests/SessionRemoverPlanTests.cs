namespace Glaude.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text;
using Glaude.Orchestration;
using Xunit;

/// <summary>
/// P4-T3: unit tests for <see cref="SessionRemover.Plan"/> - the planner half only. Every test builds a
/// fixture <c>.claude</c> tree under a fresh temp directory and passes it via <c>homeDirOverride</c>;
/// <b>none of these ever touch the real user profile</b>, and this file must never call the executor
/// (P4-T3b) at all - see the plan's own audit note for why planning and executing are separate, separately
/// reviewable units.
/// </summary>
public class SessionRemoverPlanTests : IDisposable
{
    private readonly string _homeDir;
    private readonly string _claudeHome;

    public SessionRemoverPlanTests()
    {
        _homeDir = Path.Combine(Path.GetTempPath(), "glaude-session-remover-test-" + Guid.NewGuid());
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
            // Best effort - a stray handle on Windows must not fail the test run.
        }
    }

    private string WriteFixtureFile(string relativePath, string content)
    {
        string fullPath = Path.Combine(_claudeHome, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return fullPath;
    }

    private string CreateFixtureDir(string relativePath)
    {
        string fullPath = Path.Combine(_claudeHome, relativePath);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    // ---------------------------------------------------------------------------------------------
    // The six-location enumeration and per-file byte counting, against a fully populated fixture.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Plan_FindsAllSixLocations_WhenEveryOneExists()
    {
        string sessionId = "11111111-1111-1111-1111-111111111111";
        const string slug = "C--projects";

        CreateFixtureDir($"file-history/{sessionId}");
        WriteFixtureFile($"file-history/{sessionId}/a.txt", "aaaa");
        CreateFixtureDir($"tasks/{sessionId}");
        WriteFixtureFile($"tasks/{sessionId}/b.txt", "bb");
        CreateFixtureDir($"session-env/{sessionId}");
        WriteFixtureFile($"session-env/{sessionId}/c.txt", "c");
        CreateFixtureDir($"projects/{slug}/{sessionId}");
        WriteFixtureFile($"projects/{slug}/{sessionId}/agent1.jsonl", "1234567890");
        WriteFixtureFile($"projects/{slug}/{sessionId}.jsonl", "transcript-content");
        WriteFixtureFile("history.jsonl",
            $"{{\"sessionId\":\"{sessionId}\",\"display\":\"x\"}}\n" +
            "{\"sessionId\":\"unrelated\",\"display\":\"y\"}\n");

        var plan = SessionRemover.Plan(sessionId, slug, _homeDir);

        Assert.True(plan.IsSafe);
        Assert.Empty(plan.Warnings);
        Assert.Equal(5, plan.Targets.Count); // 4 directories + 1 transcript file
        Assert.All(plan.Targets, t => Assert.True(t.Exists));
        Assert.True(plan.HistoryFileExists);
        Assert.Equal(1, plan.HistoryLinesToRemove);
        Assert.True(plan.TotalBytes > 0);

        // Transcript is strictly last.
        Assert.Equal(SessionRemovalTargetKind.TranscriptFile, plan.Targets[^1].Kind);
        Assert.All(plan.Targets.Take(plan.Targets.Count - 1), t => Assert.Equal(SessionRemovalTargetKind.Directory, t.Kind));
    }

    [Fact]
    public void Plan_MarksEveryTargetAsNotExisting_WhenNothingIsOnDiskYet()
    {
        string sessionId = "22222222-2222-2222-2222-222222222222";
        var plan = SessionRemover.Plan(sessionId, "C--projects", _homeDir);

        Assert.True(plan.IsSafe);
        Assert.Equal(5, plan.Targets.Count);
        Assert.All(plan.Targets, t => Assert.False(t.Exists));
        Assert.Equal(0, plan.TotalBytes);
        Assert.False(plan.HistoryFileExists);
        Assert.Equal(0, plan.HistoryLinesToRemove);
        Assert.Empty(plan.ExistingTargets);
    }

    [Fact]
    public void Plan_PartialFixture_OnlyThoseTargetsReportExists()
    {
        string sessionId = "33333333-3333-3333-3333-333333333333";
        const string slug = "C--projects";
        CreateFixtureDir($"tasks/{sessionId}");
        WriteFixtureFile($"tasks/{sessionId}/only.txt", "hello");

        var plan = SessionRemover.Plan(sessionId, slug, _homeDir);

        Assert.True(plan.IsSafe);
        var existing = plan.ExistingTargets.ToList();
        Assert.Single(existing);
        Assert.Equal("Tasks", existing[0].Description);
        Assert.Equal(5, plan.TotalBytes);
    }

    // ---------------------------------------------------------------------------------------------
    // Safety: malformed session id / project dir.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("11111111111111111111111111111111")] // no dashes
    [InlineData("../../etc/passwd")]
    public void Plan_RejectsMalformedSessionId(string badId)
    {
        var plan = SessionRemover.Plan(badId, "C--projects", _homeDir);

        Assert.False(plan.IsSafe);
        Assert.Empty(plan.Targets);
        Assert.NotEmpty(plan.Warnings);
    }

    [Fact]
    public void Plan_RejectsBlankProjectDir()
    {
        var plan = SessionRemover.Plan("11111111-1111-1111-1111-111111111111", "   ", _homeDir);

        Assert.False(plan.IsSafe);
        Assert.Empty(plan.Targets);
        Assert.NotEmpty(plan.Warnings);
    }

    // ---------------------------------------------------------------------------------------------
    // Safety: path-escape attempts via a hostile projectDir.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("../..")]
    [InlineData("../../..")]
    [InlineData("..\\..")]
    public void Plan_RejectsProjectDirThatEscapesClaudeHome(string hostileProjectDir)
    {
        string sessionId = "44444444-4444-4444-4444-444444444444";

        var plan = SessionRemover.Plan(sessionId, hostileProjectDir, _homeDir);

        Assert.False(plan.IsSafe);
        Assert.NotEmpty(plan.Warnings);
        Assert.DoesNotContain(plan.Targets, t => t.Kind == SessionRemovalTargetKind.TranscriptFile);
    }

    [Fact]
    public void Plan_RejectsAbsolutePathProjectDirEscapingHome()
    {
        string sessionId = "55555555-5555-5555-5555-555555555555";
        string outsideDir = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid());

        var plan = SessionRemover.Plan(sessionId, outsideDir, _homeDir);

        Assert.False(plan.IsSafe);
        Assert.DoesNotContain(plan.Targets, t => t.Kind == SessionRemovalTargetKind.TranscriptFile);
    }

    // ---------------------------------------------------------------------------------------------
    // Safety: reparse-point rejection (junction masquerading as one of the four GUID directories).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Plan_RejectsATargetDirectoryThatIsActuallyAJunction()
    {
        string sessionId = "66666666-6666-6666-6666-666666666666";
        string outsideTarget = Path.Combine(Path.GetTempPath(), "junction-target-" + Guid.NewGuid());
        Directory.CreateDirectory(outsideTarget);
        File.WriteAllText(Path.Combine(outsideTarget, "secret.txt"), "should never be touched");

        string junctionPath = Path.Combine(_claudeHome, "tasks", sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(junctionPath)!);

        // mklink /J - creating a real junction is the only way to prove the reparse-point check fires
        // against the real filesystem, not just against a mocked attribute.
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{outsideTarget}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(5000);

        try
        {
            Assert.True(Directory.Exists(junctionPath), "test setup failed to create the junction - mklink /J did not succeed");

            var plan = SessionRemover.Plan(sessionId, "C--projects", _homeDir);

            Assert.False(plan.IsSafe);
            Assert.Contains(plan.Warnings, w => w.Contains("reparse point", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(plan.Targets, t => t.Path == junctionPath);
        }
        finally
        {
            try { Directory.Delete(junctionPath); } catch { /* best effort cleanup */ }
            try { Directory.Delete(outsideTarget, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // ValidateTarget - the internal gate, exercised directly for the cases that don't need a full plan.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ValidateTarget_RejectsALeafThatIsAGuidButNotTheExpectedOne()
    {
        string expected = "77777777-7777-7777-7777-777777777777";
        string other = "88888888-8888-8888-8888-888888888888";
        string path = Path.Combine(_claudeHome, "tasks", other);

        var result = SessionRemover.ValidateTarget(path, _claudeHome, expected);

        Assert.False(result.IsValid);
        Assert.Contains("leaf", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateTarget_AcceptsTheTranscriptFileLeaf_StrippingTheJsonlExtension()
    {
        string sessionId = "99999999-9999-9999-9999-999999999999";
        string path = Path.Combine(_claudeHome, "projects", "C--projects", sessionId + ".jsonl");

        var result = SessionRemover.ValidateTarget(path, _claudeHome, sessionId);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateTarget_IsCaseInsensitiveForTheGuidLeaf()
    {
        string sessionId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        string upper = sessionId.ToUpperInvariant();
        string path = Path.Combine(_claudeHome, "tasks", upper);

        var result = SessionRemover.ValidateTarget(path, _claudeHome, sessionId);

        Assert.True(result.IsValid);
    }

    // ---------------------------------------------------------------------------------------------
    // history.jsonl matching - tolerant of malformed/partial lines.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void CountMatchingHistoryLines_CountsOnlyLinesForThisSession()
    {
        string sessionId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        string historyPath = WriteFixtureFile("history.jsonl",
            $"{{\"sessionId\":\"{sessionId}\",\"display\":\"a\"}}\n" +
            $"{{\"sessionId\":\"{sessionId}\",\"display\":\"b\"}}\n" +
            "{\"sessionId\":\"other-session\",\"display\":\"c\"}\n");

        int count = SessionRemover.CountMatchingHistoryLines(historyPath, sessionId);

        Assert.Equal(2, count);
    }

    [Fact]
    public void CountMatchingHistoryLines_TolerantOfAMalformedOrPartialLastLine()
    {
        string sessionId = "cccccccc-cccc-cccc-cccc-cccccccccccc";
        string historyPath = WriteFixtureFile("history.jsonl",
            $"{{\"sessionId\":\"{sessionId}\",\"display\":\"a\"}}\n" +
            "{\"sessionId\":\"cccccccc-cccc-cccc-cccc-ccccccccc"); // truncated mid-write, no closing brace

        int count = SessionRemover.CountMatchingHistoryLines(historyPath, sessionId);

        Assert.Equal(1, count);
    }

    [Fact]
    public void LineMatchesSession_IsCaseInsensitive()
    {
        string sessionId = "dddddddd-dddd-dddd-dddd-dddddddddddd";
        string line = $"{{\"sessionId\":\"{sessionId.ToUpperInvariant()}\"}}";

        Assert.True(SessionRemover.LineMatchesSession(line, sessionId));
    }

    [Fact]
    public void LineMatchesSession_ReturnsFalse_ForBlankOrNonObjectLines()
    {
        Assert.False(SessionRemover.LineMatchesSession("", "any"));
        Assert.False(SessionRemover.LineMatchesSession("[]", "any"));
        Assert.False(SessionRemover.LineMatchesSession("\"just a string\"", "any"));
    }

    // ---------------------------------------------------------------------------------------------
    // Do-not-touch: nothing here ever produces a target under shell-snapshots/paste-cache/.claude.json.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Plan_NeverProducesATargetUnderAnyDoNotTouchLocation()
    {
        string sessionId = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";
        var plan = SessionRemover.Plan(sessionId, "C--projects", _homeDir);

        foreach (var target in plan.Targets)
        {
            Assert.DoesNotContain("shell-snapshots", target.Path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("paste-cache", target.Path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".claude.json", target.Path, StringComparison.OrdinalIgnoreCase);
        }
    }
}

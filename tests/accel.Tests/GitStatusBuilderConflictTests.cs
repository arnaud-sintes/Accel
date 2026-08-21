namespace Accel.Tests;

using System;
using System.IO;
using System.Linq;
using Accel.Cli;
using Xunit;

/// <summary>
/// Unit tests for the unmerged-path half of <see cref="GitStatusBuilder"/> - real temp repositories
/// driven by the real `git` executable (same convention as <see cref="GitActionsServiceTests"/>),
/// since a conflict is not something porcelain output can be usefully faked into.
/// </summary>
public sealed class GitStatusBuilderConflictTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "accel-git-conflict-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup only.
        }
    }

    /// <summary>The regression this whole feature starts from: a "UU" line used to be split
    /// positionally into a staged entry and an unstaged entry for the same path.</summary>
    [Fact]
    public void Build_UnmergedPath_YieldsExactlyOneEntry()
    {
        GitTestRepo.CreateMergeConflict(_root);

        var entries = GitStatusBuilder.Build(_root);

        var conflicted = Assert.Single(entries!.Where(e => e.Path == "conflict.txt"));
        Assert.True(conflicted.IsConflicted);
        Assert.False(conflicted.IsStaged);
        Assert.Equal('U', conflicted.StatusCode);
        Assert.Equal("Conflict: both modified", conflicted.StatusDescription);
    }

    [Fact]
    public void Build_CleanlyMergedPath_IsNotReportedAsConflicted()
    {
        GitTestRepo.CreateMergeConflict(_root);

        var entries = GitStatusBuilder.Build(_root);

        Assert.DoesNotContain(entries!, e => e.Path == "agreed.txt" && e.IsConflicted);
    }

    [Fact]
    public void Build_OrdinaryChangesAreNeverMarkedConflicted()
    {
        GitTestRepo.InitRepo(_root);
        File.WriteAllText(Path.Combine(_root, "tracked.txt"), "one\n");
        GitTestRepo.RunGit(_root, "add -A");
        GitTestRepo.RunGit(_root, "commit -m initial");
        File.WriteAllText(Path.Combine(_root, "tracked.txt"), "two\n");
        File.WriteAllText(Path.Combine(_root, "fresh.txt"), "new\n");

        var entries = GitStatusBuilder.Build(_root);

        Assert.All(entries!, e => Assert.False(e.IsConflicted));
    }

    [Fact]
    public void BuildSummary_DuringAConflictingMerge_ReportsMergeInProgress()
    {
        GitTestRepo.CreateMergeConflict(_root);

        var summary = GitStatusBuilder.BuildSummary(_root);

        Assert.Equal(GitInProgressOperation.Merge, summary!.InProgressOperation);
    }

    [Fact]
    public void BuildSummary_AfterAbort_ReportsNoOperation()
    {
        GitTestRepo.CreateMergeConflict(_root);
        GitTestRepo.RunGit(_root, "merge --abort");

        var summary = GitStatusBuilder.BuildSummary(_root);

        Assert.Equal(GitInProgressOperation.None, summary!.InProgressOperation);
    }

    [Fact]
    public void BuildSummary_CleanRepo_ReportsNoOperation()
    {
        GitTestRepo.InitRepo(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a\n");
        GitTestRepo.RunGit(_root, "add -A");
        GitTestRepo.RunGit(_root, "commit -m initial");

        var summary = GitStatusBuilder.BuildSummary(_root);

        Assert.Equal(GitInProgressOperation.None, summary!.InProgressOperation);
    }

    /// <summary>The three conflict stages are what the two-pane conflict view reads for its
    /// "Before" side - see <c>MainWindow.ReadGitDiffSideAsync</c>.</summary>
    [Fact]
    public void ReadGitObject_ExposesAllThreeConflictStages()
    {
        GitTestRepo.CreateMergeConflict(_root);

        Assert.Equal("base\n", GitStatusBuilder.ReadGitObject(_root, ":1:conflict.txt"));
        Assert.Equal("ours\n", GitStatusBuilder.ReadGitObject(_root, ":2:conflict.txt"));
        Assert.Equal("theirs\n", GitStatusBuilder.ReadGitObject(_root, ":3:conflict.txt"));
    }

    /// <summary>And the working-tree copy - the conflict view's editable side - is git's merged output
    /// with the markers still in it, which is what makes editing it the resolution.</summary>
    [Fact]
    public void WorkingTreeCopy_CarriesConflictMarkers()
    {
        GitTestRepo.CreateMergeConflict(_root);

        string content = File.ReadAllText(Path.Combine(_root, "conflict.txt"));

        Assert.Contains("<<<<<<<", content, StringComparison.Ordinal);
        Assert.Contains(">>>>>>>", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData('U', 'U', "Conflict: both modified")]
    [InlineData('A', 'A', "Conflict: both added")]
    [InlineData('D', 'D', "Conflict: both deleted")]
    [InlineData('A', 'U', "Conflict: added by us")]
    [InlineData('U', 'A', "Conflict: added by them")]
    [InlineData('D', 'U', "Conflict: deleted by us")]
    [InlineData('U', 'D', "Conflict: deleted by them")]
    public void UnmergedDescription_CoversEveryUnmergedPair(char index, char worktree, string expected) =>
        Assert.Equal(expected, GitStatusBuilder.UnmergedDescription(index, worktree));

    /// <summary>"AD"/"MM"/"AM" and friends look superficially similar but are ordinary
    /// index+worktree combinations - misreading one as unmerged would collapse a genuinely two-sided
    /// change into a single row.</summary>
    [Theory]
    [InlineData('M', 'M')]
    [InlineData('A', 'M')]
    [InlineData('A', 'D')]
    [InlineData('D', 'M')]
    [InlineData('R', 'M')]
    [InlineData('M', ' ')]
    [InlineData(' ', 'M')]
    public void UnmergedDescription_OrdinaryPairsAreNotUnmerged(char index, char worktree) =>
        Assert.Null(GitStatusBuilder.UnmergedDescription(index, worktree));
}

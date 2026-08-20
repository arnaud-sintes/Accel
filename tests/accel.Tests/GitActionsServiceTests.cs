namespace Accel.Tests;

using System;
using System.IO;
using System.Threading.Tasks;
using Accel.Cli;
using Xunit;

/// <summary>
/// Unit tests for <see cref="GitActionsService"/> - real temp repositories driven by the real
/// `git` executable, same convention as <see cref="GitPanelViewModelTests"/> (no process mocking).
/// </summary>
public sealed class GitActionsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "accel-git-actions-tests-" + Guid.NewGuid().ToString("N"));

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

    private string InitRepoWithCommit()
    {
        GitTestRepo.InitRepo(_root);
        File.WriteAllText(Path.Combine(_root, "committed.txt"), "original\n");
        GitTestRepo.RunGit(_root, "add committed.txt");
        GitTestRepo.RunGit(_root, "commit -m initial");
        return _root;
    }

    [Fact]
    public async Task StageAsync_MovesUntrackedFileIntoIndex()
    {
        InitRepoWithCommit();
        File.WriteAllText(Path.Combine(_root, "new.txt"), "content");

        var result = await GitActionsService.StageAsync(_root, "new.txt");

        Assert.Equal(GitActionOutcome.Success, result.Outcome);
        var entries = GitStatusBuilder.Build(_root);
        Assert.Contains(entries!, e => e.Path == "new.txt" && e.IsStaged);
    }

    [Fact]
    public async Task UnstageAsync_MovesFileBackOutOfIndex()
    {
        InitRepoWithCommit();
        File.WriteAllText(Path.Combine(_root, "new.txt"), "content");
        GitTestRepo.RunGit(_root, "add new.txt");

        var result = await GitActionsService.UnstageAsync(_root, "new.txt");

        Assert.Equal(GitActionOutcome.Success, result.Outcome);
        var entries = GitStatusBuilder.Build(_root);
        Assert.Contains(entries!, e => e.Path == "new.txt" && !e.IsStaged);
    }

    [Fact]
    public async Task StageAllAsync_StagesEveryDirtyFile()
    {
        InitRepoWithCommit();
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "b");

        var result = await GitActionsService.StageAllAsync(_root);

        Assert.Equal(GitActionOutcome.Success, result.Outcome);
        var entries = GitStatusBuilder.Build(_root);
        Assert.All(entries!, e => Assert.True(e.IsStaged));
    }

    [Fact]
    public async Task DiscardAsync_UnstagedEdit_RestoresOriginalContent()
    {
        InitRepoWithCommit();
        File.WriteAllText(Path.Combine(_root, "committed.txt"), "edited\n");

        var result = await GitActionsService.DiscardAsync(_root, "committed.txt", isStaged: false, isUntracked: false);

        Assert.Equal(GitActionOutcome.Success, result.Outcome);
        Assert.Equal("original", File.ReadAllText(Path.Combine(_root, "committed.txt")).ReplaceLineEndings("\n").TrimEnd('\n'));
    }

    [Fact]
    public async Task DiscardAsync_StagedAndModifiedFile_RestoresToHead()
    {
        InitRepoWithCommit();
        File.WriteAllText(Path.Combine(_root, "committed.txt"), "edited\n");
        GitTestRepo.RunGit(_root, "add committed.txt");

        var result = await GitActionsService.DiscardAsync(_root, "committed.txt", isStaged: true, isUntracked: false);

        Assert.Equal(GitActionOutcome.Success, result.Outcome);
        Assert.Equal("original", File.ReadAllText(Path.Combine(_root, "committed.txt")).ReplaceLineEndings("\n").TrimEnd('\n'));
        var entries = GitStatusBuilder.Build(_root);
        Assert.Empty(entries!);
    }

    [Fact]
    public async Task DiscardAsync_UntrackedFile_RemovesIt()
    {
        InitRepoWithCommit();
        string path = Path.Combine(_root, "untracked.txt");
        File.WriteAllText(path, "content");

        var result = await GitActionsService.DiscardAsync(_root, "untracked.txt", isStaged: false, isUntracked: true);

        Assert.Equal(GitActionOutcome.Success, result.Outcome);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task CommitAsync_WithStagedChanges_CreatesCommit()
    {
        InitRepoWithCommit();
        File.WriteAllText(Path.Combine(_root, "new.txt"), "content");
        GitTestRepo.RunGit(_root, "add new.txt");

        var result = await GitActionsService.CommitAsync(_root, "Add new.txt\n\nWith a body line too.");

        Assert.Equal(GitActionOutcome.Success, result.Outcome);
        string log = GitTestRepo.RunGitCapture(_root, "log -1 --pretty=%s");
        Assert.Equal("Add new.txt", log.Trim());
    }

    [Fact]
    public async Task CommitAsync_NothingStaged_ReportsNothingToDo()
    {
        InitRepoWithCommit();

        var result = await GitActionsService.CommitAsync(_root, "empty commit attempt");

        Assert.Equal(GitActionOutcome.NothingToDo, result.Outcome);
    }

    [Fact]
    public async Task ListLocalBranchesAsync_ReturnsCurrentBranch()
    {
        InitRepoWithCommit();
        string currentBranch = GitTestRepo.RunGitCapture(_root, "rev-parse --abbrev-ref HEAD").Trim();

        string[]? branches = await GitActionsService.ListLocalBranchesAsync(_root);

        Assert.NotNull(branches);
        Assert.Contains(currentBranch, branches!);
    }

    [Fact]
    public async Task CheckoutBranchAsync_SwitchesToExistingBranch()
    {
        InitRepoWithCommit();
        GitTestRepo.RunGit(_root, "branch feature-a");

        var result = await GitActionsService.CheckoutBranchAsync(_root, "feature-a");

        Assert.Equal(GitActionOutcome.Success, result.Outcome);
        string currentBranch = GitTestRepo.RunGitCapture(_root, "rev-parse --abbrev-ref HEAD").Trim();
        Assert.Equal("feature-a", currentBranch);
    }

    [Fact]
    public void HasUncommittedChanges_TrueOnlyWhenDirty()
    {
        InitRepoWithCommit();
        Assert.False(GitActionsService.HasUncommittedChanges(_root));

        File.WriteAllText(Path.Combine(_root, "new.txt"), "content");
        Assert.True(GitActionsService.HasUncommittedChanges(_root));
    }

    [Fact]
    public async Task PushAsync_AgainstConfiguredRemote_Succeeds()
    {
        string remoteRoot = Path.Combine(Path.GetTempPath(), "accel-git-actions-tests-remote-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitTestRepo.InitBareRepo(remoteRoot);
            InitRepoWithCommit();
            GitTestRepo.RunGit(_root, $"remote add origin \"{remoteRoot}\"");
            string currentBranch = GitTestRepo.RunGitCapture(_root, "rev-parse --abbrev-ref HEAD").Trim();
            GitTestRepo.RunGit(_root, $"push -u origin {currentBranch}");

            File.WriteAllText(Path.Combine(_root, "second.txt"), "content");
            GitTestRepo.RunGit(_root, "add second.txt");
            GitTestRepo.RunGit(_root, "commit -m second");

            var result = await GitActionsService.PushAsync(_root);

            Assert.Equal(GitActionOutcome.Success, result.Outcome);
        }
        finally
        {
            try
            {
                Directory.Delete(remoteRoot, recursive: true);
            }
            catch (Exception)
            {
                // Best-effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task PullAsync_NoRemoteConfigured_ReportsCommandFailed()
    {
        InitRepoWithCommit();

        var result = await GitActionsService.PullAsync(_root);

        Assert.Equal(GitActionOutcome.CommandFailed, result.Outcome);
    }
}

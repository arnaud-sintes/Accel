namespace Accel.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Accel.App.Services;
using Accel.App.ViewModels;
using Accel.Cli;
using Xunit;

/// <summary>
/// Unit tests for panel B's <see cref="GitPanelViewModel"/> - driven exactly like
/// <see cref="FilesPanelViewModelTests"/> (<see cref="FakeTelemetryFeed"/> +
/// <see cref="RecordingUiThreadDispatcher"/>, a real <see cref="SessionSelectionService"/>). Real
/// temporary git repositories (via a real `git init`) stand in for the focused folder, since
/// <see cref="GitStatusBuilder"/> shells out to the real `git` executable, not mockable
/// telemetry.
/// </summary>
public sealed class GitPanelViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "accel-git-panel-tests-" + Guid.NewGuid().ToString("N"));

    public GitPanelViewModelTests() => Directory.CreateDirectory(_root);

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

    private static string InitRepo(string path) => GitTestRepo.InitRepo(path);

    private static (GitPanelViewModel Vm, FakeTelemetryFeed Feed, SessionSelectionService Selection, ISessionSelectionWriter Writer) Build(
        RootsPanelViewModel? rootsPanel = null)
    {
        var feed = new FakeTelemetryFeed();
        var dispatcher = new RecordingUiThreadDispatcher();
        var selection = new SessionSelectionService();
        var writer = selection.AcquireWriter();
        return (new GitPanelViewModel(feed, dispatcher, selection, rootsPanel), feed, selection, writer);
    }

    private static (GitPanelViewModel Vm, FakeTelemetryFeed Feed, SessionSelectionService Selection, ISessionSelectionWriter Writer,
        FakeGitActionsDialogService ActionDialogs, FakeFilesEntryConfirmationService Confirmation) BuildWithActionFakes()
    {
        var feed = new FakeTelemetryFeed();
        var dispatcher = new RecordingUiThreadDispatcher();
        var selection = new SessionSelectionService();
        var writer = selection.AcquireWriter();
        var actionDialogs = new FakeGitActionsDialogService();
        var confirmation = new FakeFilesEntryConfirmationService();
        var vm = new GitPanelViewModel(feed, dispatcher, selection, rootsPanel: null, actionDialogs, confirmation);
        return (vm, feed, selection, writer, actionDialogs, confirmation);
    }

    private static void Focus(FakeTelemetryFeed feed, ISessionSelectionWriter writer, string root)
    {
        var session = TelemetryFixtures.Session("session-1", isLive: true) with { Cwd = root };
        writer.SetFocused("session-1");
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(root, session) }));
    }

    [Fact]
    public void NothingFocused_ShowsHintAndNoChanges()
    {
        var (vm, _, _, _) = Build();

        Assert.False(vm.HasRepo);
        Assert.Empty(vm.StagedChanges);
        Assert.Empty(vm.Changes);
        Assert.Equal("No folder or session focused.", vm.StatusText);
    }

    [Fact]
    public void FocusedFolder_NotARepo_ShowsHint()
    {
        var (vm, feed, _, writer) = Build();
        var session = TelemetryFixtures.Session("session-1", isLive: true) with { Cwd = _root };
        writer.SetFocused("session-1");

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(_root, session) }));

        Assert.False(vm.HasRepo);
        Assert.Equal($"Not a git repository: {_root}", vm.StatusText);
    }

    [Fact]
    public void FocusedFolder_RepoWithUntrackedFile_ListsItUnstaged()
    {
        InitRepo(_root);
        File.WriteAllText(Path.Combine(_root, "new.txt"), "content");

        var (vm, feed, _, writer) = Build();
        var session = TelemetryFixtures.Session("session-1", isLive: true) with { Cwd = _root };
        writer.SetFocused("session-1");

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(_root, session) }));

        Assert.True(vm.HasRepo);
        Assert.Empty(vm.StagedChanges);
        var change = Assert.Single(vm.Changes);
        Assert.Equal("new.txt", change.Path);
        Assert.Equal("Untracked", change.StatusDescription);
    }

    [Fact]
    public void ExpandedFolder_ThatIsARepo_OverridesTheResolvedRoot()
    {
        string nestedRepo = InitRepo(Path.Combine(_root, "nested-repo"));
        File.WriteAllText(Path.Combine(nestedRepo, "new.txt"), "content");

        var (vm, feed, _, writer) = Build();
        var session = TelemetryFixtures.Session("session-1", isLive: true) with { Cwd = _root };
        writer.SetFocused("session-1");
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(_root, session) }));

        Assert.False(vm.HasRepo); // the resolved root (_root) isn't a repo itself.

        vm.OnFilesPanelFolderExpanded(nestedRepo);

        Assert.True(vm.HasRepo);
        Assert.Equal(nestedRepo, vm.StatusText);
        Assert.Equal("new.txt", Assert.Single(vm.Changes).Path);
    }

    [Fact]
    public void ExpandedFolder_ThatIsNotARepoItself_StillShowsTheContainingRepo()
    {
        InitRepo(_root);
        File.WriteAllText(Path.Combine(_root, "new.txt"), "content");
        Directory.CreateDirectory(Path.Combine(_root, "subfolder"));

        var (vm, feed, _, writer) = Build();
        var session = TelemetryFixtures.Session("session-1", isLive: true) with { Cwd = _root };
        writer.SetFocused("session-1");
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(_root, session) }));

        // "subfolder" isn't a repo root itself, but `git status` run from inside it still resolves
        // to the same containing repo (_root) - so the displayed entries are unchanged.
        vm.OnFilesPanelFolderExpanded(Path.Combine(_root, "subfolder"));

        Assert.True(vm.HasRepo);
        Assert.Equal("new.txt", Assert.Single(vm.Changes).Path);
    }

    [Fact]
    public void FocusedFolder_RepoWithNoUpstream_ShowsRepoNameAndChangeCountButNoPushCount()
    {
        InitRepo(_root);
        File.WriteAllText(Path.Combine(_root, "new.txt"), "content");

        var (vm, feed, _, writer) = Build();
        var session = TelemetryFixtures.Session("session-1", isLive: true) with { Cwd = _root };
        writer.SetFocused("session-1");
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(_root, session) }));

        string expectedRepoName = Path.GetFileName(_root.TrimEnd('\\', '/'));
        Assert.Equal(expectedRepoName, vm.RepoName);
        Assert.Equal("1 change(s)", vm.ChangesSummaryText);
        Assert.Equal(string.Empty, vm.PendingPushSummaryText);
        Assert.Contains("no upstream", vm.RemoteBranchText);
    }

    [Fact]
    public void NothingFocused_ClearsRepoSummaryFields()
    {
        var (vm, _, _, _) = Build();

        Assert.Equal(string.Empty, vm.RepoName);
        Assert.Equal(string.Empty, vm.RemoteBranchText);
        Assert.Equal(string.Empty, vm.ChangesSummaryText);
        Assert.Equal(string.Empty, vm.PendingPushSummaryText);
    }

    [Fact]
    public void GenuineFocusChange_ClearsAnyExpandedFolderOverride()
    {
        string repoB = InitRepo(Path.Combine(_root, "repo-b"));
        File.WriteAllText(Path.Combine(repoB, "new.txt"), "content");

        var (vm, feed, _, writer) = Build();
        var session = TelemetryFixtures.Session("session-1", isLive: true) with { Cwd = _root };
        writer.SetFocused("session-1");
        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(_root, session) }));

        vm.OnFilesPanelFolderExpanded(repoB);
        Assert.Equal(repoB, vm.StatusText);

        // A genuine focus change (session-2 isn't in the published snapshot, so it resolves to "no
        // folder focused") must drop the stale override rather than keep showing repo-b.
        writer.SetFocused("session-2");

        Assert.False(vm.HasRepo);
        Assert.Equal("No folder or session focused.", vm.StatusText);
    }

    [Fact]
    public async Task StageFileCommand_MovesEntryFromChangesToStaged()
    {
        InitRepo(_root);
        File.WriteAllText(Path.Combine(_root, "new.txt"), "content");

        var (vm, feed, _, writer) = Build();
        Focus(feed, writer, _root);

        var entry = Assert.Single(vm.Changes);
        await vm.StageFileCommand.ExecuteAsync(entry);

        var entries = GitStatusBuilder.Build(_root);
        Assert.Contains(entries!, e => e.Path == "new.txt" && e.IsStaged);
        Assert.True(feed.RefreshRequestCount > 0);
    }

    [Fact]
    public async Task UnstageFileCommand_MovesEntryBackToChanges()
    {
        InitRepo(_root);
        File.WriteAllText(Path.Combine(_root, "new.txt"), "content");
        GitTestRepo.RunGit(_root, "add new.txt");

        var (vm, feed, _, writer) = Build();
        Focus(feed, writer, _root);

        var entry = Assert.Single(vm.StagedChanges);
        await vm.UnstageFileCommand.ExecuteAsync(entry);

        var entries = GitStatusBuilder.Build(_root);
        Assert.Contains(entries!, e => e.Path == "new.txt" && !e.IsStaged);
        Assert.True(feed.RefreshRequestCount > 0);
    }

    [Fact]
    public async Task StageAllCommand_StagesEveryDirtyFile()
    {
        InitRepo(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "b");

        var (vm, feed, _, writer) = Build();
        Focus(feed, writer, _root);

        await vm.StageAllCommand.ExecuteAsync(null);

        var entries = GitStatusBuilder.Build(_root);
        Assert.All(entries!, e => Assert.True(e.IsStaged));
        Assert.True(feed.RefreshRequestCount > 0);
    }

    [Fact]
    public async Task StageAllCommand_NothingDirty_IsANoOp()
    {
        InitRepo(_root);

        var (vm, feed, _, writer) = Build();
        Focus(feed, writer, _root);

        await vm.StageAllCommand.ExecuteAsync(null);

        Assert.Equal(0, feed.RefreshRequestCount);
    }

    [Fact]
    public async Task DiscardFileCommand_Confirmed_RestoresOriginalContent()
    {
        InitRepo(_root);
        File.WriteAllText(Path.Combine(_root, "tracked.txt"), "original");
        GitTestRepo.RunGit(_root, "add tracked.txt");
        GitTestRepo.RunGit(_root, "commit -m initial");
        File.WriteAllText(Path.Combine(_root, "tracked.txt"), "edited");

        var (vm, feed, _, writer, _, confirmation) = BuildWithActionFakes();
        confirmation.ConfirmDiscardChangesResult = true;
        Focus(feed, writer, _root);

        var entry = Assert.Single(vm.Changes);
        await vm.DiscardFileCommand.ExecuteAsync(entry);

        Assert.Equal("original", File.ReadAllText(Path.Combine(_root, "tracked.txt")));
        Assert.True(feed.RefreshRequestCount > 0);
    }

    [Fact]
    public async Task DiscardFileCommand_Cancelled_LeavesFileUntouched()
    {
        InitRepo(_root);
        File.WriteAllText(Path.Combine(_root, "tracked.txt"), "original");
        GitTestRepo.RunGit(_root, "add tracked.txt");
        GitTestRepo.RunGit(_root, "commit -m initial");
        File.WriteAllText(Path.Combine(_root, "tracked.txt"), "edited");

        var (vm, feed, _, writer, _, confirmation) = BuildWithActionFakes();
        confirmation.ConfirmDiscardChangesResult = false;
        Focus(feed, writer, _root);

        var entry = Assert.Single(vm.Changes);
        await vm.DiscardFileCommand.ExecuteAsync(entry);

        Assert.Equal("edited", File.ReadAllText(Path.Combine(_root, "tracked.txt")));
        Assert.Equal(0, feed.RefreshRequestCount);
    }

    [Fact]
    public async Task CommitCommand_WithStagedChanges_CreatesCommit()
    {
        InitRepo(_root);
        File.WriteAllText(Path.Combine(_root, "new.txt"), "content");
        GitTestRepo.RunGit(_root, "add new.txt");

        var (vm, feed, _, writer, actionDialogs, _) = BuildWithActionFakes();
        actionDialogs.CommitMessage = "A commit message";
        Focus(feed, writer, _root);

        await vm.CommitCommand.ExecuteAsync(null);

        string log = GitTestRepo.RunGitCapture(_root, "log -1 --pretty=%s");
        Assert.Equal("A commit message", log.Trim());
        Assert.True(feed.RefreshRequestCount > 0);
    }

    [Fact]
    public async Task CommitCommand_DialogCancelled_DoesNotCommit()
    {
        InitRepo(_root);
        File.WriteAllText(Path.Combine(_root, "new.txt"), "content");
        GitTestRepo.RunGit(_root, "add new.txt");

        var (vm, feed, _, writer, actionDialogs, _) = BuildWithActionFakes();
        actionDialogs.CommitMessage = null;
        Focus(feed, writer, _root);

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal(0, feed.RefreshRequestCount);
    }

    [Fact]
    public async Task CommitCommand_NothingStaged_IsANoOp()
    {
        InitRepo(_root);
        File.WriteAllText(Path.Combine(_root, "committed.txt"), "content");
        GitTestRepo.RunGit(_root, "add committed.txt");
        GitTestRepo.RunGit(_root, "commit -m initial");

        var (vm, feed, _, writer) = Build();
        Focus(feed, writer, _root);

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal(0, feed.RefreshRequestCount);
    }

    [Fact]
    public async Task SwitchBranchAsync_CleanTree_ChecksOutWithoutPrompting()
    {
        InitRepo(_root);
        File.WriteAllText(Path.Combine(_root, "committed.txt"), "content");
        GitTestRepo.RunGit(_root, "add committed.txt");
        GitTestRepo.RunGit(_root, "commit -m initial");
        GitTestRepo.RunGit(_root, "branch feature-a");

        var (vm, feed, _, writer) = Build();
        Focus(feed, writer, _root);

        await vm.SwitchBranchAsync("feature-a");

        string currentBranch = GitTestRepo.RunGitCapture(_root, "rev-parse --abbrev-ref HEAD").Trim();
        Assert.Equal("feature-a", currentBranch);
        Assert.True(feed.RefreshRequestCount > 0);
    }
}

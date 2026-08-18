namespace Accel.Tests;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Accel.App.Services;
using Accel.App.ViewModels;
using Xunit;

/// <summary>
/// Unit tests for panel B's <see cref="GitPanelViewModel"/> - driven exactly like
/// <see cref="FilesPanelViewModelTests"/> (<see cref="FakeTelemetryFeed"/> +
/// <see cref="RecordingUiThreadDispatcher"/>, a real <see cref="SessionSelectionService"/>). Real
/// temporary git repositories (via a real `git init`) stand in for the focused folder, since
/// <see cref="Accel.Cli.GitStatusBuilder"/> shells out to the real `git` executable, not mockable
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

    private static string InitRepo(string path)
    {
        Directory.CreateDirectory(path);
        RunGit(path, "init");
        RunGit(path, "config user.email test@example.com");
        RunGit(path, "config user.name \"Accel Tests\"");
        return path;
    }

    private static void RunGit(string workingDirectory, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.Start();
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(5000);
    }

    private static (GitPanelViewModel Vm, FakeTelemetryFeed Feed, SessionSelectionService Selection, ISessionSelectionWriter Writer) Build(
        RootsPanelViewModel? rootsPanel = null)
    {
        var feed = new FakeTelemetryFeed();
        var dispatcher = new RecordingUiThreadDispatcher();
        var selection = new SessionSelectionService();
        var writer = selection.AcquireWriter();
        return (new GitPanelViewModel(feed, dispatcher, selection, rootsPanel), feed, selection, writer);
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
}

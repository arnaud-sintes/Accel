namespace Accel.Cli;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

/// <summary>One changed path from `git status`, as reported for either the index (staged) or the
/// working tree (unstaged) side of a line - a line with changes on both sides yields two entries,
/// one per side, matching how VS Code's Source Control view groups the same path under both
/// "Staged Changes" and "Changes" when it differs in both places.</summary>
/// <remarks><paramref name="IsConflicted"/> marks an <i>unmerged</i> path (a merge/rebase/cherry-pick
/// conflict). Such a path is neither staged nor unstaged - git holds up to three competing blobs for
/// it in the index (stages 1/2/3) instead of the usual single one - so it yields exactly ONE entry
/// with <paramref name="IsStaged"/> false, never the two an ordinary both-sides-changed line would.
/// See <see cref="GitStatusBuilder.UnmergedDescription"/> for the seven code pairs that qualify.</remarks>
public sealed record GitChangeEntry(
    string Path, char StatusCode, string StatusDescription, bool IsStaged, bool IsConflicted = false);

/// <summary>The multi-step git operation a repository is currently stopped in the middle of, as
/// implied by the marker files git leaves in its git directory. <see cref="None"/> is the ordinary
/// case; anything else means the working tree may hold unmerged paths and that the operation has to
/// be either completed or aborted before normal work can resume.</summary>
public enum GitInProgressOperation
{
    None,
    Merge,
    Rebase,
    CherryPick,
    Revert,
}

/// <summary>Panel B's git header summary: the repo's folder name, its current branch's upstream
/// (when one is configured), how many local commits on that branch haven't been pushed to it
/// yet, and whether a merge/rebase/cherry-pick/revert is currently in progress.
/// <see cref="RemoteBranch"/> is <c>null</c> when the branch has no upstream configured, in
/// which case <see cref="AheadCount"/> is always 0 (there's nothing to compare against).</summary>
public sealed record GitRepoSummary(
    string RepoName,
    string? Branch,
    string? RemoteBranch,
    int AheadCount,
    GitInProgressOperation InProgressOperation = GitInProgressOperation.None);

/// <summary>
/// Pure, WPF-free builder for panel B's git status list - the git-status counterpart to
/// <see cref="FilesTreeBuilder"/> (which walks disk, not `git status`). Shells out to the `git`
/// executable found on PATH rather than a library like LibGit2Sharp: this repo has no git
/// dependency yet (Phase 7 is read-only, list-only - no stage/commit/push actions), and porcelain
/// v1 output is a small, stable format to parse by hand.
///
/// <para>Never throws: git not installed, the folder not being a repository, and any I/O failure
/// all degrade to <c>null</c> for that call, matching <see cref="FilesTreeBuilder"/>'s "never
/// propagate" convention. Called on a focus change, on one of panel B's own git actions, and on a
/// debounced <c>FileSystemWatcher</c> signal for the repository being shown (see
/// <see cref="Accel.App.ViewModels.GitPanelViewModel"/>'s remarks) - never on a timer, so an
/// untouched repository costs nothing.</para>
/// </summary>
public static class GitStatusBuilder
{
    /// <summary>Runs `git status --porcelain=v1 --untracked-files=all` in <paramref name="repoRootPath"/>
    /// and parses its output, or <c>null</c> if the path is empty/missing, `git` could not be
    /// launched, or the folder is not (inside) a git repository (non-zero exit code).</summary>
    public static GitChangeEntry[]? Build(string? repoRootPath)
    {
        if (string.IsNullOrWhiteSpace(repoRootPath) || !Directory.Exists(repoRootPath))
        {
            return null;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git", "status --porcelain=v1 --untracked-files=all")
                {
                    WorkingDirectory = repoRootPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(5000);

            return process.ExitCode == 0 ? Parse(output) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Runs the handful of `git rev-parse`/`git rev-list` calls needed for panel B's git
    /// header summary, or <c>null</c> under the same "not a repo" conditions as <see cref="Build"/>.
    /// Each call degrades independently: no upstream configured for the current branch leaves
    /// <see cref="GitRepoSummary.RemoteBranch"/> null and <see cref="GitRepoSummary.AheadCount"/> at
    /// 0 rather than failing the whole summary.</summary>
    public static GitRepoSummary? BuildSummary(string? repoRootPath)
    {
        if (string.IsNullOrWhiteSpace(repoRootPath) || !Directory.Exists(repoRootPath))
        {
            return null;
        }

        string? toplevel = RunGitCommand(repoRootPath, "rev-parse --show-toplevel");
        if (toplevel is null)
        {
            return null;
        }

        string repoName = Path.GetFileName(toplevel.TrimEnd('/', '\\'));
        string? branch = RunGitCommand(repoRootPath, "rev-parse --abbrev-ref HEAD");
        string? remoteBranch = RunGitCommand(repoRootPath, "rev-parse --abbrev-ref --symbolic-full-name @{u}");

        int aheadCount = 0;
        if (!string.IsNullOrEmpty(remoteBranch))
        {
            string? aheadText = RunGitCommand(repoRootPath, "rev-list --count @{u}..HEAD");
            int.TryParse(aheadText, out aheadCount);
        }

        return new GitRepoSummary(repoName, branch, remoteBranch, aheadCount, ReadInProgressOperation(repoRootPath));
    }

    /// <summary>
    /// Which multi-step operation, if any, the repository is stopped in the middle of - read from the
    /// marker files/directories git itself uses for exactly this purpose (the same ones the stock
    /// bash prompt and `git status`'s own header inspect). Deliberately a file-existence check rather
    /// than a sixth `git` subprocess per refresh: this is read on every watcher tick (see
    /// <see cref="Accel.App.ViewModels.GitPanelViewModel"/>'s remarks on refresh cost), and
    /// `--absolute-git-dir` is the only process call it needs, which the summary would be making
    /// anyway.
    ///
    /// <para>Order matters: a `git rebase` that stops on a conflict leaves both <c>REBASE_HEAD</c>
    /// and (for a merge-strategy rebase) a <c>MERGE_MSG</c>, and a cherry-pick/revert also writes
    /// <c>MERGE_MSG</c> - so the more specific markers are tested before the plain merge one, or
    /// every rebase conflict would be reported as a merge and offered `git merge --abort`, which
    /// would fail.</para>
    /// </summary>
    private static GitInProgressOperation ReadInProgressOperation(string repoRootPath)
    {
        string? gitDir = RunGitCommand(repoRootPath, "rev-parse --absolute-git-dir");
        if (string.IsNullOrEmpty(gitDir))
        {
            return GitInProgressOperation.None;
        }

        try
        {
            if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")) || Directory.Exists(Path.Combine(gitDir, "rebase-apply")))
            {
                return GitInProgressOperation.Rebase;
            }

            if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")))
            {
                return GitInProgressOperation.CherryPick;
            }

            if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD")))
            {
                return GitInProgressOperation.Revert;
            }

            return File.Exists(Path.Combine(gitDir, "MERGE_HEAD"))
                ? GitInProgressOperation.Merge
                : GitInProgressOperation.None;
        }
        catch (Exception)
        {
            // Same "never propagate" rule as every other method here - an unreadable git directory
            // just means "no operation detected", not a broken panel.
            return GitInProgressOperation.None;
        }
    }

    /// <summary>
    /// The root of the repository containing <paramref name="path"/> (`git rev-parse
    /// --show-toplevel`, normalized to a platform path), or null when <paramref name="path"/> is not
    /// inside a repository at all.
    /// </summary>
    /// <remarks>
    /// Needed because every other method here happily accepts a <i>subfolder</i> of a repository -
    /// `git status` run in <c>repo/src</c> still reports the whole repository, with repo-root-relative
    /// paths - so "the folder panel B is showing" and "the folder whose contents determine what panel
    /// B shows" are not the same directory. Anything that has to scope itself to the second (notably
    /// <see cref="Accel.App.Services.IDirectoryWatcher"/>: a commit or a branch switch shows up in
    /// <c>.git</c>, which only exists at the root) needs this rather than the displayed path.
    /// </remarks>
    public static string? FindRepositoryRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return null;
        }

        string? toplevel = RunGitCommand(path, "rev-parse --show-toplevel");
        if (string.IsNullOrEmpty(toplevel))
        {
            return null;
        }

        try
        {
            // git prints forward slashes even on Windows; GetFullPath normalizes them so the result
            // compares equal to paths built anywhere else in the app.
            return Path.GetFullPath(toplevel);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs `git show HEAD:&lt;relativePath&gt;` in <paramref name="repoRootPath"/> to retrieve a
    /// path's last-committed content - used for panel D's read-only git-change tab
    /// (<c>MainWindow.ShowFileTabAsync</c>) when the working-tree copy no longer exists (a Deleted
    /// entry). Thin convenience wrapper over <see cref="ReadGitObject"/> for the one revision spec
    /// that call site needs.
    /// </summary>
    public static string? ReadCommittedContent(string repoRootPath, string relativePath) =>
        ReadGitObject(repoRootPath, $"HEAD:{relativePath}");

    /// <summary>
    /// Runs `git show &lt;gitObjectSpec&gt;` (e.g. <c>"HEAD:src/foo.cs"</c> for the last commit, or
    /// <c>":src/foo.cs"</c> for the index/staged blob) in <paramref name="repoRootPath"/> - the
    /// general form <see cref="ReadCommittedContent"/> wraps, and what panel D's side-by-side git
    /// diff tab (<c>MainWindow.ShowGitDiffTabAsync</c>) uses for whichever side of the comparison
    /// isn't the plain working-tree file. Never throws: any failure (not a repo, the object doesn't
    /// exist at that revision, git not installed) returns <see langword="null"/>, matching this
    /// class's other "never propagate" convention. <paramref name="gitObjectSpec"/> is passed via
    /// <see cref="ProcessStartInfo.ArgumentList"/> (never string-concatenated into a single arguments
    /// string) so a path containing spaces or shell-special characters cannot be misparsed or escape
    /// the intended argument.
    /// </summary>
    public static string? ReadGitObject(string repoRootPath, string gitObjectSpec)
    {
        if (string.IsNullOrWhiteSpace(repoRootPath) || string.IsNullOrWhiteSpace(gitObjectSpec))
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = repoRootPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("show");
            startInfo.ArgumentList.Add(gitObjectSpec);

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(5000);

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? RunGitCommand(string workingDirectory, string arguments)
    {
        try
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
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.StandardError.ReadToEnd();
            process.WaitForExit(5000);

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static GitChangeEntry[] Parse(string porcelainOutput)
    {
        var result = new List<GitChangeEntry>();

        foreach (string rawLine in porcelainOutput.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length < 4)
            {
                continue;
            }

            char indexStatus = line[0];
            char worktreeStatus = line[1];
            string pathPart = line.Substring(3);

            // Rename/copy lines read "ORIG_PATH -> NEW_PATH" - the new path is the one worth
            // showing, same as VS Code's own SCM list.
            int arrow = pathPart.IndexOf(" -> ", StringComparison.Ordinal);
            string path = arrow >= 0 ? pathPart.Substring(arrow + 4) : pathPart;

            if (indexStatus == '?' && worktreeStatus == '?')
            {
                result.Add(new GitChangeEntry(path, '?', "Untracked", IsStaged: false));
                continue;
            }

            // Unmerged paths are checked as a PAIR, before the per-side split below, because the two
            // characters are not an index status and a worktree status for such a line - they name
            // which side did what to the file. Splitting them positionally listed one conflict twice
            // (once as "staged", once as "unstaged") and, for the pairs that contain no 'U' at all
            // (AA/DD), reported it as an ordinary Added/Deleted change indistinguishable from a real
            // one.
            if (UnmergedDescription(indexStatus, worktreeStatus) is { } conflictDescription)
            {
                result.Add(new GitChangeEntry(path, 'U', conflictDescription, IsStaged: false, IsConflicted: true));
                continue;
            }

            if (indexStatus != ' ')
            {
                result.Add(new GitChangeEntry(path, indexStatus, DescribeStatus(indexStatus), IsStaged: true));
            }

            if (worktreeStatus != ' ')
            {
                result.Add(new GitChangeEntry(path, worktreeStatus, DescribeStatus(worktreeStatus), IsStaged: false));
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// The human-readable conflict kind for an <i>unmerged</i> porcelain v1 code pair, or
    /// <see langword="null"/> when the pair is an ordinary index/worktree status combination.
    /// These seven pairs are the complete set git documents as unmerged (git-status(1),
    /// "Short Format"); "us"/"them" read from the perspective the pair is recorded in - during a
    /// rebase that is inverted relative to the branch the user started on, which is why the panel
    /// pairs this text with the operation from <see cref="GitRepoSummary.InProgressOperation"/>
    /// rather than claiming a branch name.
    /// </summary>
    internal static string? UnmergedDescription(char indexStatus, char worktreeStatus) => (indexStatus, worktreeStatus) switch
    {
        ('U', 'U') => "Conflict: both modified",
        ('A', 'A') => "Conflict: both added",
        ('D', 'D') => "Conflict: both deleted",
        ('A', 'U') => "Conflict: added by us",
        ('U', 'A') => "Conflict: added by them",
        ('D', 'U') => "Conflict: deleted by us",
        ('U', 'D') => "Conflict: deleted by them",
        _ => null,
    };

    private static string DescribeStatus(char code) => code switch
    {
        'M' => "Modified",
        'A' => "Added",
        'D' => "Deleted",
        'R' => "Renamed",
        'C' => "Copied",
        'U' => "Conflict",
        '?' => "Untracked",
        _ => "Changed",
    };
}

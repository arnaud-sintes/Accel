namespace Accel.Cli;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

/// <summary>One changed path from `git status`, as reported for either the index (staged) or the
/// working tree (unstaged) side of a line - a line with changes on both sides yields two entries,
/// one per side, matching how VS Code's Source Control view groups the same path under both
/// "Staged Changes" and "Changes" when it differs in both places.</summary>
public sealed record GitChangeEntry(string Path, char StatusCode, string StatusDescription, bool IsStaged);

/// <summary>Panel B's git header summary: the repo's folder name, its current branch's upstream
/// (when one is configured), and how many local commits on that branch haven't been pushed to it
/// yet. <see cref="RemoteBranch"/> is <c>null</c> when the branch has no upstream configured, in
/// which case <see cref="AheadCount"/> is always 0 (there's nothing to compare against).</summary>
public sealed record GitRepoSummary(string RepoName, string? Branch, string? RemoteBranch, int AheadCount);

/// <summary>
/// Pure, WPF-free builder for panel B's git status list - the git-status counterpart to
/// <see cref="FilesTreeBuilder"/> (which walks disk, not `git status`). Shells out to the `git`
/// executable found on PATH rather than a library like LibGit2Sharp: this repo has no git
/// dependency yet (Phase 7 is read-only, list-only - no stage/commit/push actions), and porcelain
/// v1 output is a small, stable format to parse by hand.
///
/// <para>Never throws: git not installed, the folder not being a repository, and any I/O failure
/// all degrade to <c>null</c> for that call, matching <see cref="FilesTreeBuilder"/>'s "never
/// propagate" convention. Called only on a focus change (see
/// <see cref="Accel.App.ViewModels.GitPanelViewModel"/>'s remarks), never on a timer or
/// <c>FileSystemWatcher</c> - so a stale git status simply waits for the next focus change, same
/// as panel B's file tree.</para>
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

        return new GitRepoSummary(repoName, branch, remoteBranch, aheadCount);
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

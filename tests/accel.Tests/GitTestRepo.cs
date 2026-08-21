namespace Accel.Tests;

using System.Diagnostics;
using System.IO;

/// <summary>Shared "real `git.exe`, real temp repo" test fixture helper — every test that exercises
/// <see cref="Accel.Cli.GitStatusBuilder"/> or <see cref="Accel.Cli.GitActionsService"/> drives a
/// real repository rather than mocking <see cref="Process"/>, since both of those classes shell out
/// to the real `git` executable.</summary>
internal static class GitTestRepo
{
    public static string InitRepo(string path)
    {
        Directory.CreateDirectory(path);
        RunGit(path, "init");
        RunGit(path, "config user.email test@example.com");
        RunGit(path, "config user.name \"Accel Tests\"");
        return path;
    }

    public static string InitBareRepo(string path)
    {
        Directory.CreateDirectory(path);
        RunGit(path, "init --bare");
        return path;
    }

    /// <summary>Leaves <paramref name="path"/> as a repository stopped in the middle of a conflicting
    /// merge: <c>conflict.txt</c> is unmerged (both sides modified the same line, so the working-tree
    /// copy carries conflict markers) while <c>agreed.txt</c> merged cleanly. Returns the name of the
    /// branch the merge is being made <i>into</i> - "ours" - which is not hard-coded because the
    /// initial branch name depends on the host's <c>init.defaultBranch</c>.</summary>
    public static string CreateMergeConflict(string path)
    {
        InitRepo(path);
        File.WriteAllText(Path.Combine(path, "conflict.txt"), "base\n");
        File.WriteAllText(Path.Combine(path, "agreed.txt"), "shared\n");
        RunGit(path, "add -A");
        RunGit(path, "commit -m initial");

        string baseBranch = RunGitCapture(path, "rev-parse --abbrev-ref HEAD").Trim();

        RunGit(path, "checkout -b incoming");
        File.WriteAllText(Path.Combine(path, "conflict.txt"), "theirs\n");
        RunGit(path, "add -A");
        RunGit(path, "commit -m theirs");

        RunGit(path, "checkout " + baseBranch);
        File.WriteAllText(Path.Combine(path, "conflict.txt"), "ours\n");
        RunGit(path, "add -A");
        RunGit(path, "commit -m ours");

        // Expected to fail with a conflict - that failure IS the fixture.
        RunGit(path, "merge incoming");
        return baseBranch;
    }

    /// <summary>Reads a working-tree file with its line endings normalized to <c>\n</c>. Necessary
    /// because `git checkout` applies the host's <c>core.autocrlf</c> setting, so the same fixture
    /// yields "ours\n" on one developer's machine and "ours\r\n" on another - a difference none of
    /// these tests are about.</summary>
    public static string ReadNormalized(string repoPath, string relativePath) =>
        File.ReadAllText(Path.Combine(repoPath, relativePath)).Replace("\r\n", "\n");

    public static void RunGit(string workingDirectory, string arguments)
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

    public static string RunGitCapture(string workingDirectory, string arguments)
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
        string output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(5000);
        return output;
    }
}

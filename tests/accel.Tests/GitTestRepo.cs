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

namespace Accel.Cli;

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Outcome of running a single <c>git</c> invocation.</summary>
public sealed class GitCommandOutcome
{
    /// <summary>False when the process could not even be started (git missing, bad working dir).</summary>
    public bool Started { get; init; }

    /// <summary>True when the command exceeded its timeout budget and was killed.</summary>
    public bool TimedOut { get; init; }

    /// <summary>Child exit code, or -1 when unknown (not started / killed).</summary>
    public int ExitCode { get; init; }

    /// <summary>Decoded stdout — every caller here parses or displays this as text, unlike
    /// <see cref="ShellCommandRunner"/>'s opaque byte relay.</summary>
    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;

    public static GitCommandOutcome NotStarted { get; } = new() { Started = false, ExitCode = -1 };

    public bool Succeeded => Started && !TimedOut && ExitCode == 0;
}

/// <summary>
/// Runs a single <c>git &lt;args...&gt;</c> invocation asynchronously, under a caller-supplied
/// timeout — the mutating-command counterpart to <see cref="GitStatusBuilder"/>'s synchronous,
/// read-only process calls. Arguments are always passed via <see cref="ProcessStartInfo.ArgumentList"/>
/// (never a concatenated string), so a file path or commit message containing spaces/quotes can't
/// be misparsed, matching <see cref="GitStatusBuilder.ReadGitObject"/>'s existing precedent.
///
/// <para>Never throws: every failure mode (git missing, bad working directory, timeout) is reported
/// through <see cref="GitCommandOutcome"/>, matching this codebase's other process-running
/// conventions (<see cref="ShellCommandRunner"/>, <see cref="GitStatusBuilder"/>).</para>
/// </summary>
public static class GitCommandRunner
{
    /// <summary>Timeout for local, disk-only git commands (stage/unstage/commit/checkout/discard/
    /// branch-list) — these never touch the network and should complete near-instantly.</summary>
    public static readonly TimeSpan LocalOperationTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Timeout for network-bound git commands (push/pull) — generous enough to tolerate a
    /// slow remote or an interactive credential prompt surfaced by the platform's credential
    /// helper.</summary>
    public static readonly TimeSpan NetworkOperationTimeout = TimeSpan.FromSeconds(120);

    /// <summary>How long to keep draining the child's pipes after it exited or was killed.</summary>
    private static readonly TimeSpan DrainBudget = TimeSpan.FromMilliseconds(250);

    public static async Task<GitCommandOutcome> RunAsync(
        string repoRootPath,
        string[] arguments,
        TimeSpan timeout,
        string? stdin = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repoRootPath) || !Directory.Exists(repoRootPath) || arguments.Length == 0)
        {
            return GitCommandOutcome.NotStarted;
        }

        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoRootPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };

        try
        {
            try
            {
                if (!process.Start())
                {
                    return GitCommandOutcome.NotStarted;
                }
            }
            catch
            {
                // git not on PATH, bad working directory, or blocked by policy — all "did not start".
                return GitCommandOutcome.NotStarted;
            }

            // Start pumping both output pipes and pushing stdin *before* waiting for exit: a child
            // that writes more than a pipe buffer would otherwise deadlock.
            var stdoutTask = DrainAsync(process.StandardOutput.BaseStream);
            var stderrTask = DrainAsync(process.StandardError.BaseStream);
            var stdinTask = FeedStdinAsync(process, stdin);

            var timedOut = false;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(timeout);
                try
                {
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    timedOut = true;
                    TryKill(process);
                }
            }

            // Bounded drain: killing the child closes its handles, so the pumps normally finish
            // immediately — but a surviving grandchild holding the pipe must not hang us.
            await Task.WhenAny(
                    Task.WhenAll(stdoutTask, stderrTask, stdinTask),
                    Task.Delay(DrainBudget, CancellationToken.None))
                .ConfigureAwait(false);

            return new GitCommandOutcome
            {
                Started = true,
                TimedOut = timedOut,
                ExitCode = TryGetExitCode(process),
                StandardOutput = Harvest(stdoutTask),
                StandardError = Harvest(stderrTask),
            };
        }
        catch
        {
            return GitCommandOutcome.NotStarted;
        }
        finally
        {
            try
            {
                process.Dispose();
            }
            catch
            {
                // Nothing useful to do.
            }
        }
    }

    private static async Task<string> DrainAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        try
        {
            // No cancellation token on purpose: cancelling an anonymous-pipe read can leave the
            // read pending forever on Windows. Killing the child closes the pipe, which ends this
            // copy naturally.
            await stream.CopyToAsync(buffer).ConfigureAwait(false);
        }
        catch
        {
            // Partial content is kept; a broken pipe is normal when the child is killed.
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task FeedStdinAsync(Process process, string? stdin)
    {
        try
        {
            var writer = process.StandardInput;
            if (!string.IsNullOrEmpty(stdin))
            {
                await writer.WriteAsync(stdin).ConfigureAwait(false);
            }

            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            // Broken pipe: the child simply did not read its stdin. Not an error.
        }
        finally
        {
            try
            {
                // Must close, or a child that reads to EOF would wait forever.
                process.StandardInput.Close();
            }
            catch
            {
                // Already gone.
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Race with natural exit, or no rights to kill — nothing further to do.
        }
    }

    private static int TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string Harvest(Task<string> task) =>
        task.IsCompletedSuccessfully ? task.Result : string.Empty;
}

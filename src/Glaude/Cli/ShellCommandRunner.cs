namespace Glaude.Cli;

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Outcome of running an arbitrary shell command string.</summary>
public sealed class ShellCommandResult
{
    /// <summary>False when the process could not even be started.</summary>
    public bool Started { get; init; }

    /// <summary>True when the command exceeded its timeout budget and was killed.</summary>
    public bool TimedOut { get; init; }

    /// <summary>Child exit code, or -1 when unknown (not started / killed).</summary>
    public int ExitCode { get; init; }

    /// <summary>
    /// Raw stdout bytes. Deliberately <b>bytes</b>, not a string: for the status-line chain this
    /// is opaque display text that must be relayed byte-for-byte (it may be ANSI-coloured or in
    /// an encoding we have no business re-interpreting).
    /// </summary>
    public byte[] StandardOutput { get; init; } = Array.Empty<byte>();

    /// <summary>Raw stderr bytes. Drained only to keep the pipe from filling; never relayed.</summary>
    public byte[] StandardError { get; init; } = Array.Empty<byte>();

    public static ShellCommandResult NotStarted { get; } = new() { Started = false, ExitCode = -1 };

    public bool Succeeded => Started && !TimedOut && ExitCode == 0;
}

/// <summary>
/// Runs an arbitrary <b>shell command string</b> (not an exe + argv), feeding it a fixed stdin
/// buffer and capturing its stdout as raw bytes, under a hard timeout.
///
/// Needed because <c>statusLine</c> in Claude Code's settings.json has no args/exec form — the
/// captured original is a shell string, so re-invoking it means going back through a shell
/// (<c>cmd.exe /d /s /c "…"</c> on Windows, <c>/bin/sh -c</c> elsewhere).
///
/// Never throws: every failure mode is reported through <see cref="ShellCommandResult"/>.
/// </summary>
public static class ShellCommandRunner
{
    /// <summary>How long to keep draining the child's pipes after it exited or was killed.</summary>
    private static readonly TimeSpan DrainBudget = TimeSpan.FromMilliseconds(250);

    public static async Task<ShellCommandResult> RunAsync(
        string? commandLine,
        byte[]? stdin,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return ShellCommandResult.NotStarted;
        }

        var process = new Process { StartInfo = BuildStartInfo(commandLine!) };

        try
        {
            try
            {
                if (!process.Start())
                {
                    return ShellCommandResult.NotStarted;
                }
            }
            catch
            {
                // Missing shell, blocked by policy, bad command line — all "did not start".
                return ShellCommandResult.NotStarted;
            }

            // Start pumping both output pipes and pushing stdin *before* waiting for exit:
            // a child that writes more than a pipe buffer would otherwise deadlock.
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

            return new ShellCommandResult
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
            return ShellCommandResult.NotStarted;
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

    private static ProcessStartInfo BuildStartInfo(string commandLine)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi.FileName = "cmd.exe";

            // /d skips AutoRun scripts (a user's AutoRun could otherwise inject stdout into the
            // status bar); /s + wrapping quotes makes cmd strip exactly the outer pair and take
            // the remainder verbatim, which is what makes embedded quotes in the captured
            // command (e.g. "C:\Program Files\x\y.exe" --flag) survive intact.
            psi.Arguments = "/d /s /c \"" + commandLine + "\"";
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(commandLine);
        }

        return psi;
    }

    private static async Task<byte[]> DrainAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        try
        {
            // No cancellation token on purpose: cancelling an anonymous-pipe read can leave the
            // read pending forever on Windows. Killing the child closes the pipe, which ends
            // this copy naturally.
            await stream.CopyToAsync(buffer).ConfigureAwait(false);
        }
        catch
        {
            // Partial content is kept; a broken pipe is normal when the child is killed.
        }

        return buffer.ToArray();
    }

    private static async Task FeedStdinAsync(Process process, byte[]? stdin)
    {
        try
        {
            var stream = process.StandardInput.BaseStream;
            if (stdin is { Length: > 0 })
            {
                await stream.WriteAsync(stdin).ConfigureAwait(false);
            }

            await stream.FlushAsync().ConfigureAwait(false);
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

    private static byte[] Harvest(Task<byte[]> task) =>
        task.IsCompletedSuccessfully ? task.Result : Array.Empty<byte>();
}

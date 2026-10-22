namespace Accel.Tests;

using System;
using System.Threading.Tasks;
using Accel.Orchestration;
using Xunit;

/// <summary>
/// A plain terminal must open - and still report its exit - on machines that refuse the two extras around
/// the launch: assigning a shell to the kill-on-close job (Win32 error 5) and reopening the child by PID
/// to watch it (Access is denied). The lenient launch is what "Open terminal here" uses.
/// </summary>
public class PtySessionLenientLaunchTests
{
    private static readonly PtySessionOptions Lenient = new() { JobAssignmentRequired = false };

    [Fact]
    public void ShellSpec_Launches_WithoutRequiringTheJob()
    {
        using var shell = PtySession.Start(PtySession.CreateShellSpec(Environment.CurrentDirectory), Lenient);

        Assert.True(shell.ProcessId > 0);
    }

    [Fact]
    public async Task ChildExit_AndItsExitCode_AreObserved_EvenWithoutAProcessObserver()
    {
        string cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        using var session = PtySession.Start(
            new PtyLaunchSpec { ExecutablePath = cmd, Arguments = new[] { "/c", "exit", "7" } },
            Lenient);

        var finished = await Task.WhenAny(session.ExitTask, Task.Delay(TimeSpan.FromSeconds(15)));

        Assert.Same(session.ExitTask, finished);
        Assert.Equal(7, await session.ExitTask);
    }
}

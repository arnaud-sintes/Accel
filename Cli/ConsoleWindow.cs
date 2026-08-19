namespace Accel.Cli;

using System.Runtime.InteropServices;

/// <summary>
/// Lets Accel.exe (built with <c>OutputType=WinExe</c> - see Accel.csproj - specifically so a
/// double-click/shortcut/Start-menu launch never gets an OS-allocated console at all) still write
/// to an already-open interactive console when one exists, by reattaching to it.
///
/// <para><b>Why not just hide a console after the fact (the previous approach).</b> A WinExe-subsystem
/// process never gets a console auto-allocated in the first place, which is what makes this
/// reliable - the previous approach (<c>OutputType=Exe</c> + <c>ShowWindow(GetConsoleWindow(),
/// SW_HIDE)</c>) does not actually hide anything on a machine where Windows Terminal is the
/// registered default terminal app (Windows 11's default): in that hosting model the visible window
/// belongs to a separate <c>wt.exe</c> process, and <c>GetConsoleWindow()</c> only ever returns
/// the invisible conhost pseudo-console handle behind it - <c>ShowWindow</c> on that handle has no
/// effect on the actually-visible window, so the console was reported as "still appearing" even
/// with that fix in place.</para>
///
/// <para><b>Why this still works for every other launch path.</b> A redirected/piped stdout (how
/// Claude Code invokes <c>accel statusline</c>/<c>notify</c> as short-lived hook child processes,
/// and how <c>--verbose</c> logs get captured by a caller) is set on the child's standard handles at
/// <c>CreateProcess</c> time regardless of subsystem - .NET's <see cref="System.Console"/> picks
/// that inherited handle straight up, WinExe or not, so those paths need no help from this class at
/// all. The one case a WinExe subsystem alone would leave silently broken is a user typing
/// <c>accel doctor</c> (or any dev verb) directly into an already-open PowerShell/cmd window with
/// no redirection - <see cref="AttachToParentConsoleIfPresent"/> reattaches Accel's stdout/stderr to
/// that already-visible parent console instead of getting nothing, without ever allocating (or
/// needing to hide) a console window of its own.</para>
/// </summary>
public static class ConsoleWindow
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    /// <summary>Best-effort: fails silently (returns false) when there is no parent console to
    /// attach to (double-click, shortcut, Start menu, or a piped/redirected launch) - that is the
    /// expected, common case, not an error.</summary>
    public static bool AttachToParentConsoleIfPresent() => AttachConsole(AttachParentProcess);
}

namespace Accel.Orchestration;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Rejects text before it is ever written to a live agent's stdin. This is the security control for
/// <see cref="SlashCommandDriver"/> (P4-T1): the PTY input pipe accepts arbitrary bytes, so anything
/// that reaches <see cref="PtySession.WriteText"/> unfiltered could inject a second command, an escape
/// sequence, or a Ctrl+C. Allowlist-shaped (reject known-dangerous, not "reject known-bad") per the
/// plan's explicit hostile-input table: embedded CR/LF, ESC, ETX (Ctrl+C), Unicode line separators
/// U+2028/U+2029, and over-long input.
/// </summary>
public static class SlashCommandInputSanitizer
{
    /// <summary>
    /// Conservative cap on a single token's length. Nothing this drives today (a display name) is
    /// remotely close to this, so it exists purely to close "over-long names" as a DoS/UI-corruption
    /// vector rather than to express any real product limit.
    /// </summary>
    public const int MaxLength = 200;

    /// <summary>
    /// Validates one token (the command itself, or one argument) before it may be written to a PTY.
    /// Rejects: <see langword="null"/>, longer than <see cref="MaxLength"/>, any ASCII control
    /// character (0x00-0x1F, 0x7F - which subsumes CR, LF, ESC, and ETX/Ctrl+C individually called out
    /// by the plan), and the Unicode line/paragraph separators U+2028/U+2029 (not covered by
    /// <see cref="char.IsControl(char)"/> but just as capable of desynchronizing a line-oriented
    /// injection).
    /// </summary>
    public static bool TryValidate(string? input, out string? rejectionReason)
    {
        if (input is null)
        {
            rejectionReason = "input is null";
            return false;
        }

        if (input.Length > MaxLength)
        {
            rejectionReason = $"input exceeds the {MaxLength}-character limit";
            return false;
        }

        foreach (char c in input)
        {
            if (char.IsControl(c) || c == '\u2028' || c == '\u2029')
            {
                rejectionReason = $"input contains a forbidden character (U+{(int)c:X4})";
                return false;
            }
        }

        rejectionReason = null;
        return true;
    }
}

/// <summary>How one <see cref="SlashCommandDriver.InvokeAsync"/> call ended.</summary>
public enum SlashCommandOutcome
{
    /// <summary>The completion predicate matched a polled status snapshot before the timeout elapsed.</summary>
    Completed,

    /// <summary>The command was written, but the completion predicate never matched within the timeout.
    /// The command may still have applied - only observation timed out, not necessarily execution
    /// (see the plan's "non-modal 'rename may not have applied' warning" for how P4-T2 surfaces this).</summary>
    TimedOut,

    /// <summary>The command or one of its arguments failed <see cref="SlashCommandInputSanitizer"/> and
    /// was never written to the session. <see cref="SlashCommandResult.RejectionReason"/> carries why.</summary>
    Rejected,
}

/// <summary>The result of one <see cref="SlashCommandDriver.InvokeAsync"/> call. Never thrown - mirrors
/// this codebase's <c>PtyCloseResult</c>/<c>PtyOrphanActionResult</c> convention of reporting outcomes
/// as data.</summary>
public sealed record SlashCommandResult(SlashCommandOutcome Outcome, string? RejectionReason, TimeSpan Elapsed)
{
    public bool Succeeded => Outcome == SlashCommandOutcome.Completed;
}

/// <summary>
/// P4-T1: the generic mechanism for driving a live `claude` session through its slash-command TUI by
/// writing sanitized text to its PTY input and polling <c>~/.claude/sessions/&lt;pid&gt;.json</c> for
/// the resulting state change - never by screen-scraping the terminal output, which the plan
/// explicitly bans (the TUI's rendering is not a contract; the status file is Claude Code's own
/// source of truth for whether a command has taken effect).
///
/// <para>First consumer is rename (P4-T2): <c>new SlashCommandDriver().InvokeAsync(session, "/rename",
/// new[] { displayName }, snap => snap?.Name == displayName, TimeSpan.FromSeconds(5))</c>. The driver
/// itself knows nothing about rename specifically - the completion predicate and the gate on "is this
/// session idle before we inject anything" are both the caller's job (P4-T2), so any future slash
/// command reuses this without touching it.</para>
/// </summary>
public sealed class SlashCommandDriver
{
    private readonly Func<int, ClaudeSessionStatusSnapshot?> _statusReader;
    private readonly TimeSpan _pollInterval;

    /// <param name="statusReader">Test seam: overrides how a pid's status snapshot is obtained. Production
    /// callers leave this null, which reads the real <c>~/.claude/sessions/&lt;pid&gt;.json</c> via
    /// <see cref="ClaudeSessionStatusFile.TryRead(int, string?)"/>.</param>
    /// <param name="pollInterval">Test seam: how often the status file is re-read while waiting. Defaults
    /// to 150ms, matching the responsiveness of a UI-facing wait without hammering the disk.</param>
    public SlashCommandDriver(Func<int, ClaudeSessionStatusSnapshot?>? statusReader = null, TimeSpan? pollInterval = null)
    {
        _statusReader = statusReader ?? (pid => ClaudeSessionStatusFile.TryRead(pid));
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(150);
    }

    /// <summary>
    /// Convenience overload for production callers: sanitizes and sends the command to a real
    /// <paramref name="session"/>, polling its own <see cref="PtySession.ProcessId"/>. Delegates to the
    /// core, process-agnostic overload below - see its remarks for the full contract.
    /// </summary>
    public Task<SlashCommandResult> InvokeAsync(
        PtySession session,
        string command,
        IReadOnlyList<string>? args,
        Func<ClaudeSessionStatusSnapshot?, bool> completionPredicate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return InvokeAsync(session.ProcessId, session.WriteText, command, args, completionPredicate, timeout, cancellationToken);
    }

    /// <summary>
    /// The core mechanism, decoupled from <see cref="PtySession"/> itself (which - like everything else
    /// backed by a real ConPTY/child process in this codebase - cannot be fabricated for a unit test; see
    /// <see cref="IPtySessionHost"/>'s remarks for the same split applied to the registry). Sanitizes
    /// <paramref name="command"/> and every element of <paramref name="args"/>, and - only if every token
    /// passes - calls <paramref name="writeText"/> with <c>"{command} {arg1} {arg2} ...\r"</c>, then polls
    /// <see cref="ClaudeSessionStatusFile.TryRead(int, string?)"/> (via the injected status reader) against
    /// <paramref name="completionPredicate"/> until it matches or <paramref name="timeout"/> elapses. Never
    /// throws for a rejected or timed-out input - see <see cref="SlashCommandOutcome"/>. Can throw only for
    /// what <paramref name="writeText"/> itself throws or for <paramref name="cancellationToken"/> cancellation.
    /// </summary>
    public async Task<SlashCommandResult> InvokeAsync(
        int processId,
        Action<string> writeText,
        string command,
        IReadOnlyList<string>? args,
        Func<ClaudeSessionStatusSnapshot?, bool> completionPredicate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeText);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(completionPredicate);

        if (!SlashCommandInputSanitizer.TryValidate(command, out string? commandRejection))
        {
            return new SlashCommandResult(SlashCommandOutcome.Rejected, $"command: {commandRejection}", TimeSpan.Zero);
        }

        args ??= Array.Empty<string>();
        foreach (string arg in args)
        {
            if (!SlashCommandInputSanitizer.TryValidate(arg, out string? argRejection))
            {
                return new SlashCommandResult(SlashCommandOutcome.Rejected, $"argument '{arg}': {argRejection}", TimeSpan.Zero);
            }
        }

        var stopwatch = Stopwatch.StartNew();
        writeText(BuildCommandLine(command, args));

        while (true)
        {
            var snapshot = _statusReader(processId);
            if (completionPredicate(snapshot))
            {
                return new SlashCommandResult(SlashCommandOutcome.Completed, null, stopwatch.Elapsed);
            }

            if (stopwatch.Elapsed >= timeout)
            {
                return new SlashCommandResult(SlashCommandOutcome.TimedOut, null, stopwatch.Elapsed);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string BuildCommandLine(string command, IReadOnlyList<string> args)
    {
        var builder = new StringBuilder(command);
        foreach (string arg in args)
        {
            builder.Append(' ').Append(arg);
        }

        builder.Append('\r');
        return builder.ToString();
    }
}

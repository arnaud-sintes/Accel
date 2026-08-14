namespace Glaude.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Glaude.Orchestration;
using Xunit;

/// <summary>
/// Unit tests for <see cref="SlashCommandInputSanitizer"/> and the process-agnostic
/// <see cref="SlashCommandDriver.InvokeAsync(int, Action{string}, string, IReadOnlyList{string}?, Func{ClaudeSessionStatusSnapshot?, bool}, TimeSpan, CancellationToken)"/>
/// overload - fully deterministic, no real PTY/process (see <see cref="IPtySessionHost"/>'s remarks on why
/// <see cref="PtySession"/> itself is never fabricated for a unit test). The real end-to-end path (writing
/// into an actual `claude` session and observing its real status file) is a smoke-test concern, not this file's.
/// </summary>
public class SlashCommandDriverTests
{
    // ---------------------------------------------------------------------------------------------
    // SlashCommandInputSanitizer - the hostile-input table the plan calls for explicitly.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("New Name")]
    [InlineData("a")]
    [InlineData("--fork-session")]
    [InlineData("session-with-dashes-and-123")]
    public void TryValidate_AcceptsOrdinaryInput(string input)
    {
        Assert.True(SlashCommandInputSanitizer.TryValidate(input, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void TryValidate_RejectsNull()
    {
        Assert.False(SlashCommandInputSanitizer.TryValidate(null, out var reason));
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("embedded\rCR")]
    [InlineData("embedded\nLF")]
    [InlineData("embedded\r\nCRLF")]
    [InlineData("escape\x1bsequence")]
    [InlineData("ctrl\u0003c")]
    [InlineData("line\u2028separator")]
    [InlineData("paragraph\u2029separator")]
    [InlineData("\x00")]
    [InlineData("\x7f")]
    [InlineData("tab\tcharacter")] // a real control character, even though it looks harmless
    public void TryValidate_RejectsHostileInput(string input)
    {
        Assert.False(SlashCommandInputSanitizer.TryValidate(input, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void TryValidate_RejectsOverLongInput()
    {
        string tooLong = new string('a', SlashCommandInputSanitizer.MaxLength + 1);
        Assert.False(SlashCommandInputSanitizer.TryValidate(tooLong, out var reason));
        Assert.Contains("limit", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidate_AcceptsInputExactlyAtTheLimit()
    {
        string atLimit = new string('a', SlashCommandInputSanitizer.MaxLength);
        Assert.True(SlashCommandInputSanitizer.TryValidate(atLimit, out _));
    }

    // ---------------------------------------------------------------------------------------------
    // InvokeAsync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_RejectsHostileCommand_WithoutWritingAnything()
    {
        var writes = new List<string>();
        var driver = new SlashCommandDriver(statusReader: _ => null);

        var result = await driver.InvokeAsync(
            processId: 123,
            writeText: writes.Add,
            command: "/rename\r\nsomething",
            args: Array.Empty<string>(),
            completionPredicate: _ => true,
            timeout: TimeSpan.FromSeconds(1));

        Assert.Equal(SlashCommandOutcome.Rejected, result.Outcome);
        Assert.Contains("command:", result.RejectionReason);
        Assert.Empty(writes);
    }

    [Fact]
    public async Task InvokeAsync_RejectsHostileArgument_WithoutWritingAnything()
    {
        var writes = new List<string>();
        var driver = new SlashCommandDriver(statusReader: _ => null);

        var result = await driver.InvokeAsync(
            processId: 123,
            writeText: writes.Add,
            command: "/rename",
            args: new[] { "evil\x03name" },
            completionPredicate: _ => true,
            timeout: TimeSpan.FromSeconds(1));

        Assert.Equal(SlashCommandOutcome.Rejected, result.Outcome);
        Assert.Contains("argument", result.RejectionReason);
        Assert.Empty(writes);
    }

    [Fact]
    public async Task InvokeAsync_WritesCommandAndArgsJoinedBySpaces_TerminatedWithCR()
    {
        string? written = null;
        var driver = new SlashCommandDriver(statusReader: _ => null, pollInterval: TimeSpan.FromMilliseconds(5));

        _ = await driver.InvokeAsync(
            processId: 123,
            writeText: text => written = text,
            command: "/rename",
            args: new[] { "New", "Name" },
            completionPredicate: _ => true, // completes immediately after the write
            timeout: TimeSpan.FromSeconds(1));

        Assert.Equal("/rename New Name\r", written);
    }

    [Fact]
    public async Task InvokeAsync_PollsTheInjectedProcessId_UntilPredicateMatches()
    {
        var seenPids = new List<int>();
        int callCount = 0;
        var driver = new SlashCommandDriver(
            statusReader: pid =>
            {
                seenPids.Add(pid);
                callCount++;
                return callCount >= 3
                    ? new ClaudeSessionStatusSnapshot(pid, null, "New Name", "derived", "idle", null)
                    : new ClaudeSessionStatusSnapshot(pid, null, "Old Name", "derived", "busy", null);
            },
            pollInterval: TimeSpan.FromMilliseconds(5));

        var result = await driver.InvokeAsync(
            processId: 999,
            writeText: _ => { },
            command: "/rename",
            args: new[] { "New Name" },
            completionPredicate: snap => snap?.Name == "New Name",
            timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(SlashCommandOutcome.Completed, result.Outcome);
        Assert.True(callCount >= 3);
        Assert.All(seenPids, pid => Assert.Equal(999, pid));
    }

    [Fact]
    public async Task InvokeAsync_TimesOut_WhenPredicateNeverMatches()
    {
        var driver = new SlashCommandDriver(statusReader: _ => null, pollInterval: TimeSpan.FromMilliseconds(5));

        var result = await driver.InvokeAsync(
            processId: 1,
            writeText: _ => { },
            command: "/rename",
            args: Array.Empty<string>(),
            completionPredicate: _ => false,
            timeout: TimeSpan.FromMilliseconds(50));

        Assert.Equal(SlashCommandOutcome.TimedOut, result.Outcome);
        Assert.True(result.Elapsed >= TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task InvokeAsync_RespectsCancellation()
    {
        using var cts = new CancellationTokenSource();
        var driver = new SlashCommandDriver(statusReader: _ => null, pollInterval: TimeSpan.FromMilliseconds(5));

        var task = driver.InvokeAsync(
            processId: 1,
            writeText: _ => cts.Cancel(),
            command: "/rename",
            args: Array.Empty<string>(),
            completionPredicate: _ => false,
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task InvokeAsync_NullArgs_IsTreatedAsNoArguments()
    {
        string? written = null;
        var driver = new SlashCommandDriver(statusReader: _ => null);

        await driver.InvokeAsync(
            processId: 1,
            writeText: text => written = text,
            command: "/help",
            args: null,
            completionPredicate: _ => true,
            timeout: TimeSpan.FromSeconds(1));

        Assert.Equal("/help\r", written);
    }

    // ---------------------------------------------------------------------------------------------
    // ClaudeSessionStatusFile
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ClaudeSessionStatusFile_TryRead_ReturnsNull_WhenFileMissing()
    {
        string homeDir = Path.Combine(Path.GetTempPath(), "glaude-status-test-" + Guid.NewGuid());
        Assert.Null(ClaudeSessionStatusFile.TryRead(12345, homeDir));
    }

    [Fact]
    public void ClaudeSessionStatusFile_TryRead_ParsesARealShapeFile()
    {
        string homeDir = Path.Combine(Path.GetTempPath(), "glaude-status-test-" + Guid.NewGuid());
        string sessionsDir = Path.Combine(homeDir, ".claude", "sessions");
        Directory.CreateDirectory(sessionsDir);
        try
        {
            string json = "{\"pid\":24764,\"sessionId\":\"ca217e05-08e6-4e38-bcc6-62a7510e1b97\"," +
                "\"cwd\":\"C:\\\\projects\",\"name\":\"projects-49\",\"nameSource\":\"derived\"," +
                "\"status\":\"idle\",\"updatedAt\":1786696329984}";
            File.WriteAllText(Path.Combine(sessionsDir, "24764.json"), json, Encoding.UTF8);

            var snapshot = ClaudeSessionStatusFile.TryRead(24764, homeDir);

            Assert.NotNull(snapshot);
            Assert.Equal(24764, snapshot!.Pid);
            Assert.Equal("ca217e05-08e6-4e38-bcc6-62a7510e1b97", snapshot.SessionId);
            Assert.Equal("projects-49", snapshot.Name);
            Assert.Equal("derived", snapshot.NameSource);
            Assert.Equal("idle", snapshot.Status);
            Assert.Equal(1786696329984, snapshot.UpdatedAt);
            Assert.True(ClaudeSessionStatusFile.IsIdle(snapshot));
        }
        finally
        {
            Directory.Delete(homeDir, recursive: true);
        }
    }

    [Fact]
    public void ClaudeSessionStatusFile_TryRead_DegradesToNull_OnMalformedJson()
    {
        string homeDir = Path.Combine(Path.GetTempPath(), "glaude-status-test-" + Guid.NewGuid());
        string sessionsDir = Path.Combine(homeDir, ".claude", "sessions");
        Directory.CreateDirectory(sessionsDir);
        try
        {
            File.WriteAllText(Path.Combine(sessionsDir, "1.json"), "{ not json", Encoding.UTF8);
            Assert.Null(ClaudeSessionStatusFile.TryRead(1, homeDir));
        }
        finally
        {
            Directory.Delete(homeDir, recursive: true);
        }
    }

    [Fact]
    public void ClaudeSessionStatusFile_IsIdle_FailsClosed_OnNullSnapshot()
    {
        Assert.False(ClaudeSessionStatusFile.IsIdle(null));
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("Idle")] // case must match exactly - no case-insensitive gate
    [InlineData("")]
    [InlineData("unknown-future-status")]
    public void ClaudeSessionStatusFile_IsIdle_FailsClosed_OnAnythingOtherThanExactIdleLiteral(string status)
    {
        var snapshot = new ClaudeSessionStatusSnapshot(1, null, null, null, status, null);
        Assert.False(ClaudeSessionStatusFile.IsIdle(snapshot));
    }
}

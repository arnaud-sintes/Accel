namespace Accel.Tests;

using Accel.Orchestration;
using Xunit;

/// <summary>
/// Unit tests for <see cref="TerminalScreenBuffer"/> - the minimal terminal emulation catalog
/// discovery needs. The behaviour worth pinning is not "escape sequences are stripped" but
/// "overwrites win": a TUI repaints by moving the cursor back and drawing over what was there, so a
/// buffer that merely dropped escapes would return every stale frame concatenated with the current
/// one, and the picker parser would then read rows that are no longer on screen.
/// </summary>
public class TerminalScreenBufferTests
{
    private const string Esc = "\u001b";

    [Fact]
    public void Render_PlainText_LandsOnTheFirstLine()
    {
        var screen = TerminalScreenBuffer.Render("hello", 10, 3);

        Assert.Equal(3, screen.Count);
        Assert.Equal("hello     ", screen[0]);
    }

    [Fact]
    public void Render_HonoursCarriageReturnAsAnOverwrite()
    {
        var screen = TerminalScreenBuffer.Render("aaaa\rbb", 6, 1);

        Assert.Equal("bbaa  ", screen[0]);
    }

    [Fact]
    public void Render_HonoursCursorPositioning()
    {
        // CUP to row 2, column 3, then write - the sequence a TUI uses constantly.
        var screen = TerminalScreenBuffer.Render($"{Esc}[2;3Hxy", 6, 3);

        Assert.Equal("      ", screen[0]);
        Assert.Equal("  xy  ", screen[1]);
    }

    /// <summary>The whole reason this class exists: a repaint must not leave the previous frame's
    /// text visible next to the new frame's.</summary>
    [Fact]
    public void Render_EraseInDisplay_ClearsTheStaleFrame()
    {
        var stream = $"stale row one\nstale row two{Esc}[H{Esc}[2Jfresh";

        var screen = TerminalScreenBuffer.Render(stream, 14, 3);

        Assert.Equal("fresh         ", screen[0]);
        Assert.Equal("              ", screen[1]);
    }

    [Fact]
    public void Render_EraseInLine_ClearsToTheEndOfTheLineOnly()
    {
        var screen = TerminalScreenBuffer.Render($"abcdef{Esc}[1;4H{Esc}[K", 6, 1);

        Assert.Equal("abc   ", screen[0]);
    }

    [Fact]
    public void Render_SgrColourSequences_LeaveNoVisibleCharacters()
    {
        var screen = TerminalScreenBuffer.Render($"{Esc}[1;31mred{Esc}[0m", 6, 1);

        Assert.Equal("red   ", screen[0]);
    }

    [Fact]
    public void Render_OperatingSystemCommand_IsConsumedEntirely()
    {
        // A window-title OSC, terminated by BEL - painted by Claude Code on startup. Its payload must
        // not leak into the screen as text.
        var screen = TerminalScreenBuffer.Render($"{Esc}]0;a title\aok", 6, 1);

        Assert.Equal("ok    ", screen[0]);
    }

    [Fact]
    public void Render_LineWrapsAtTheColumnLimit()
    {
        var screen = TerminalScreenBuffer.Render("abcdef", 3, 3);

        Assert.Equal("abc", screen[0]);
        Assert.Equal("def", screen[1]);
    }

    [Fact]
    public void Render_ScrollsWhenOutputExceedsTheRowCount()
    {
        // CRLF, not bare LF: a ConPTY emits both, and LF alone is a line feed that does NOT return
        // the cursor to column 0 - so writing the newlines the way the real stream writes them is
        // what makes this a test of scrolling rather than of an assumption about LF.
        var screen = TerminalScreenBuffer.Render("one\r\ntwo\r\nthree", 5, 2);

        // "one" has scrolled off the top.
        Assert.Equal("two  ", screen[0]);
        Assert.Equal("three", screen[1]);
    }

    [Fact]
    public void Render_EmptyStream_IsABlankScreenOfTheRequestedSize()
    {
        var screen = TerminalScreenBuffer.Render(string.Empty, 4, 2);

        Assert.Equal(2, screen.Count);
        Assert.All(screen, line => Assert.Equal("    ", line));
    }

    [Fact]
    public void Render_TruncatedEscapeSequenceAtEndOfStream_DoesNotThrow()
    {
        // The stream is a live capture, so it can be cut mid-sequence at any moment.
        Assert.Equal("ok  ", TerminalScreenBuffer.Render($"ok{Esc}[", 4, 1)[0]);
        Assert.Equal("ok  ", TerminalScreenBuffer.Render($"ok{Esc}", 4, 1)[0]);
        Assert.Equal("ok  ", TerminalScreenBuffer.Render($"ok{Esc}[12;", 4, 1)[0]);
    }

    [Fact]
    public void Render_CursorMovesBeyondTheScreen_AreClampedNotThrown()
    {
        var screen = TerminalScreenBuffer.Render($"{Esc}[99;99Hx", 4, 2);

        Assert.Equal(2, screen.Count);
        Assert.Contains("x", screen[1]);
    }
}

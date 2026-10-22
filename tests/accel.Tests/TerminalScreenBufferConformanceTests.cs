namespace Accel.Tests;

using System.Linq;
using Accel.Orchestration;
using Xunit;

/// <summary>
/// One case per escape sequence the emulator is expected to honour. A TUI repaints only the cells it
/// believes changed and skips the rest with cursor-forward, so any sequence the emulator ignores or
/// prints leaves stale text in exactly the skipped cells - which is how a model name ended up as
/// "Sonnet 5.5up". These pin the behaviour of a real terminal for each one.
/// </summary>
public class TerminalScreenBufferConformanceTests
{
    private const string Esc = "\u001b";

    private static string[] Screen(string stream, int columns = 20, int rows = 5) =>
        TerminalScreenBuffer.Render(stream, columns, rows).Select(l => l.TrimEnd()).ToArray();

    private static string Row0(string stream, int columns = 20) => Screen(stream, columns)[0];

    // --- nothing between ESC [ and the final byte is ever printed -----------------------------------------

    [Theory]
    [InlineData("[>0q")]          // XTVERSION
    [InlineData("[>4;1m")]        // modifyOtherKeys
    [InlineData("[<u")]           // kitty keyboard pop
    [InlineData("[=5u")]
    [InlineData("[?u")]           // kitty keyboard query
    [InlineData("[2 q")]          // DECSCUSR (intermediate byte)
    [InlineData("[?2026$p")]      // DECRQM
    [InlineData("[!p")]           // DECSTR
    [InlineData("[38:2::255:0:0m")] // colon sub-parameters
    [InlineData("(B")]            // charset designation
    [InlineData(")0")]
    [InlineData("#8")]
    [InlineData("%G")]
    [InlineData("=")]
    [InlineData(">")]
    public void SequencesWithNoScreenEffect_PrintNothing(string body)
    {
        Assert.Equal("abcd", Row0("ab" + Esc + body + "cd"));
    }

    [Fact]
    public void ASequenceCutOffByAnotherEscape_IsAbandoned_AndTheNewOneApplies()
    {
        Assert.Equal("ab c", Row0("ab" + Esc + "[3" + Esc + "[1Cc"));
    }

    [Fact]
    public void AControlCharacterInsideACsi_DoesNotEndItOrPrint()
    {
        Assert.Equal("a  b", Row0("a" + Esc + "[2\u0007C" + "b"));
    }

    // --- cursor movement ----------------------------------------------------------------------------------

    [Fact]
    public void CursorForward_SkipsCells_LeavingTheirContent()
    {
        // The partial-redraw idiom: the second paint skips a cell it believes is already right.
        Assert.Equal("abXdX", Row0("XXXXX\rab" + Esc + "[1Cd"));
    }

    [Theory]
    [InlineData("[3G", 2)]
    [InlineData("[3`", 2)]
    [InlineData("[2C", 2)]
    [InlineData("[2a", 2)]
    public void HorizontalMoves_LandOnTheRightColumn(string move, int column)
    {
        Assert.Equal(new string(' ', column) + "x", Row0("\r" + Esc + move + "x"));
    }

    [Fact]
    public void VerticalAbsoluteAndRelativeMoves_LandOnTheRightRow()
    {
        Assert.Equal("x", Screen(Esc + "[3dx")[2]);                       // VPA: absolute row 3
        Assert.Equal("x", Screen(Esc + "[4;1H" + Esc + "[2Ax")[1]);       // CUU 2 from row 4
        Assert.Equal("x", Screen(Esc + "[2ex")[2]);                       // VPR: 2 rows down from row 1
    }

    [Fact]
    public void NextAndPreviousLine_MoveToColumnZero()
    {
        Assert.Equal("x", Screen("ab" + Esc + "[1Ex")[1]);                // CNL: next line, column 1
        Assert.Equal("xb", Screen("ab\r\ncd" + Esc + "[1Fx")[0]);         // CPL: previous line, column 1
    }

    [Fact]
    public void SaveAndRestoreCursor_BothForms()
    {
        Assert.Equal("abXd", Row0("ab" + Esc + "7cd" + Esc + "8X"));
        Assert.Equal("abXd", Row0("ab" + Esc + "[scd" + Esc + "[uX"));
    }

    [Fact]
    public void TabForwardAndBack()
    {
        Assert.Equal("a       b", Row0("a" + Esc + "[1Ib"));           // CHT: to the next tab stop (column 9)
        Assert.Equal("a       c", Row0("a\tb" + Esc + "[1Zc"));         // CBT: back to the stop "b" sits on
    }

    // --- erasing and editing within a line ------------------------------------------------------------------

    [Fact]
    public void EraseCharacters_BlanksInPlace_WithoutMovingTheCursorOrShiftingText()
    {
        // "hello" is blanked; the space after it and "world" are untouched.
        Assert.Equal("      world", Row0("hello world\r" + Esc + "[5X"));
    }

    [Fact]
    public void DeleteCharacters_ShiftsTheRestOfTheLineLeft()
    {
        Assert.Equal("abef", Row0("abcdef\r" + Esc + "[2C" + Esc + "[2P"));
    }

    [Fact]
    public void InsertCharacters_ShiftsTheRestOfTheLineRight()
    {
        Assert.Equal("ab  cdef", Row0("abcdef\r" + Esc + "[2C" + Esc + "[2@"));
    }

    [Fact]
    public void EditsNearTheRightEdge_AreClamped_NotThrown()
    {
        // Deleting more than is left blanks the rest of the line; inserting or erasing at column 7 of 8
        // can only touch the last two cells.
        Assert.Equal("abc", Row0("abcdefgh" + Esc + "[1;4H" + Esc + "[99P", columns: 8));
        Assert.Equal("abcdef", Row0("abcdefgh" + Esc + "[1;7H" + Esc + "[99@", columns: 8));
        Assert.Equal("abcdef", Row0("abcdefgh" + Esc + "[1;7H" + Esc + "[99X", columns: 8));
    }

    [Fact]
    public void RepeatCharacter_RepeatsTheLastPrintedOne()
    {
        Assert.Equal("aaaa", Row0("a" + Esc + "[3b"));
    }

    [Theory]
    [InlineData(0, "hello")]        // cursor to end of line
    [InlineData(1, "      ----")]   // start of line through the cursor, inclusive
    [InlineData(2, "")]             // whole line
    public void EraseInLine_Modes(int mode, string expected)
    {
        // "hello-----" with the cursor parked on column 6 (the first dash)
        Assert.Equal(expected, Row0("hello-----" + Esc + "[1;6H" + Esc + $"[{mode}K"));
    }

    // --- lines and scrolling -------------------------------------------------------------------------------

    [Fact]
    public void InsertAndDeleteLines_ShiftTheRowsBelow()
    {
        string frame = "a\r\nb\r\nc" + Esc + "[2;1H";
        Assert.Equal(new[] { "a", "", "b", "c", "" }, Screen(frame + Esc + "[1L"));
        Assert.Equal(new[] { "a", "c", "", "", "" }, Screen(frame + Esc + "[1M"));
    }

    [Fact]
    public void ScrollUpAndDown_MoveTheWholeScreen()
    {
        string frame = "a\r\nb\r\nc";
        Assert.Equal(new[] { "b", "c", "", "", "" }, Screen(frame + Esc + "[1S"));
        Assert.Equal(new[] { "", "a", "b", "c", "" }, Screen(frame + Esc + "[1T"));
    }

    [Fact]
    public void ReverseIndexAtTheTop_ScrollsDown()
    {
        Assert.Equal(new[] { "", "a", "", "", "" }, Screen("a" + Esc + "[1;1H" + Esc + "M"));
    }

    [Fact]
    public void ScrollMargins_ConfineScrolling()
    {
        // Rows A..D, margins 2-3, a line feed on row 3 scrolls only rows 2-3.
        string stream = "A\r\nB\r\nC\r\nD" + Esc + "[2;3r" + Esc + "[3;1H\n";
        Assert.Equal(new[] { "A", "C", "", "D" }, TerminalScreenBuffer.Render(stream, 10, 4).Select(l => l.TrimEnd()).ToArray());
    }

    // --- screens and modes ---------------------------------------------------------------------------------

    [Fact]
    public void EnteringTheAlternateScreen_GivesAFreshBlankGrid()
    {
        Assert.Equal("new", Row0("old text here" + Esc + "[?1049h" + "new"));
        Assert.Equal("new", Row0("old text here" + Esc + "[?47h" + "new"));
    }

    [Fact]
    public void FullReset_ClearsTheScreen()
    {
        Assert.Equal("z", Row0("old" + Esc + "cz"));
    }

    // --- characters ----------------------------------------------------------------------------------------

    [Fact]
    public void WideCharacters_TakeTwoCells()
    {
        // "日本" fills columns 0-3, so a CHA to column 5 lands directly after them.
        Assert.Equal("日本y", Row0("日本\r" + Esc + "[5Gy"));
    }

    [Fact]
    public void CombiningMarks_TakeNoCell()
    {
        Assert.Equal("ez", Row0("é\r" + Esc + "[2Gz"));
    }

    [Fact]
    public void AstralCharacters_TakeOneOrTwoCells_NotTwoFromTheSurrogatePair()
    {
        // An emoji (wide) takes two cells; the cursor must not advance by 2 *units* of the pair on top.
        Assert.EndsWith("y", Row0("\U0001F600\r" + Esc + "[3Gy"));
    }

    [Fact]
    public void LineWrap_IsLazy_SoACursorMoveAtTheLastColumnDoesNotWrapEarly()
    {
        var screen = Screen("12345" + Esc + "[1;5Hx", columns: 5);
        Assert.Equal("1234x", screen[0]);
        Assert.Equal(string.Empty, screen[1]);
    }

    // --- the bug this guards against, end to end --------------------------------------------------------------

    [Fact]
    public void PartialRepaint_ThatBlanksStaleCellsWithEraseCharacters_LeavesNoResidue()
    {
        // Frame 1 painted "Sonnet 5.5up"; frame 2 repaints the row, blanking the two stale cells.
        Assert.Equal("Sonnet 5.5", Row0("Sonnet 5.5up\rSonnet 5.5" + Esc + "[2X"));
    }

    // --- diagnostics -----------------------------------------------------------------------------------------

    [Fact]
    public void UnknownCellAffectingSequences_AreCounted_KnownOnesAreNot()
    {
        var clean = TerminalScreenBuffer.RenderDetailed("a" + Esc + "[1;31mb" + Esc + "[?25l" + Esc + "[2K" + Esc + "(B", 20, 5);
        Assert.Equal(0, clean.UnsupportedSequences);

        var unknown = TerminalScreenBuffer.RenderDetailed("a" + Esc + "[5vb" + Esc + "[3~", 20, 5);
        Assert.Equal(2, unknown.UnsupportedSequences);
    }

    [Fact]
    public void ArbitraryGarbage_NeverThrows()
    {
        var random = new System.Random(12345);
        for (int run = 0; run < 200; run++)
        {
            var chars = new char[random.Next(1, 200)];
            for (int i = 0; i < chars.Length; i++)
            {
                chars[i] = random.Next(0, 6) == 0 ? '\u001b' : (char)random.Next(0x20, 0x7F);
            }

            var render = TerminalScreenBuffer.RenderDetailed(new string(chars), random.Next(1, 40), random.Next(1, 12));
            Assert.NotEmpty(render.Lines);
        }
    }
}

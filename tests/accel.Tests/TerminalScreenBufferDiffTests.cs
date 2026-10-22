namespace Accel.Tests;

using System;
using System.Linq;
using System.Text;
using Accel.Orchestration;
using Xunit;

/// <summary>
/// ConPTY does not repaint a screen: it emits the <i>difference</i> from the one it believes is on the
/// terminal - cursor moves, cursor-forward over cells it thinks are unchanged, only the changed cells
/// written. So the emulator must reproduce the console's grid exactly, or the skipped cells show stale text
/// (the cause of "Sonnet 5.5up"). This test builds that kind of stream from random screen pairs and
/// requires the emulator to land on exactly the target screen, whichever technique blanks a cell.
/// </summary>
public class TerminalScreenBufferDiffTests
{
    private const string Esc = "\u001b";
    private const int Columns = 40;
    private const int Rows = 12;

    private enum BlankStyle
    {
        Spaces,
        EraseCharacters,
        EraseToEndOfLine,
    }

    [Fact]
    public void ADiffStream_ReproducesTheTargetScreenExactly_ForRandomScreens()
    {
        var random = new Random(20261022);

        for (var iteration = 0; iteration < 300; iteration++)
        {
            var previous = RandomScreen(random);
            var next = Mutate(previous, random);
            var style = (BlankStyle)random.Next(0, 3);

            var stream = new StringBuilder();
            stream.Append(Esc).Append("[2J").Append(Esc).Append("[H");
            stream.Append(Paint(previous));
            var afterFirstPaint = Rendered(stream.ToString());
            Assert.Equal(previous, afterFirstPaint);

            stream.Append(Diff(previous, next, style));

            Assert.True(
                next.SequenceEqual(Rendered(stream.ToString())),
                $"iteration {iteration}, blank style {style}: the emulator's grid diverged from the target screen");
        }
    }

    [Fact]
    public void TheSameDiffs_StaySupported_NoSequenceIsLeftUnapplied()
    {
        var random = new Random(7);
        var previous = RandomScreen(random);
        var next = Mutate(previous, random);

        var render = TerminalScreenBuffer.RenderDetailed(Paint(previous) + Diff(previous, next, BlankStyle.EraseCharacters), Columns, Rows);

        Assert.Equal(0, render.UnsupportedSequences);
    }

    // --- the encoder, standing in for conhost ----------------------------------------------------------------

    private static string[] Rendered(string stream) =>
        TerminalScreenBuffer.Render(stream, Columns, Rows).ToArray();

    private static string Paint(string[] screen)
    {
        var text = new StringBuilder();
        for (var row = 0; row < Rows; row++)
        {
            text.Append(Esc).Append('[').Append(row + 1).Append(";1H").Append(screen[row].TrimEnd());
        }

        return text.ToString();
    }

    private static string Diff(string[] previous, string[] next, BlankStyle blankStyle)
    {
        var text = new StringBuilder();
        for (var row = 0; row < Rows; row++)
        {
            if (previous[row] == next[row])
            {
                continue;
            }

            text.Append(Esc).Append('[').Append(row + 1).Append(";1H");
            var column = 0;
            while (column < Columns)
            {
                if (previous[row][column] == next[row][column])
                {
                    // Skip the run of unchanged cells with cursor-forward, as conhost does - even inside a word.
                    var run = 0;
                    while (column + run < Columns && previous[row][column + run] == next[row][column + run])
                    {
                        run++;
                    }

                    if (column + run < Columns)
                    {
                        text.Append(Esc).Append('[').Append(run).Append('C');
                    }

                    column += run;
                    continue;
                }

                if (next[row][column] == ' ' && blankStyle != BlankStyle.Spaces)
                {
                    var blanks = 0;
                    while (column + blanks < Columns && next[row][column + blanks] == ' ')
                    {
                        blanks++;
                    }

                    var restIsBlank = column + blanks == Columns;
                    if (blankStyle == BlankStyle.EraseToEndOfLine && restIsBlank)
                    {
                        text.Append(Esc).Append("[K");
                        break;
                    }

                    text.Append(Esc).Append('[').Append(blanks).Append('X');
                    if (!restIsBlank)
                    {
                        text.Append(Esc).Append('[').Append(blanks).Append('C');
                    }

                    column += blanks;
                    continue;
                }

                text.Append(next[row][column]);
                column++;
            }
        }

        return text.ToString();
    }

    private static string[] RandomScreen(Random random)
    {
        var screen = new string[Rows];
        for (var row = 0; row < Rows; row++)
        {
            var cells = new char[Columns];
            Array.Fill(cells, ' ');
            if (random.Next(0, 4) != 0)
            {
                var start = random.Next(0, 10);
                var length = random.Next(1, Columns - start);
                for (var i = 0; i < length; i++)
                {
                    cells[start + i] = random.Next(0, 6) == 0 ? ' ' : (char)random.Next('a', 'z' + 1);
                }
            }

            screen[row] = new string(cells);
        }

        return screen;
    }

    private static string[] Mutate(string[] screen, Random random)
    {
        var next = (string[])screen.Clone();
        for (var changes = random.Next(1, 6); changes > 0; changes--)
        {
            var row = random.Next(0, Rows);
            var cells = next[row].ToCharArray();
            var start = random.Next(0, Columns - 1);
            var length = random.Next(1, Math.Min(12, Columns - start));
            for (var i = 0; i < length; i++)
            {
                cells[start + i] = random.Next(0, 3) == 0 ? ' ' : (char)random.Next('A', 'Z' + 1);
            }

            next[row] = new string(cells);
        }

        return next;
    }
}

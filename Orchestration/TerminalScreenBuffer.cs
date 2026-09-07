namespace Accel.Orchestration;

using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// The minimum terminal emulator needed to see what the user sees. A TUI does not emit its screen
/// as text — it emits cursor moves and erases and overwrites cells, so naively stripping escape
/// sequences from the byte stream yields every intermediate frame concatenated (stale rows from a
/// previous paint sitting next to current ones), which is unparseable for reasons that have nothing
/// to do with the picker itself. Honouring CUP/ED/EL/CR/LF is what makes a parse failure here mean
/// something. Deliberately partial: no scroll regions, no tab stops, no wide characters.
/// </summary>
public static class TerminalScreenBuffer
{
    public static IReadOnlyList<string> Render(string stream, int columns, int rows)
    {
        var grid = new char[rows][];
        for (var r = 0; r < rows; r++)
        {
            grid[r] = new char[columns];
            Array.Fill(grid[r], ' ');
        }

        var row = 0;
        var column = 0;

        for (var i = 0; i < stream.Length; i++)
        {
            var c = stream[i];

            if (c == '\u001b')
            {
                i = HandleEscape(stream, i, grid, columns, rows, ref row, ref column);
                continue;
            }

            switch (c)
            {
                case '\r':
                    column = 0;
                    continue;
                case '\n':
                    row++;
                    if (row >= rows)
                    {
                        Scroll(grid, columns, rows);
                        row = rows - 1;
                    }

                    continue;
                case '\b':
                    column = Math.Max(0, column - 1);
                    continue;
                case '\t':
                    column = Math.Min(columns - 1, ((column / 8) + 1) * 8);
                    continue;
            }

            if (char.IsControl(c))
            {
                continue;
            }

            if (column >= columns)
            {
                column = 0;
                row++;
                if (row >= rows)
                {
                    Scroll(grid, columns, rows);
                    row = rows - 1;
                }
            }

            grid[row][column] = c;
            column++;
        }

        var result = new List<string>(rows);
        foreach (var line in grid)
        {
            result.Add(new string(line));
        }

        return result;
    }

    /// <summary>Consumes one escape sequence starting at <paramref name="start"/>, applying the few
    /// that move the cursor or erase. Returns the index of its final character.</summary>
    private static int HandleEscape(
        string stream, int start, char[][] grid, int columns, int rows, ref int row, ref int column)
    {
        if (start + 1 >= stream.Length)
        {
            return start;
        }

        var next = stream[start + 1];

        // OSC (]) and DCS/PM/APC run until BEL or ST - window titles and the like, no screen effect.
        if (next == ']' || next == 'P' || next == '^' || next == '_')
        {
            for (var i = start + 2; i < stream.Length; i++)
            {
                if (stream[i] == '\a')
                {
                    return i;
                }

                if (stream[i] == '\u001b' && i + 1 < stream.Length && stream[i + 1] == '\\')
                {
                    return i + 1;
                }
            }

            return stream.Length - 1;
        }

        if (next != '[')
        {
            return start + 1; // two-character sequence (ESC c, ESC =, ...) - ignored
        }

        var cursor = start + 2;
        var parameters = new StringBuilder();
        while (cursor < stream.Length && (char.IsAsciiDigit(stream[cursor]) || stream[cursor] == ';' || stream[cursor] == '?'))
        {
            parameters.Append(stream[cursor]);
            cursor++;
        }

        if (cursor >= stream.Length)
        {
            return stream.Length - 1;
        }

        var final = stream[cursor];
        var args = ParseParameters(parameters.ToString());

        switch (final)
        {
            case 'H':
            case 'f':
                row = Math.Clamp(Arg(args, 0, 1) - 1, 0, rows - 1);
                column = Math.Clamp(Arg(args, 1, 1) - 1, 0, columns - 1);
                break;

            case 'A':
                row = Math.Max(0, row - Arg(args, 0, 1));
                break;
            case 'B':
                row = Math.Min(rows - 1, row + Arg(args, 0, 1));
                break;
            case 'C':
                column = Math.Min(columns - 1, column + Arg(args, 0, 1));
                break;
            case 'D':
                column = Math.Max(0, column - Arg(args, 0, 1));
                break;

            case 'G':
                column = Math.Clamp(Arg(args, 0, 1) - 1, 0, columns - 1);
                break;

            case 'J':
                EraseInDisplay(grid, columns, rows, row, column, Arg(args, 0, 0));
                break;

            case 'K':
                EraseInLine(grid, columns, row, column, Arg(args, 0, 0));
                break;
        }

        return cursor;
    }

    private static int[] ParseParameters(string parameters)
    {
        if (parameters.Length == 0)
        {
            return Array.Empty<int>();
        }

        var parts = parameters.Replace("?", string.Empty).Split(';');
        var values = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            values[i] = int.TryParse(parts[i], out var parsed) ? parsed : 0;
        }

        return values;
    }

    private static int Arg(int[] args, int index, int fallback) =>
        index < args.Length && args[index] > 0 ? args[index] : fallback;

    private static void EraseInLine(char[][] grid, int columns, int row, int column, int mode)
    {
        var (from, to) = mode switch
        {
            1 => (0, column),
            2 => (0, columns - 1),
            _ => (column, columns - 1),
        };

        for (var c = from; c <= to && c < columns; c++)
        {
            grid[row][c] = ' ';
        }
    }

    private static void EraseInDisplay(char[][] grid, int columns, int rows, int row, int column, int mode)
    {
        switch (mode)
        {
            case 0:
                EraseInLine(grid, columns, row, column, 0);
                for (var r = row + 1; r < rows; r++)
                {
                    Array.Fill(grid[r], ' ');
                }

                break;

            case 1:
                EraseInLine(grid, columns, row, column, 1);
                for (var r = 0; r < row; r++)
                {
                    Array.Fill(grid[r], ' ');
                }

                break;

            default:
                foreach (var line in grid)
                {
                    Array.Fill(line, ' ');
                }

                break;
        }
    }

    private static void Scroll(char[][] grid, int columns, int rows)
    {
        var first = grid[0];
        for (var r = 0; r < rows - 1; r++)
        {
            grid[r] = grid[r + 1];
        }

        Array.Fill(first, ' ');
        grid[rows - 1] = first;
    }
}

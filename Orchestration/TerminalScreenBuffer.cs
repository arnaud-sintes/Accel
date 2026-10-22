namespace Accel.Orchestration;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>What <see cref="TerminalScreenBuffer.RenderDetailed"/> produced: the screen's rows, and how many
/// escape sequences the emulator saw but does not implement.</summary>
/// <param name="Lines">One string per screen row, trailing cells included (blank = space).</param>
/// <param name="UnsupportedSequences">Count of sequences that would have changed cells or the cursor on a
/// real terminal but are not implemented here. Non-zero means the grid may no longer match the real
/// screen - and since the TUI repaints only the cells it believes changed, a mismatch shows up as stale
/// text in the skipped cells - so a caller deciding whether to trust what it read must treat the render
/// as untrusted.</param>
public sealed record ScreenRender(IReadOnlyList<string> Lines, int UnsupportedSequences);

/// <summary>
/// The terminal emulator needed to see what the user sees. A TUI does not emit its screen as text - it
/// emits cursor moves, erases and cursor-forward <i>skips</i> over cells it believes are already right,
/// and overwrites the rest - so the screen we read is only correct if this grid matches the real
/// terminal's exactly. Naively stripping escape sequences yields every intermediate frame concatenated,
/// and a <i>partial</i> emulator is subtly worse: whatever it fails to apply leaves stale text in
/// precisely the cells the TUI skipped. So this follows the real grammar (parameter bytes, intermediate
/// bytes, final byte - a sequence's bytes are never printed), implements everything that moves the cursor
/// or changes cells, and <b>counts</b> what it cannot implement instead of silently ignoring it.
///
/// <para>Implemented: C0 (CR LF BS HT VT FF), CUP/HVP CUU CUD CUF CUB CNL CPL CHA/HPA HPR VPA VPR CHT CBT,
/// ED EL ECH DCH ICH IL DL SU SD REP, DECSTBM scroll margins, save/restore cursor (ESC 7/8, CSI s/u),
/// index/reverse index/next line, RIS, alternate-screen entry (a fresh blank grid), zero- and double-width
/// characters and lazy autowrap. Not modelled: SGR (no attributes are kept), scrollback, tab stops other
/// than every eighth column, origin mode.</para>
/// </summary>
public static class TerminalScreenBuffer
{
    public static IReadOnlyList<string> Render(string stream, int columns, int rows) =>
        RenderDetailed(stream, columns, rows).Lines;

    public static ScreenRender RenderDetailed(string stream, int columns, int rows)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        var emulator = new Emulator(columns, rows);
        emulator.Feed(stream);
        return emulator.Snapshot();
    }

    private sealed class Emulator
    {
        private readonly int _columns;
        private readonly int _rows;
        private readonly char[][] _grid;

        private int _row;
        private int _column;       // may equal _columns: autowrap is lazy, the wrap happens on the next print
        private int _top;
        private int _bottom;
        private int _savedRow;
        private int _savedColumn;
        private int _lastCodePoint = ' ';
        private int _unsupported;

        public Emulator(int columns, int rows)
        {
            _columns = columns;
            _rows = rows;
            _bottom = rows - 1;
            _grid = new char[rows][];
            for (var r = 0; r < rows; r++)
            {
                _grid[r] = new char[columns];
                Array.Fill(_grid[r], ' ');
            }
        }

        public ScreenRender Snapshot()
        {
            var lines = new List<string>(_rows);
            foreach (var row in _grid)
            {
                // A wide character occupies two cells; its second cell holds '\0' and has no glyph.
                lines.Add(new string(row).Replace("\0", string.Empty, StringComparison.Ordinal));
            }

            return new ScreenRender(lines, _unsupported);
        }

        /// <summary>The cursor column clamped onto the screen - what an operation "at the cursor" acts on
        /// while a wrap is pending.</summary>
        private int Cx => Math.Min(_column, _columns - 1);

        public void Feed(string stream)
        {
            for (var i = 0; i < stream.Length; i++)
            {
                var c = stream[i];

                if (c == '\u001b')
                {
                    i = HandleEscape(stream, i);
                    continue;
                }

                switch (c)
                {
                    case '\r':
                        _column = 0;
                        continue;
                    case '\n':
                    case '\u000b':
                    case '\u000c':
                        LineFeed();
                        continue;
                    case '\b':
                        _column = Math.Max(0, Cx - 1);
                        continue;
                    case '\t':
                        _column = Math.Min(_columns - 1, ((Cx / 8) + 1) * 8);
                        continue;
                }

                if (char.IsControl(c))
                {
                    continue;
                }

                int codePoint = c;
                if (char.IsHighSurrogate(c) && i + 1 < stream.Length && char.IsLowSurrogate(stream[i + 1]))
                {
                    codePoint = char.ConvertToUtf32(c, stream[i + 1]);
                    i++;
                }

                Print(codePoint);
            }
        }

        // --- printing -------------------------------------------------------------------------------------

        private void Print(int codePoint)
        {
            var width = Width(codePoint);
            if (width == 0)
            {
                return; // a combining mark / zero-width joiner / variation selector takes no cell
            }

            _lastCodePoint = codePoint;

            if (_column + width > _columns)
            {
                _column = 0;
                LineFeed();
            }

            _grid[_row][_column] = codePoint <= 0xFFFF ? (char)codePoint : '□';
            if (width == 2 && _column + 1 < _columns)
            {
                _grid[_row][_column + 1] = '\0';
            }

            _column += width;
        }

        private static int Width(int codePoint)
        {
            if (codePoint < 0x300)
            {
                return 1;
            }

            if (codePoint <= 0xFFFF)
            {
                switch (CharUnicodeInfo.GetUnicodeCategory((char)codePoint))
                {
                    case UnicodeCategory.NonSpacingMark:
                    case UnicodeCategory.EnclosingMark:
                    case UnicodeCategory.Format:
                        return 0;
                }
            }

            return IsWide(codePoint) ? 2 : 1;
        }

        // The East Asian Wide / Fullwidth blocks plus emoji presentation - the ranges that matter for text a
        // hook, plugin or project name can put on screen.
        private static bool IsWide(int cp) =>
            (cp >= 0x1100 && cp <= 0x115F) ||
            (cp >= 0x2E80 && cp <= 0x303E) ||
            (cp >= 0x3041 && cp <= 0x33FF) ||
            (cp >= 0x3400 && cp <= 0x4DBF) ||
            (cp >= 0x4E00 && cp <= 0x9FFF) ||
            (cp >= 0xA000 && cp <= 0xA4CF) ||
            (cp >= 0xAC00 && cp <= 0xD7A3) ||
            (cp >= 0xF900 && cp <= 0xFAFF) ||
            (cp >= 0xFE30 && cp <= 0xFE6F) ||
            (cp >= 0xFF00 && cp <= 0xFF60) ||
            (cp >= 0xFFE0 && cp <= 0xFFE6) ||
            (cp >= 0x1F300 && cp <= 0x1F64F) ||
            (cp >= 0x1F900 && cp <= 0x1F9FF) ||
            (cp >= 0x20000 && cp <= 0x3FFFD);

        // --- vertical movement and scrolling --------------------------------------------------------------

        private void LineFeed()
        {
            if (_row == _bottom)
            {
                ScrollUp(_top, _bottom, 1);
            }
            else if (_row < _rows - 1)
            {
                _row++;
            }
        }

        private void ReverseIndex()
        {
            if (_row == _top)
            {
                ScrollDown(_top, _bottom, 1);
            }
            else if (_row > 0)
            {
                _row--;
            }
        }

        private void ScrollUp(int top, int bottom, int count)
        {
            count = Math.Min(count, bottom - top + 1);
            for (var n = 0; n < count; n++)
            {
                var first = _grid[top];
                for (var r = top; r < bottom; r++)
                {
                    _grid[r] = _grid[r + 1];
                }

                Array.Fill(first, ' ');
                _grid[bottom] = first;
            }
        }

        private void ScrollDown(int top, int bottom, int count)
        {
            count = Math.Min(count, bottom - top + 1);
            for (var n = 0; n < count; n++)
            {
                var last = _grid[bottom];
                for (var r = bottom; r > top; r--)
                {
                    _grid[r] = _grid[r - 1];
                }

                Array.Fill(last, ' ');
                _grid[top] = last;
            }
        }

        // --- erasing --------------------------------------------------------------------------------------

        private void EraseInLine(int mode)
        {
            var (from, to) = mode switch
            {
                1 => (0, Cx),
                2 => (0, _columns - 1),
                _ => (Cx, _columns - 1),
            };

            for (var c = from; c <= to; c++)
            {
                _grid[_row][c] = ' ';
            }
        }

        private void EraseInDisplay(int mode)
        {
            switch (mode)
            {
                case 0:
                    EraseInLine(0);
                    for (var r = _row + 1; r < _rows; r++)
                    {
                        Array.Fill(_grid[r], ' ');
                    }

                    break;

                case 1:
                    EraseInLine(1);
                    for (var r = 0; r < _row; r++)
                    {
                        Array.Fill(_grid[r], ' ');
                    }

                    break;

                default: // 2 = whole screen, 3 = whole screen + scrollback (which is not modelled)
                    foreach (var line in _grid)
                    {
                        Array.Fill(line, ' ');
                    }

                    break;
            }
        }

        private void EraseCharacters(int count)
        {
            var end = Math.Min(_columns, Cx + count);
            for (var c = Cx; c < end; c++)
            {
                _grid[_row][c] = ' ';
            }
        }

        private void DeleteCharacters(int count)
        {
            var line = _grid[_row];
            var at = Cx;
            count = Math.Min(count, _columns - at);
            Array.Copy(line, at + count, line, at, _columns - at - count);
            Array.Fill(line, ' ', _columns - count, count);
        }

        private void InsertCharacters(int count)
        {
            var line = _grid[_row];
            var at = Cx;
            count = Math.Min(count, _columns - at);
            Array.Copy(line, at, line, at + count, _columns - at - count);
            Array.Fill(line, ' ', at, count);
        }

        private void InsertLines(int count)
        {
            if (_row >= _top && _row <= _bottom)
            {
                ScrollDown(_row, _bottom, count);
                _column = 0;
            }
        }

        private void DeleteLines(int count)
        {
            if (_row >= _top && _row <= _bottom)
            {
                ScrollUp(_row, _bottom, count);
                _column = 0;
            }
        }

        private void ClearAll()
        {
            foreach (var line in _grid)
            {
                Array.Fill(line, ' ');
            }

            _row = 0;
            _column = 0;
        }

        // --- escape sequences -----------------------------------------------------------------------------

        /// <summary>Consumes one escape sequence starting at <paramref name="start"/>; returns the index of
        /// its last character (or, for a sequence cut off by another ESC, the one before it).</summary>
        private int HandleEscape(string stream, int start)
        {
            if (start + 1 >= stream.Length)
            {
                return start;
            }

            var next = stream[start + 1];

            // OSC (]), DCS (P), SOS (X), PM (^) and APC (_) run until BEL or ST: titles, hyperlinks, sixel...
            if (next is ']' or 'P' or 'X' or '^' or '_')
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

            if (next == '[')
            {
                return HandleCsi(stream, start);
            }

            switch (next)
            {
                case '7':
                    _savedRow = _row;
                    _savedColumn = _column;
                    return start + 1;
                case '8':
                    _row = Math.Clamp(_savedRow, 0, _rows - 1);
                    _column = Math.Clamp(_savedColumn, 0, _columns);
                    return start + 1;
                case 'M':
                    ReverseIndex();
                    return start + 1;
                case 'D':
                    LineFeed();
                    return start + 1;
                case 'E':
                    _column = 0;
                    LineFeed();
                    return start + 1;
                case 'c':
                    ClearAll();
                    _top = 0;
                    _bottom = _rows - 1;
                    return start + 1;
            }

            // ESC ( B, ESC ) 0, ESC # 8, ESC % G, ESC SP F ...: an intermediate byte and then a final one.
            // Swallowing the final byte is what keeps "ESC ( B" from printing a "B".
            if (next is '(' or ')' or '*' or '+' or '-' or '.' or '/' or '#' or '%' or ' ')
            {
                return Math.Min(stream.Length - 1, start + 2);
            }

            return start + 1; // ESC =, ESC >, ESC N ...: two characters, no screen effect
        }

        private int HandleCsi(string stream, int start)
        {
            var cursor = start + 2;
            var parameters = new StringBuilder();
            var hasIntermediate = false;

            // parameter bytes 0x30-0x3F (digits ; : < = > ?), then intermediate bytes 0x20-0x2F, then ONE
            // final byte 0x40-0x7E. Nothing between ESC [ and the final byte is ever printed.
            while (cursor < stream.Length)
            {
                var ch = stream[cursor];
                if (ch == '\u001b')
                {
                    return cursor - 1; // cut off by a new sequence: let the caller restart there
                }

                if (ch is >= '0' and <= '?' && !hasIntermediate)
                {
                    parameters.Append(ch);
                }
                else if (ch is >= ' ' and <= '/')
                {
                    hasIntermediate = true;
                }
                else if (ch is >= '@' and <= '~')
                {
                    break;
                }
                else if (ch < ' ')
                {
                    // a C0 control inside a CSI is executed without ending it - not printed
                }
                else
                {
                    _unsupported++; // a byte that is not valid anywhere in a CSI
                    return cursor;
                }

                cursor++;
            }

            if (cursor >= stream.Length)
            {
                return stream.Length - 1;
            }

            var final = stream[cursor];
            var text = parameters.ToString();
            var prefix = text.Length > 0 && text[0] is '?' or '<' or '=' or '>' ? text[0] : '\0';
            var args = ParseParameters(prefix == '\0' ? text : text[1..]);

            if (hasIntermediate)
            {
                // DECRQM ($p), DECSCUSR ( q), DECSTR (!p) and friends: none move the cursor or change cells.
                return cursor;
            }

            if (prefix is '<' or '=' or '>')
            {
                return cursor; // XTVERSION (>0q), kitty keyboard (<u, =u, >u), modifyOtherKeys (>4;1m)...
            }

            if (prefix == '?')
            {
                ApplyPrivate(final, args);
                return cursor;
            }

            Apply(final, args);
            return cursor;
        }

        private void ApplyPrivate(char final, int[] args)
        {
            switch (final)
            {
                case 'h':
                    // Entering the alternate screen (1049/1047/47) gives the app a fresh, blank grid - the
                    // normal screen's content is not visible under it.
                    foreach (var mode in args)
                    {
                        if (mode is 1049 or 1047 or 47)
                        {
                            ClearAll();
                            break;
                        }
                    }

                    break;

                case 'J':
                    EraseInDisplay(Arg(args, 0, 0)); // DECSED behaves like ED for our purposes
                    break;
                case 'K':
                    EraseInLine(Arg(args, 0, 0)); // DECSEL likewise
                    break;
            }
        }

        private void Apply(char final, int[] args)
        {
            switch (final)
            {
                case 'H':
                case 'f':
                    _row = Math.Clamp(Arg(args, 0, 1) - 1, 0, _rows - 1);
                    _column = Math.Clamp(Arg(args, 1, 1) - 1, 0, _columns - 1);
                    break;

                case 'A':
                    _row = Math.Max(0, _row - Arg(args, 0, 1));
                    break;
                case 'B':
                case 'e':
                    _row = Math.Min(_rows - 1, _row + Arg(args, 0, 1));
                    break;
                case 'C':
                case 'a':
                    _column = Math.Min(_columns - 1, Cx + Arg(args, 0, 1));
                    break;
                case 'D':
                    _column = Math.Max(0, Cx - Arg(args, 0, 1));
                    break;
                case 'E':
                    _row = Math.Min(_rows - 1, _row + Arg(args, 0, 1));
                    _column = 0;
                    break;
                case 'F':
                    _row = Math.Max(0, _row - Arg(args, 0, 1));
                    _column = 0;
                    break;
                case 'G':
                case '`':
                    _column = Math.Clamp(Arg(args, 0, 1) - 1, 0, _columns - 1);
                    break;
                case 'd':
                    _row = Math.Clamp(Arg(args, 0, 1) - 1, 0, _rows - 1);
                    break;

                case 'I':
                    for (var n = Arg(args, 0, 1); n > 0; n--)
                    {
                        _column = Math.Min(_columns - 1, ((Cx / 8) + 1) * 8);
                    }

                    break;
                case 'Z':
                    for (var n = Arg(args, 0, 1); n > 0; n--)
                    {
                        _column = Math.Max(0, ((Cx - 1) / 8) * 8);
                    }

                    break;

                case 'J':
                    EraseInDisplay(Arg(args, 0, 0));
                    break;
                case 'K':
                    EraseInLine(Arg(args, 0, 0));
                    break;
                case 'X':
                    EraseCharacters(Arg(args, 0, 1));
                    break;
                case 'P':
                    DeleteCharacters(Arg(args, 0, 1));
                    break;
                case '@':
                    InsertCharacters(Arg(args, 0, 1));
                    break;
                case 'L':
                    InsertLines(Arg(args, 0, 1));
                    break;
                case 'M':
                    DeleteLines(Arg(args, 0, 1));
                    break;
                case 'S':
                    ScrollUp(_top, _bottom, Arg(args, 0, 1));
                    break;
                case 'T':
                    ScrollDown(_top, _bottom, Arg(args, 0, 1));
                    break;
                case 'b':
                    for (var n = Math.Min(Arg(args, 0, 1), _columns * _rows); n > 0; n--)
                    {
                        Print(_lastCodePoint);
                    }

                    break;

                case 'r':
                {
                    var top = Arg(args, 0, 1) - 1;
                    var bottom = Arg(args, 1, _rows) - 1;
                    if (top >= 0 && bottom < _rows && top < bottom)
                    {
                        _top = top;
                        _bottom = bottom;
                    }
                    else
                    {
                        _top = 0;
                        _bottom = _rows - 1;
                    }

                    _row = 0;
                    _column = 0;
                    break;
                }

                case 's':
                    _savedRow = _row;
                    _savedColumn = _column;
                    break;
                case 'u':
                    _row = Math.Clamp(_savedRow, 0, _rows - 1);
                    _column = Math.Clamp(_savedColumn, 0, _columns);
                    break;

                // No cursor or cell effect: SGR, modes, reports (DSR/DA), window ops, soft reset, tab clear.
                case 'm':
                case 'h':
                case 'l':
                case 'n':
                case 'c':
                case 't':
                case 'q':
                case 'p':
                case 'i':
                case 'g':
                case 'x':
                case 'y':
                    break;

                default:
                    _unsupported++; // a final byte we do not know: the grid may now differ from the real screen
                    break;
            }
        }

        private static int[] ParseParameters(string parameters)
        {
            if (parameters.Length == 0)
            {
                return Array.Empty<int>();
            }

            var parts = parameters.Split(';');
            var values = new int[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                // "38:2::255:0:0" style sub-parameters: only the leading value is of interest.
                var head = parts[i].Split(':')[0];
                values[i] = int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            }

            return values;
        }

        private static int Arg(int[] args, int index, int fallback) =>
            index < args.Length && args[index] > 0 ? args[index] : fallback;
    }
}

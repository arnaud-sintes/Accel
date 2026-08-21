namespace Accel.App.Services;

using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

/// <summary>
/// <see cref="IDocumentSearchView"/> over a <see cref="RichTextBox"/> - the diff view's <b>read-only</b>
/// "After" pane (<c>MainWindow.DiffNewText</c>), which shows a hand-built <see cref="FlowDocument"/>
/// instead of an AvalonEdit document (see <c>MainWindow.BuildHighlightedDocument</c>).
///
/// <para><b>Why an offset map.</b> The find bar works in plain-text offsets, but a
/// <see cref="FlowDocument"/> is addressed with <see cref="TextPointer"/>s, and the one built for a
/// diff pane is deliberately fragmented: syntax colouring emits a separate <see cref="Run"/> per
/// token, so an ordinary query ("if (") straddles several of them. Walking the document once into a
/// flat string plus a run-by-run offset table is what lets a match be found across those run
/// boundaries and still be turned back into a selectable pointer pair. The map is rebuilt only when
/// the pane is handed a different document, not per keystroke.</para>
///
/// <para><b>Why only the current match is marked.</b> Highlighting every hit here would mean
/// <c>TextRange.ApplyPropertyValue(TextElement.BackgroundProperty, ...)</c> per match, which splits
/// runs and rewrites the document - destroying the per-run added-line backgrounds this very pane uses
/// to show the diff, with no way to restore them. So <see cref="ShowMatches"/> is a no-op and
/// <see cref="Reveal"/> marks the current hit with the selection instead (the pane sets
/// <c>IsInactiveSelectionHighlightEnabled</c> so it stays visible while the find box has focus). The
/// AvalonEdit panes, which can colour without touching their document, do highlight all matches - see
/// <see cref="TextEditorSearchView"/>.</para>
/// </summary>
public sealed class RichTextBoxSearchView : IDocumentSearchView
{
    /// <summary>One <see cref="Run"/>'s text: where it starts in <see cref="Text"/>, how long it is,
    /// and the pointer its first character sits at.</summary>
    private readonly record struct Segment(TextPointer Start, int TextOffset, int Length);

    private readonly RichTextBox _box;
    private readonly List<Segment> _segments = new();

    private FlowDocument? _mappedDocument;
    private string _text = string.Empty;

    public RichTextBoxSearchView(RichTextBox box)
    {
        _box = box;
    }

    public string Text
    {
        get
        {
            EnsureMap();
            return _text;
        }
    }

    public int CaretOffset
    {
        get
        {
            EnsureMap();
            return OffsetOf(_box.Selection.Start);
        }
    }

    /// <summary>No-op by design - see the class remarks.</summary>
    public void ShowMatches(IReadOnlyList<TextSearchMatch> matches, int currentIndex)
    {
    }

    public void Reveal(TextSearchMatch match)
    {
        EnsureMap();

        if (PointerAt(match.Offset) is not { } start || PointerAfter(match.EndOffset - 1) is not { } end)
        {
            return;
        }

        _box.Selection.Select(start, end);
        ScrollIntoView(start);
    }

    /// <summary>Nothing to undo - <see cref="ShowMatches"/> paints nothing, and the selection
    /// <see cref="Reveal"/> left behind is deliberately kept so closing the bar leaves the user on the
    /// match they stopped at.</summary>
    public void ClearMatches()
    {
    }

    public void FocusDocument() => _box.Focus();

    /// <summary>
    /// Flattens the current document into <see cref="_text"/> + <see cref="_segments"/>, or does
    /// nothing when the document has not changed since the last call.
    ///
    /// <para>Only <see cref="LineBreak"/> is treated as a newline (and gets no segment of its own,
    /// since the find bar's query is single-line and so can never match across one): the sole producer
    /// of the documents this runs over, <c>MainWindow.BuildHighlightedDocument</c>, emits exactly one
    /// <see cref="Paragraph"/> of <see cref="Run"/>s separated by <see cref="LineBreak"/>s.</para>
    /// </summary>
    private void EnsureMap()
    {
        if (ReferenceEquals(_box.Document, _mappedDocument))
        {
            return;
        }

        _mappedDocument = _box.Document;
        _segments.Clear();
        var builder = new StringBuilder();

        var position = _mappedDocument?.ContentStart;
        while (position is not null)
        {
            var context = position.GetPointerContext(LogicalDirection.Forward);
            if (context == TextPointerContext.Text)
            {
                string run = position.GetTextInRun(LogicalDirection.Forward);
                if (run.Length > 0)
                {
                    _segments.Add(new Segment(position, builder.Length, run.Length));
                    builder.Append(run);

                    // Advance by the run's own length: inside a text run, one symbol is one character,
                    // so this lands exactly at the run's end rather than re-walking it context by
                    // context.
                    position = position.GetPositionAtOffset(run.Length, LogicalDirection.Forward);
                    continue;
                }
            }
            else if (context == TextPointerContext.ElementStart
                     && position.GetAdjacentElement(LogicalDirection.Forward) is LineBreak)
            {
                // ElementStart only: a LineBreak is an empty element, so its ElementEnd position
                // reports the very same adjacent element and would append a second newline for the
                // one break, shifting every offset below it.
                builder.Append('\n');
            }

            position = position.GetNextContextPosition(LogicalDirection.Forward);
        }

        _text = builder.ToString();
    }

    /// <summary>Pointer immediately before the character at <paramref name="offset"/>.</summary>
    private TextPointer? PointerAt(int offset) =>
        SegmentContaining(offset) is { } segment
            ? segment.Start.GetPositionAtOffset(offset - segment.TextOffset, LogicalDirection.Forward)
            : null;

    /// <summary>Pointer immediately after the character at <paramref name="offset"/>. Resolved from
    /// that character's own segment (never from the match's start segment) so a match spanning several
    /// syntax-coloured runs still ends on a valid in-run pointer.</summary>
    private TextPointer? PointerAfter(int offset) =>
        SegmentContaining(offset) is { } segment
            ? segment.Start.GetPositionAtOffset(offset - segment.TextOffset + 1, LogicalDirection.Forward)
            : null;

    /// <summary>The run holding the character at <paramref name="offset"/>, or <see langword="null"/>
    /// when the offset is out of range or is one of the synthetic newlines (which have no run).</summary>
    private Segment? SegmentContaining(int offset)
    {
        int low = 0;
        int high = _segments.Count - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            var segment = _segments[mid];
            if (offset < segment.TextOffset)
            {
                high = mid - 1;
            }
            else if (offset >= segment.TextOffset + segment.Length)
            {
                low = mid + 1;
            }
            else
            {
                return segment;
            }
        }

        return null;
    }

    /// <summary>
    /// Flat-text offset of <paramref name="pointer"/>, or 0 when it cannot be placed. Linear over the
    /// segments (only ever called once, when the bar opens) and driven by
    /// <see cref="TextPointer.CompareTo"/> rather than <c>GetOffsetToPosition</c> per segment, which
    /// would make this quadratic.
    /// </summary>
    private int OffsetOf(TextPointer pointer)
    {
        foreach (var segment in _segments)
        {
            if (segment.Start.CompareTo(pointer) > 0)
            {
                // Past it already: the pointer sat in a gap (an element boundary or a line break), so
                // the next run's start is the nearest meaningful offset.
                return segment.TextOffset;
            }

            int distance = segment.Start.GetOffsetToPosition(pointer);
            if (distance <= segment.Length)
            {
                return segment.TextOffset + distance;
            }
        }

        return 0;
    }

    /// <summary>
    /// Scrolls <paramref name="pointer"/> into view, but only when it is actually outside the
    /// viewport - so repeated Find Next hits within one screenful leave the view still.
    /// <see cref="TextPointer.GetCharacterRect"/> is already in the
    /// <see cref="RichTextBox"/>'s own coordinates, hence the plain viewport comparisons.
    /// </summary>
    private void ScrollIntoView(TextPointer pointer)
    {
        var rect = pointer.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty)
        {
            return;
        }

        if (rect.Top < 0 || rect.Bottom > _box.ViewportHeight)
        {
            _box.ScrollToVerticalOffset(_box.VerticalOffset + rect.Top - (_box.ViewportHeight / 2));
        }

        if (rect.Left < 0 || rect.Right > _box.ViewportWidth)
        {
            _box.ScrollToHorizontalOffset(_box.HorizontalOffset + rect.Left - (_box.ViewportWidth / 3));
        }
    }
}

namespace Accel.App.Services;

using System;
using System.Collections.Generic;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

/// <summary>
/// AvalonEdit line transformer that paints the find bar's hits: <see cref="MatchBrush"/> behind every
/// match and <see cref="CurrentMatchBrush"/> behind the one the bar is currently on. A third
/// transformer alongside <see cref="SyntaxColorizer"/> and <see cref="DiffLineHighlighter"/> for the
/// same reason those two are separate from each other - it varies with the search query alone, so
/// retyping in the find bar must not invalidate the syntax cache or the diff highlight.
///
/// <para>Added <b>after</b> the other two on the same <c>TextView</c>: transformers run in order and
/// the last background wins, so a hit stays visible on top of an added-line highlight.</para>
/// </summary>
public sealed class SearchMatchColorizer : DocumentColorizingTransformer
{
    private readonly Brush _matchBrush;
    private readonly Brush _currentMatchBrush;

    private IReadOnlyList<TextSearchMatch> _matches = Array.Empty<TextSearchMatch>();
    private int _currentIndex = -1;

    public SearchMatchColorizer(Brush matchBrush, Brush currentMatchBrush)
    {
        _matchBrush = matchBrush;
        _currentMatchBrush = currentMatchBrush;
    }

    /// <summary>Replaces the painted match set. <paramref name="matches"/> must be sorted by offset
    /// (<see cref="TextSearchEngine.FindAll"/> already is) - <see cref="ColorizeLine"/> binary-searches
    /// it. The caller redraws the <see cref="TextView"/>; this class does not own one.</summary>
    public void SetMatches(IReadOnlyList<TextSearchMatch> matches, int currentIndex)
    {
        _matches = matches;
        _currentIndex = currentIndex;
    }

    public void Clear() => SetMatches(Array.Empty<TextSearchMatch>(), -1);

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_matches.Count == 0)
        {
            return;
        }

        // Binary search rather than a scan from index 0: ColorizeLine runs once per visible line on
        // every redraw, so a linear scan would be O(visible lines x matches) - visibly slow once a
        // short query matches thousands of times in a large file.
        for (int i = FirstMatchFrom(line.Offset); i < _matches.Count; i++)
        {
            var match = _matches[i];
            if (match.Offset >= line.EndOffset)
            {
                break;
            }

            // The find bar's query is single-line, so a match never spans a line break - but clamp
            // anyway rather than trust that, since ChangeLinePart throws on an out-of-line offset.
            int start = Math.Max(match.Offset, line.Offset);
            int end = Math.Min(match.EndOffset, line.EndOffset);
            if (start >= end)
            {
                continue;
            }

            var brush = i == _currentIndex ? _currentMatchBrush : _matchBrush;
            ChangeLinePart(start, end, element => element.TextRunProperties.SetBackgroundBrush(brush));
        }
    }

    /// <summary>Index of the first match that could still touch <paramref name="lineOffset"/> or
    /// anything after it - i.e. the first whose <see cref="TextSearchMatch.EndOffset"/> is past it.</summary>
    private int FirstMatchFrom(int lineOffset)
    {
        int low = 0;
        int high = _matches.Count;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (_matches[mid].EndOffset <= lineOffset)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}

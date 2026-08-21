namespace Accel.App.Services;

using System;
using System.Collections.Generic;

/// <summary>One hit: a half-open <c>[Offset, Offset + Length)</c> span of the searched text.</summary>
public readonly record struct TextSearchMatch(int Offset, int Length)
{
    public int EndOffset => Offset + Length;
}

/// <summary>
/// The whole find-in-document algorithm, deliberately WPF-free and static so it can be unit-tested
/// without a document, an editor or a UI thread (see TextSearchEngineTests) - the controls that use
/// it (<see cref="TextEditorSearchView"/>, <see cref="RichTextBoxSearchView"/>, and the
/// <c>DocumentSearchBar</c> that drives them) only turn its offsets into highlights and scroll
/// positions.
/// </summary>
public static class TextSearchEngine
{
    /// <summary>
    /// Hard cap on the number of matches collected for one query, so a degenerate one-or-two-character
    /// query against a large file cannot make every keystroke allocate a multi-million-entry list (and,
    /// worse, make the highlighting colorizer walk it). Reaching the cap is reported through
    /// <see cref="SearchResult.Truncated"/> rather than silently pretending the file only had this
    /// many hits.
    /// </summary>
    public const int MaxMatches = 20_000;

    /// <summary>Matches in ascending <see cref="TextSearchMatch.Offset"/> order, plus whether
    /// <see cref="MaxMatches"/> cut the scan short.</summary>
    public readonly record struct SearchResult(IReadOnlyList<TextSearchMatch> Matches, bool Truncated)
    {
        public static SearchResult Empty { get; } = new(Array.Empty<TextSearchMatch>(), false);

        public int Count => Matches.Count;
    }

    /// <summary>
    /// All non-overlapping occurrences of <paramref name="query"/> in <paramref name="text"/>.
    /// Non-overlapping (the scan resumes past the end of each hit, not one character past its start)
    /// to match what every editor's find bar reports: "aa" in "aaaa" is two matches, not three.
    /// An empty query matches nothing rather than everything - the find bar shows its neutral
    /// "type to search" state instead of claiming a hit per character.
    /// </summary>
    public static SearchResult FindAll(string? text, string? query, bool matchCase, bool wholeWord)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query) || query.Length > text.Length)
        {
            return SearchResult.Empty;
        }

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = new List<TextSearchMatch>();
        int from = 0;

        while (from <= text.Length - query.Length)
        {
            int at = text.IndexOf(query, from, comparison);
            if (at < 0)
            {
                break;
            }

            if (!wholeWord || IsWholeWord(text, at, query.Length))
            {
                matches.Add(new TextSearchMatch(at, query.Length));
                if (matches.Count >= MaxMatches)
                {
                    return new SearchResult(matches, true);
                }

                from = at + query.Length;
            }
            else
            {
                // Only advance by one here: the rejected span may still start a real whole-word hit
                // one character later (e.g. "in" against "print in" - rejecting the "in" inside
                // "print" must not skip past the standalone one).
                from = at + 1;
            }
        }

        return new SearchResult(matches, false);
    }

    /// <summary>
    /// Index of the first match starting at or after <paramref name="offset"/>, or 0 when there is
    /// none (wrapping to the top, the same way <see cref="Next"/>/<see cref="Previous"/> wrap). Used
    /// to open the find bar on the match nearest the caret rather than always jumping to the top of
    /// the file.
    /// </summary>
    public static int IndexAtOrAfter(IReadOnlyList<TextSearchMatch> matches, int offset)
    {
        for (int i = 0; i < matches.Count; i++)
        {
            if (matches[i].Offset >= offset)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>Wrapping "find next" step over <paramref name="count"/> matches; -1 for none.</summary>
    public static int Next(int current, int count) => count <= 0 ? -1 : (current + 1) % count;

    /// <summary>Wrapping "find previous" step over <paramref name="count"/> matches; -1 for none.
    /// A <paramref name="current"/> of -1 (nothing current yet) steps to the <b>last</b> match, the
    /// mirror of <see cref="Next"/> stepping to the first.</summary>
    public static int Previous(int current, int count) => count <= 0 ? -1 : current <= 0 ? count - 1 : current - 1;

    /// <summary>
    /// True when the span is bounded on both sides by something that is not a word character, where
    /// "word character" is letter/digit/underscore - the same definition editors use, so an
    /// identifier match does not count its surrounding punctuation.
    /// </summary>
    private static bool IsWholeWord(string text, int offset, int length)
    {
        if (offset > 0 && IsWordChar(text[offset - 1]))
        {
            return false;
        }

        int after = offset + length;
        return after >= text.Length || !IsWordChar(text[after]);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}

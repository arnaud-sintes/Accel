namespace Accel.Tests;

using System.Collections.Generic;
using System.Linq;
using Accel.App.Services;
using Xunit;

/// <summary>
/// Unit tests for the Ctrl+F find bar's search logic. <see cref="TextSearchEngine"/> is deliberately
/// WPF-free precisely so this can be covered without a window, an editor or a UI thread - the
/// controls that use it only translate these offsets into highlights and scroll positions.
/// </summary>
public sealed class TextSearchEngineTests
{
    private static IReadOnlyList<TextSearchMatch> Find(
        string text, string query, bool matchCase = false, bool wholeWord = false) =>
        TextSearchEngine.FindAll(text, query, matchCase, wholeWord).Matches;

    private static (int Offset, int Length)[] Offsets(IReadOnlyList<TextSearchMatch> matches) =>
        matches.Select(m => (m.Offset, m.Length)).ToArray();

    [Fact]
    public void FindAll_IsCaseInsensitiveByDefault()
    {
        Assert.Equal(new[] { (0, 3), (4, 3) }, Offsets(Find("Foo foo", "foo")));
    }

    [Fact]
    public void FindAll_MatchCaseExcludesDifferentlyCasedHits()
    {
        Assert.Equal(new[] { (4, 3) }, Offsets(Find("Foo foo", "foo", matchCase: true)));
    }

    [Fact]
    public void FindAll_ReturnsNonOverlappingMatches()
    {
        // "aa" in "aaaa" is two matches, not three - what every editor's find bar reports.
        Assert.Equal(new[] { (0, 2), (2, 2) }, Offsets(Find("aaaa", "aa")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void FindAll_EmptyQueryMatchesNothing(string? query)
    {
        Assert.Empty(Find("some text", query!));
    }

    [Fact]
    public void FindAll_EmptyTextMatchesNothing()
    {
        Assert.Empty(Find(string.Empty, "x"));
    }

    [Fact]
    public void FindAll_QueryLongerThanTextMatchesNothing()
    {
        Assert.Empty(Find("ab", "abc"));
    }

    [Fact]
    public void FindAll_MatchesAcrossLines()
    {
        // Offsets are into the whole text, newlines included - the colorizer maps them back to lines.
        Assert.Equal(new[] { (0, 3), (7, 3) }, Offsets(Find("int a;\nint b;", "int")));
    }

    [Fact]
    public void FindAll_WholeWordRejectsMatchesInsideIdentifiers()
    {
        Assert.Equal(new[] { (6, 2) }, Offsets(Find("print in", "in", wholeWord: true)));
    }

    [Fact]
    public void FindAll_WholeWordTreatsUnderscoreAndDigitsAsWordCharacters()
    {
        Assert.Empty(Find("_id id2", "id", wholeWord: true));
    }

    [Fact]
    public void FindAll_WholeWordAcceptsPunctuationBoundaries()
    {
        Assert.Equal(new[] { (1, 3) }, Offsets(Find("(foo)", "foo", wholeWord: true)));
    }

    [Fact]
    public void FindAll_WholeWordAcceptsMatchAtBothEndsOfTheText()
    {
        Assert.Equal(new[] { (0, 3) }, Offsets(Find("foo", "foo", wholeWord: true)));
    }

    [Fact]
    public void FindAll_WholeWordDoesNotSkipARealHitFollowingARejectedOne()
    {
        // The rejected "in" inside "inn" must not advance the scan past the standalone "in" that
        // starts one character later.
        Assert.Equal(new[] { (5, 2) }, Offsets(Find("inn, in", "in", wholeWord: true)));
    }

    [Fact]
    public void FindAll_ReportsTruncationOnceTheMatchCapIsHit()
    {
        var result = TextSearchEngine.FindAll(
            new string('a', TextSearchEngine.MaxMatches + 50), "a", matchCase: false, wholeWord: false);

        Assert.True(result.Truncated);
        Assert.Equal(TextSearchEngine.MaxMatches, result.Count);
    }

    [Fact]
    public void FindAll_DoesNotReportTruncationBelowTheCap()
    {
        var result = TextSearchEngine.FindAll("aaa", "a", matchCase: false, wholeWord: false);

        Assert.False(result.Truncated);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void IndexAtOrAfter_PicksTheFirstMatchFromTheCaret()
    {
        var matches = Find("foo foo foo", "foo");

        Assert.Equal(0, TextSearchEngine.IndexAtOrAfter(matches, 0));
        Assert.Equal(1, TextSearchEngine.IndexAtOrAfter(matches, 1));
        Assert.Equal(2, TextSearchEngine.IndexAtOrAfter(matches, 8));
    }

    [Fact]
    public void IndexAtOrAfter_WrapsToTheTopWhenTheCaretIsPastEveryMatch()
    {
        Assert.Equal(0, TextSearchEngine.IndexAtOrAfter(Find("foo", "foo"), 99));
    }

    [Fact]
    public void IndexAtOrAfter_ReturnsZeroForAnEmptyMatchSet()
    {
        Assert.Equal(0, TextSearchEngine.IndexAtOrAfter(Find("bar", "foo"), 0));
    }

    [Fact]
    public void NextAndPrevious_WrapAroundBothEnds()
    {
        Assert.Equal(1, TextSearchEngine.Next(0, 3));
        Assert.Equal(0, TextSearchEngine.Next(2, 3));
        Assert.Equal(2, TextSearchEngine.Previous(0, 3));
        Assert.Equal(1, TextSearchEngine.Previous(2, 3));
    }

    [Fact]
    public void NextAndPrevious_StartFromTheTopWhenNothingIsCurrentYet()
    {
        // -1 is the "no current match" sentinel the find bar holds before its first step.
        Assert.Equal(0, TextSearchEngine.Next(-1, 3));
        Assert.Equal(2, TextSearchEngine.Previous(-1, 3));
    }

    [Fact]
    public void NextAndPrevious_ReturnNoMatchWhenThereAreNone()
    {
        Assert.Equal(-1, TextSearchEngine.Next(-1, 0));
        Assert.Equal(-1, TextSearchEngine.Previous(-1, 0));
    }

    [Fact]
    public void EndOffset_IsTheExclusiveEndOfTheMatch()
    {
        var match = Assert.Single(Find("xxfooxx", "foo"));

        Assert.Equal(2, match.Offset);
        Assert.Equal(5, match.EndOffset);
    }
}

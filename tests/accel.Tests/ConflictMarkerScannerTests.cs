namespace Accel.Tests;

using Accel.App.Services;
using Xunit;

/// <summary>
/// Unit tests for <see cref="ConflictMarkerScanner"/> - pure string work, so no repository fixture
/// here (the git-side half of conflict handling is covered by
/// <see cref="GitStatusBuilderConflictTests"/> and <see cref="GitActionsServiceTests"/>).
/// </summary>
public sealed class ConflictMarkerScannerTests
{
    private const string OneConflict =
        "unchanged\n"
        + "<<<<<<< HEAD\n"
        + "ours\n"
        + "=======\n"
        + "theirs\n"
        + ">>>>>>> incoming\n"
        + "tail\n";

    [Fact]
    public void Scan_NoMarkers_IsEmpty()
    {
        var scan = ConflictMarkerScanner.Scan("line one\nline two\n");

        Assert.Equal(0, scan.RegionCount);
        Assert.Empty(scan.Lines);
    }

    [Fact]
    public void Scan_CoversTheWholeRegionIncludingItsMarkers()
    {
        var scan = ConflictMarkerScanner.Scan(OneConflict);

        Assert.Equal(1, scan.RegionCount);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, scan.Lines);
    }

    [Fact]
    public void Scan_CountsEachRegionSeparately()
    {
        var scan = ConflictMarkerScanner.Scan(OneConflict + OneConflict);

        Assert.Equal(2, scan.RegionCount);
    }

    [Fact]
    public void Scan_Diff3StyleBaseSectionStaysInsideTheRegion()
    {
        string content =
            "<<<<<<< HEAD\n"
            + "ours\n"
            + "||||||| merged common ancestors\n"
            + "base\n"
            + "=======\n"
            + "theirs\n"
            + ">>>>>>> incoming\n";

        var scan = ConflictMarkerScanner.Scan(content);

        Assert.Equal(1, scan.RegionCount);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, scan.Lines);
    }

    /// <summary>A bare "=======" is ordinary prose (a rule line, a quoted diff, a doc comment) - only
    /// an opening marker starts a region, or every markdown file with a setext heading underline would
    /// light up as conflicted.</summary>
    [Fact]
    public void Scan_SeparatorWithoutAnOpeningMarker_IsNotAConflict()
    {
        var scan = ConflictMarkerScanner.Scan("Heading\n=======\nbody\n>>>>>>> quoted\n");

        Assert.Equal(0, scan.RegionCount);
        Assert.Empty(scan.Lines);
    }

    /// <summary>A half-deleted marker (the user removed the closing line but not the rest) is exactly
    /// the state worth still highlighting, so an unterminated region is reported rather than dropped -
    /// and it runs to the end of the file, including the empty line a trailing newline produces, since
    /// there is nothing left to bound it.</summary>
    [Fact]
    public void Scan_UnterminatedRegion_RunsToEndOfFile()
    {
        var scan = ConflictMarkerScanner.Scan("head\n<<<<<<< HEAD\nours\n");

        Assert.Equal(1, scan.RegionCount);
        Assert.Equal(new[] { 1, 2, 3 }, scan.Lines);
    }

    [Fact]
    public void Scan_HandlesWindowsLineEndings()
    {
        var scan = ConflictMarkerScanner.Scan(OneConflict.Replace("\n", "\r\n"));

        Assert.Equal(1, scan.RegionCount);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, scan.Lines);
    }

    [Theory]
    [InlineData("<<<<<<< HEAD")]
    [InlineData("||||||| base")]
    [InlineData("=======")]
    [InlineData(">>>>>>> other")]
    public void IsMarkerLine_RecognizesAllFourMarkers(string line) =>
        Assert.True(ConflictMarkerScanner.IsMarkerLine(line));

    [Fact]
    public void IsMarkerLine_OrdinaryTextIsNotAMarker() =>
        Assert.False(ConflictMarkerScanner.IsMarkerLine("<< not a marker"));
}

namespace Accel.App.Services;

using System;
using System.Collections.Generic;

/// <summary>Where the conflict markers are in one working-tree file: the 0-based indices of every
/// line inside a conflict region (the <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c>/<c>=======</c>/
/// <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> lines themselves included), and how many such regions there
/// are. An empty <see cref="Lines"/> with a zero <see cref="RegionCount"/> means the file no longer
/// has any markers in it - i.e. the user has finished editing and the row is ready to be marked
/// resolved.</summary>
public sealed record ConflictMarkerScan(IReadOnlyList<int> Lines, int RegionCount)
{
    public static readonly ConflictMarkerScan Empty = new(Array.Empty<int>(), 0);
}

/// <summary>
/// Pure, WPF-free scanner for git's conflict markers in a working-tree file - the input to the GIT
/// diff view's conflict highlighting (<c>MainWindow.ShowGitDiffTabAsync</c> feeds
/// <see cref="ConflictMarkerScan.Lines"/> straight into a <see cref="DiffLineHighlighter"/>, exactly
/// as it feeds that class the added-line set of an ordinary diff).
///
/// <para>Deliberately marker-text-driven rather than asking git: the whole point of the conflict
/// view is that the user is <i>editing</i> the markers away, so what matters is what the buffer says
/// right now, not what the index recorded when the merge stopped. <c>git status</c> would keep
/// reporting the path as unmerged either way.</para>
/// </summary>
public static class ConflictMarkerScanner
{
    private const string StartMarker = "<<<<<<<";
    private const string BaseMarker = "|||||||";
    private const string SeparatorMarker = "=======";
    private const string EndMarker = ">>>>>>>";

    /// <summary>Scans <paramref name="content"/> (any line endings). A region left unterminated at
    /// end of file is still reported - a half-deleted marker is precisely the state worth showing -
    /// but nothing is reported for a <c>=======</c> or <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> with no
    /// opening marker before it, since those also occur naturally in ordinary text (rule lines,
    /// quoted diffs, this file's own doc comment).</summary>
    public static ConflictMarkerScan Scan(string? content)
    {
        if (string.IsNullOrEmpty(content) || !content.Contains(StartMarker, StringComparison.Ordinal))
        {
            return ConflictMarkerScan.Empty;
        }

        return Scan(content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
    }

    /// <summary>Line-array overload - used by callers that have already split and normalized the
    /// content for the diff panes (<c>MainWindow.ShowGitDiffTabAsync</c> does both), so it doesn't
    /// pay for a second split.</summary>
    public static ConflictMarkerScan Scan(IReadOnlyList<string> lines)
    {
        var result = new List<int>();
        var pending = new List<int>();
        int regionCount = 0;
        bool inRegion = false;

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];

            if (line.StartsWith(StartMarker, StringComparison.Ordinal))
            {
                // A second start marker before the first was closed means the file is mangled beyond
                // what this can attribute - keep what was collected as its own region and restart, so
                // the highlight still covers everything rather than silently dropping it.
                if (inRegion)
                {
                    result.AddRange(pending);
                    regionCount++;
                    pending.Clear();
                }

                inRegion = true;
                pending.Add(i);
                continue;
            }

            if (!inRegion)
            {
                continue;
            }

            pending.Add(i);

            if (line.StartsWith(EndMarker, StringComparison.Ordinal))
            {
                result.AddRange(pending);
                regionCount++;
                pending.Clear();
                inRegion = false;
            }
        }

        if (pending.Count > 0)
        {
            result.AddRange(pending);
            regionCount++;
        }

        return result.Count == 0 ? ConflictMarkerScan.Empty : new ConflictMarkerScan(result, regionCount);
    }

    /// <summary>Whether <paramref name="line"/> is one of git's four conflict-marker lines - exposed
    /// for callers that only need the per-line question (nothing in the app needs the region grouping
    /// to decide, say, whether to bold a single line).</summary>
    public static bool IsMarkerLine(string line) =>
        line.StartsWith(StartMarker, StringComparison.Ordinal)
        || line.StartsWith(BaseMarker, StringComparison.Ordinal)
        || line.StartsWith(SeparatorMarker, StringComparison.Ordinal)
        || line.StartsWith(EndMarker, StringComparison.Ordinal);
}

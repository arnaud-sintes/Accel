namespace Accel.Tests;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

/// <summary>
/// Tests for <c>copytext.js</c>, the pure clean-up applied to text copied out of the terminal. It is a
/// browser-side script, so these run it under <c>node</c> against the real file (a no-op, with a note, on a
/// machine without node - the script itself has no other dependency).
/// </summary>
public class TerminalCopyTextTests
{
    private sealed record Case(string Text, bool Rectangular = false, int Columns = 0, int StartColumn = 0);

    private static string? FindScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "App", "Controls", "wwwroot", "xterm", "copytext.js");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The normalised text for each case, or null when node is not available.</summary>
    private static string[]? Run(params Case[] cases)
    {
        var script = FindScript();
        Assert.NotNull(script);

        const string driver =
            "const { normalizeTerminalCopy } = require(process.argv[1]);" +
            "let s = ''; process.stdin.on('data', d => s += d).on('end', () => {" +
            "  const cases = JSON.parse(s);" +
            "  process.stdout.write(JSON.stringify(cases.map(c => normalizeTerminalCopy(c.text, " +
            "    { rectangular: c.rectangular, columns: c.columns, startColumn: c.startColumn }))));" +
            "});";

        var info = new ProcessStartInfo("node")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // The terminal text is not ASCII ("●", box drawing): without this the pipe uses the console code page.
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
        };
        info.ArgumentList.Add("-e");
        info.ArgumentList.Add(driver);
        info.ArgumentList.Add(script!);

        Process process;
        try
        {
            process = Process.Start(info)!;
        }
        catch (Win32Exception)
        {
            return null; // no node on this machine
        }

        using (process)
        {
            process.StandardInput.Write(JsonSerializer.Serialize(
                cases.Select(c => new { text = c.Text, rectangular = c.Rectangular, columns = c.Columns, startColumn = c.StartColumn })));
            process.StandardInput.Close();

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
            return JsonSerializer.Deserialize<string[]>(output)!;
        }
    }

    private static void Check(string input, string expected, bool rectangular = false, int columns = 0, int startColumn = 0)
    {
        var results = Run(new Case(input, rectangular, columns, startColumn));
        if (results is null)
        {
            return;
        }

        Assert.Equal(expected, results[0]);
    }

    [Fact]
    public void TrailingSpacesOnEveryLine_AreRemoved()
    {
        Check("first line      \r\nsecond line   \r\nthird", "first line\r\nsecond line\r\nthird");
    }

    [Fact]
    public void LeadingAndInteriorWhitespace_IsContent_AndIsKept()
    {
        Check("  indented line   \r\n    more   text  ", "  indented line\r\n    more   text");
    }

    [Fact]
    public void TrailingTabsAreRemovedToo()
    {
        Check("a\t\t\r\nb \t ", "a\r\nb");
    }

    [Fact]
    public void NonBreakingSpaces_BecomeSpaces_ThenTrailingOnesAreRemoved()
    {
        Check("x  \r\ny z", "x\r\ny z");
    }

    [Fact]
    public void PaddingOnlyRowsAtTheEnd_DoNotPasteAsEmptyLines()
    {
        Check("text   \r\n      \r\n          ", "text");
    }

    [Fact]
    public void BlankRowsInTheMiddle_AreKeptAsEmptyLines()
    {
        Check("para one  \r\n     \r\npara two", "para one\r\n\r\npara two");
    }

    [Fact]
    public void LineEndings_AreNormalisedToCrLf_WhateverTheInputUsed()
    {
        Check("a \nb \rc", "a\r\nb\r\nc");
    }

    [Fact]
    public void ASingleLine_IsJustTrimmed()
    {
        Check("text      ", "text");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("  \r\n  ")]
    public void NothingButWhitespace_BecomesEmpty(string input)
    {
        Check(input, string.Empty);
    }

    [Fact]
    public void ARectangularSelection_KeepsItsWhitespace_OnlyTheLineEndingsAreNormalised()
    {
        Check("ab  \ncd  ", "ab  \r\ncd  ", rectangular: true);
    }

    [Fact]
    public void ManyCasesInOneRun_AreIndependent()
    {
        var results = Run(new Case("a  "), new Case("b  ", Rectangular: true), new Case("c\r\nd   "));
        if (results is null)
        {
            return;
        }

        Assert.Equal(new[] { "a", "b  ", "c\r\nd" }, results);
    }

    // --- soft-wrap joining -------------------------------------------------------------------------------

    private const string Sentence =
        "The quick brown fox jumps over the lazy dog while the five boxing wizards jump quickly and " +
        "pack my box with five dozen liquor jugs before the sphinx of black quartz judges my vow.";

    /// <summary>Lays a paragraph out the way Claude Code does: greedy word wrap, the first row prefixed with a
    /// marker, every other row with the two-column margin, optionally padded with real spaces to the full width.</summary>
    private static string[] Wrap(string paragraph, int width, string first = "● ", string next = "  ", bool pad = false)
    {
        var rows = new List<string>();
        var current = first;
        foreach (var word in paragraph.Split(' '))
        {
            var candidate = current.Length == (rows.Count == 0 ? first : next).Length ? current + word : current + " " + word;
            if (candidate.Length > width && current.Length > (rows.Count == 0 ? first : next).Length)
            {
                rows.Add(current);
                current = next + word;
            }
            else
            {
                current = candidate;
            }
        }

        rows.Add(current);
        return pad ? rows.Select(r => r.PadRight(width)).ToArray() : rows.ToArray();
    }

    [Theory]
    [InlineData(30, false)]
    [InlineData(47, true)]
    [InlineData(80, false)]
    [InlineData(120, true)]
    public void AWrappedSentence_IsJoinedBackIntoOne(int width, bool pad)
    {
        var rows = Wrap(Sentence, width, pad: pad);
        Assert.True(rows.Length > 1 || width >= Sentence.Length);

        Check(string.Join("\r\n", rows), "● " + Sentence, columns: width);
    }

    [Fact]
    public void TwoParagraphs_StaySeparate_AndTheirMarginIsStripped()
    {
        var one = Wrap(Sentence, 60);
        var two = Wrap("A second paragraph that is also long enough to wrap onto another row of the screen.", 60, first: "  ");

        var input = string.Join("\r\n", one.Concat(new[] { "" }).Concat(two));

        Check(input, "● " + Sentence + "\r\n\r\nA second paragraph that is also long enough to wrap onto another row of the screen.", columns: 60);
    }

    [Fact]
    public void ListItems_AreNotJoinedIntoEachOther_ButTheirWrappedRowsAre()
    {
        var item1 = Wrap("first item with enough words to need a second row on this narrow screen", 40, first: "  - ", next: "    ");
        var item2 = Wrap("second item that is short", 40, first: "  - ", next: "    ");

        Check(
            string.Join("\r\n", item1.Concat(item2)),
            "  - first item with enough words to need a second row on this narrow screen\r\n  - second item that is short",
            columns: 40);
    }

    [Fact]
    public void ShortIndentedLines_AreHardBreaks_AndKeepTheirBreaks()
    {
        Check("  roses are red\r\n  violets are blue\r\n  sugar is sweet", "  roses are red\r\n  violets are blue\r\n  sugar is sweet", columns: 80);
    }

    [Fact]
    public void ABlockStart_NeverJoinsTheRowAbove_EvenOnAFullRow()
    {
        var full = "  " + new string('x', 36) + " yy"; // 41 wide on a 40-column screen (no marker: no gutter strip)
        Check(full + "\r\n  # heading", full + "\r\n  # heading", columns: 40);
        Check(full + "\r\n  > quoted", full + "\r\n  > quoted", columns: 40);
        Check(full + "\r\n  | a | b |", full + "\r\n  | a | b |", columns: 40);
        Check(full + "\r\n  ```", full + "\r\n  ```", columns: 40);
    }

    [Fact]
    public void ASelectionStartedPartWayAlongTheFirstRow_StillJoins()
    {
        var rows = Wrap(Sentence, 50);
        const int start = 12;
        var firstRowFromStart = rows[0][start..];

        Check(
            string.Join("\r\n", new[] { firstRowFromStart }.Concat(rows.Skip(1))),
            (rows[0] + " " + string.Join(" ", rows.Skip(1).Select(r => r.TrimStart())))[start..],
            columns: 50,
            startColumn: start);
    }

    [Fact]
    public void AHyphenatedWordBrokenAtItsHyphen_IsJoinedWithoutASpace()
    {
        Check("● this row ends with a well-\r\n  known word", "● this row ends with a well-known word", columns: 28);
    }

    [Fact]
    public void ALongTokenCutAtTheEdge_IsJoinedWithoutASpace()
    {
        Check("  https://example.com/a/very/long\r\n  /path/continues", "  https://example.com/a/very/long/path/continues", columns: 34);
    }

    [Fact]
    public void WithoutAKnownWidth_RowsAreNeverJoined_ButTheMarginIsStillStripped()
    {
        var rows = Wrap(Sentence, 30);
        var expected = rows.Select((r, i) => i == 0 ? r : r[2..]);

        Check(string.Join("\r\n", rows), string.Join("\r\n", expected));
    }

    [Fact]
    public void ARectangularSelection_IsNeverJoined()
    {
        var rows = Wrap(Sentence, 30);
        var input = string.Join("\n", rows);
        Check(input, string.Join("\r\n", rows), rectangular: true, columns: 30);
    }

    [Fact]
    public void TheScriptIsServedByTheTerminalPage_BeforeTerminalJs()
    {
        var script = FindScript();
        Assert.NotNull(script);

        var html = File.ReadAllText(Path.Combine(Path.GetDirectoryName(script!)!, "index.html"));

        // The script tags, not the prose: the page's comments also mention terminal.js.
        var copyAt = html.IndexOf("accel-terminal/copytext.js", StringComparison.Ordinal);
        var terminalAt = html.IndexOf("accel-terminal/terminal.js", StringComparison.Ordinal);
        Assert.True(copyAt >= 0 && terminalAt > copyAt, "copytext.js must load before terminal.js");
    }
}

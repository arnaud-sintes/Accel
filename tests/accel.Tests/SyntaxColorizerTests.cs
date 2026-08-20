using System;
using System.Linq;
using Accel.App.Services;
using Xunit;

namespace Accel.Tests;

/// <summary>
/// Unit tests for the pure "tokens -> per-line spans" half of panel D's AvalonEdit colouriser
/// (<see cref="SyntaxLineSpanMapper"/>). The colouriser itself is a
/// <c>DocumentColorizingTransformer</c> driven by AvalonEdit's render path and needs a live
/// TextView, so it is not unit-tested here; the mapping is where the offset arithmetic (and hence
/// the risk of mis-coloured or out-of-range spans) actually lives, which is exactly why it was split
/// out as a static, WPF-free class. <c>SyntaxHighlighterTests</c> covers the tokenizer itself and is
/// intentionally untouched: <see cref="SyntaxHighlighter"/> stays the single source of colour truth.
/// </summary>
public class SyntaxColorizerTests
{
    private const string Red = "#FFFF0000";
    private const string Blue = "#FF0000FF";

    [Fact]
    public void Map_SingleLine_ReturnsOneLineWithOffsetsRelativeToTheLineStart()
    {
        var lines = SyntaxLineSpanMapper.Map(new[]
        {
            new SyntaxToken("abc", null),
            new SyntaxToken("de", Red),
            new SyntaxToken("f", null),
        });

        var line = Assert.Single(lines);
        var span = Assert.Single(line);
        Assert.Equal(new SyntaxLineSpan(3, 2, Red), span);
    }

    [Fact]
    public void Map_AlwaysReturnsOneEntryPerLineIncludingEmptyAndTrailingOnes()
    {
        // "a\n\nb\n" is four lines: "a", "", "b" and the empty line after the final newline.
        var lines = SyntaxLineSpanMapper.Map(new[] { new SyntaxToken("a\n\nb\n", null) });

        Assert.Equal(4, lines.Count);
        Assert.All(lines, l => Assert.Empty(l));
    }

    [Fact]
    public void Map_TokenStraddlingNewlines_ContributesOneSpanPerLineItCovers()
    {
        // The shape a block comment or a triple-quoted string produces: one token, many lines.
        var lines = SyntaxLineSpanMapper.Map(new[]
        {
            new SyntaxToken("x = ", null),
            new SyntaxToken("/* one\ntwo\nthree */", Red),
            new SyntaxToken(";", null),
        });

        Assert.Equal(3, lines.Count);
        Assert.Equal(new SyntaxLineSpan(4, 6, Red), Assert.Single(lines[0]));   // "/* one"
        Assert.Equal(new SyntaxLineSpan(0, 3, Red), Assert.Single(lines[1]));   // "two"
        Assert.Equal(new SyntaxLineSpan(0, 8, Red), Assert.Single(lines[2]));   // "three */"
    }

    [Fact]
    public void Map_ColumnRestartsAtZeroOnEveryLine()
    {
        var lines = SyntaxLineSpanMapper.Map(new[]
        {
            new SyntaxToken("aa", Red),
            new SyntaxToken("\nbbb", null),
            new SyntaxToken("cc", Blue),
        });

        Assert.Equal(2, lines.Count);
        Assert.Equal(new SyntaxLineSpan(0, 2, Red), Assert.Single(lines[0]));
        Assert.Equal(new SyntaxLineSpan(3, 2, Blue), Assert.Single(lines[1]));
    }

    [Fact]
    public void Map_UncolouredAndEmptyTokensProduceNoSpans()
    {
        var lines = SyntaxLineSpanMapper.Map(new[]
        {
            new SyntaxToken(string.Empty, Red),
            new SyntaxToken("plain", null),
        });

        Assert.Empty(Assert.Single(lines));
    }

    [Fact]
    public void Map_RealTokenizerOutput_SpansLineUpWithTheSourceText()
    {
        const string source = "// hi\nint n = 42;\n";
        var lines = SyntaxLineSpanMapper.Map(SyntaxHighlighter.Tokenize(source, SourceLanguage.CLike));
        string[] rawLines = source.Split('\n');

        Assert.Equal(rawLines.Length, lines.Count);

        // Every span must be substring-addressable inside its own line - the invariant
        // SyntaxColorizer.ColorizeLine relies on when it turns a span into a document offset range.
        for (int i = 0; i < lines.Count; i++)
        {
            foreach (var span in lines[i])
            {
                Assert.InRange(span.Start, 0, rawLines[i].Length);
                Assert.InRange(span.Start + span.Length, 0, rawLines[i].Length);
            }
        }

        Assert.Equal("// hi", rawLines[0][lines[0].Single().Start..]);

        // "int n = 42;" colours two runs: the keyword and the number.
        Assert.Equal(2, lines[1].Length);
        Assert.Equal("int", rawLines[1].Substring(lines[1][0].Start, lines[1][0].Length));
        Assert.Equal("42", rawLines[1].Substring(lines[1][1].Start, lines[1][1].Length));
    }

    [Fact]
    public void Map_NullTokens_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SyntaxLineSpanMapper.Map(null!));
    }
}

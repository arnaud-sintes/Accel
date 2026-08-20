using System.Linq;
using Accel.App.Services;
using Xunit;

namespace Accel.Tests;

/// <summary>
/// Pins down <see cref="SourceLanguageResolver"/>'s extension bucketing and
/// <see cref="SyntaxHighlighter"/>'s token colouring for panel D's file editor/viewer - not
/// exhaustive per-language grammar coverage (this is deliberately a regex approximation, not a real
/// parser), just enough to catch a broken pattern (e.g. an unbalanced group) or a language wired to
/// the wrong bucket.
/// </summary>
public class SyntaxHighlighterTests
{
    [Theory]
    [InlineData("Program.cs", SourceLanguage.CLike)]
    [InlineData("app.tsx", SourceLanguage.CLike)]
    [InlineData("script.py", SourceLanguage.Python)]
    [InlineData("data.json", SourceLanguage.Json)]
    [InlineData("index.html", SourceLanguage.Markup)]
    [InlineData("values.yaml", SourceLanguage.Yaml)]
    [InlineData("README.md", SourceLanguage.Markdown)]
    [InlineData("deploy.sh", SourceLanguage.Shell)]
    [InlineData("notes.txt", SourceLanguage.PlainText)]
    [InlineData("no-extension", SourceLanguage.PlainText)]
    public void Resolve_MapsExtensionToExpectedLanguage(string fileName, SourceLanguage expected)
    {
        Assert.Equal(expected, SourceLanguageResolver.Resolve(fileName));
    }

    [Fact]
    public void Tokenize_CLike_ColorsCommentStringNumberAndKeywordDistinctly()
    {
        var tokens = SyntaxHighlighter.Tokenize("// hi\nif (x == 1) { var s = \"a\"; }", SourceLanguage.CLike);

        Assert.Contains(tokens, t => t.Text == "// hi" && t.ColorHex is not null);
        Assert.Contains(tokens, t => t.Text == "\"a\"" && t.ColorHex is not null);
        Assert.Contains(tokens, t => t.Text == "1" && t.ColorHex is not null);
        Assert.Contains(tokens, t => t.Text == "if" && t.ColorHex is not null);

        // Distinct categories must not collapse onto the same colour, or the whole point (telling
        // comments/strings/numbers/keywords apart at a glance) is lost.
        var commentColor = tokens.First(t => t.Text == "// hi").ColorHex;
        var stringColor = tokens.First(t => t.Text == "\"a\"").ColorHex;
        var numberColor = tokens.First(t => t.Text == "1").ColorHex;
        var keywordColor = tokens.First(t => t.Text == "if").ColorHex;
        Assert.Equal(4, new[] { commentColor, stringColor, numberColor, keywordColor }.Distinct().Count());
    }

    [Fact]
    public void Tokenize_PlainText_ReturnsWholeContentUncolored()
    {
        var tokens = SyntaxHighlighter.Tokenize("just some notes", SourceLanguage.PlainText);

        var token = Assert.Single(tokens);
        Assert.Equal("just some notes", token.Text);
        Assert.Null(token.ColorHex);
    }

    [Fact]
    public void Tokenize_ReassemblesToOriginalContent()
    {
        const string source = "def f(x):\n    # comment\n    return x + 1\n";
        var tokens = SyntaxHighlighter.Tokenize(source, SourceLanguage.Python);

        Assert.Equal(source, string.Concat(tokens.Select(t => t.Text)));
    }

    [Fact]
    public void Tokenize_OversizedContent_SkipsHighlightingEntirely()
    {
        string huge = new string('a', SyntaxHighlighter.MaxHighlightedLength + 1);

        var tokens = SyntaxHighlighter.Tokenize(huge, SourceLanguage.CLike);

        var token = Assert.Single(tokens);
        Assert.Equal(huge, token.Text);
        Assert.Null(token.ColorHex);
    }

    [Fact]
    public void Tokenize_Json_ColorsKeysAndLiterals()
    {
        var tokens = SyntaxHighlighter.Tokenize("{\"a\": true, \"b\": 2}", SourceLanguage.Json);

        Assert.Contains(tokens, t => t.Text == "\"a\"" && t.ColorHex is not null);
        Assert.Contains(tokens, t => t.Text == "true" && t.ColorHex is not null);
        Assert.Contains(tokens, t => t.Text == "2" && t.ColorHex is not null);
    }
}

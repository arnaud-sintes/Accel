namespace Glaude.Tests;

using Glaude.App.Services;
using Xunit;

/// <summary>
/// P2-T6: unit tests for <see cref="ExtraArgsParser"/> - the tokenizer that turns the "Create
/// session" dialog's free-text extra-CLI-args field into a real argv array. The load-bearing case is
/// <see cref="QuotedValueContainingASpace_IsNotReSplit"/>: a naive <c>text.Split(' ')</c> would break
/// exactly this case, which is the class of bug the plan's array-not-a-string requirement exists to
/// prevent.
/// </summary>
public class ExtraArgsParserTests
{
    [Fact]
    public void NullOrWhitespaceInput_YieldsEmptyArray()
    {
        Assert.Empty(ExtraArgsParser.Parse(null));
        Assert.Empty(ExtraArgsParser.Parse(string.Empty));
        Assert.Empty(ExtraArgsParser.Parse("   \t  "));
    }

    [Fact]
    public void PlainTokens_SplitOnWhitespace()
    {
        var tokens = ExtraArgsParser.Parse("--permission-mode bypassPermissions");
        Assert.Equal(new[] { "--permission-mode", "bypassPermissions" }, tokens);
    }

    [Fact]
    public void MultipleWhitespaceRuns_CollapseToOneSeparator()
    {
        var tokens = ExtraArgsParser.Parse("  --foo   bar\tbaz\n qux ");
        Assert.Equal(new[] { "--foo", "bar", "baz", "qux" }, tokens);
    }

    /// <summary>
    /// The load-bearing case: a quoted value containing a space must survive as ONE array element,
    /// not be re-split on the space it contains.
    /// </summary>
    [Fact]
    public void QuotedValueContainingASpace_IsNotReSplit()
    {
        var tokens = ExtraArgsParser.Parse("--name \"hello world\" --flag");
        Assert.Equal(new[] { "--name", "hello world", "--flag" }, tokens);

        // The naive approach really would break this - proves the test is exercising something real.
        var naive = "--name \"hello world\" --flag".Split(' ');
        Assert.NotEqual(tokens, naive);
    }

    [Fact]
    public void DoubledQuoteInsideQuotes_IsALiteralQuote()
    {
        var tokens = ExtraArgsParser.Parse("--name \"say \"\"hi\"\"\"");
        Assert.Equal(new[] { "--name", "say \"hi\"" }, tokens);
    }

    [Fact]
    public void EmptyQuotedToken_ProducesAnEmptyStringElement()
    {
        var tokens = ExtraArgsParser.Parse("--name \"\" --flag");
        Assert.Equal(new[] { "--name", string.Empty, "--flag" }, tokens);
    }

    [Fact]
    public void UnterminatedQuote_RunsToEndOfString()
    {
        var tokens = ExtraArgsParser.Parse("--name \"unterminated tail");
        Assert.Equal(new[] { "--name", "unterminated tail" }, tokens);
    }
}

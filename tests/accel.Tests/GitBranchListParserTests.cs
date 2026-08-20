namespace Accel.Tests;

using Accel.Cli;
using Xunit;

public sealed class GitBranchListParserTests
{
    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(GitBranchListParser.Parse(string.Empty));
    }

    [Fact]
    public void ParsesOneBranchPerLine()
    {
        string[] branches = GitBranchListParser.Parse("main\r\nfeature/foo\r\n");

        Assert.Equal(new[] { "main", "feature/foo" }, branches);
    }

    [Fact]
    public void SkipsBlankLines()
    {
        string[] branches = GitBranchListParser.Parse("main\n\nfeature/foo\n\n");

        Assert.Equal(new[] { "main", "feature/foo" }, branches);
    }
}

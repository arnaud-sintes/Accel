namespace Accel.Tests;

using Accel.App.Services;
using Xunit;

public class ProjectPathsTests
{
    [Theory]
    [InlineData(@"C:\projects\app", @"C:\projects\app")]
    [InlineData(@"C:\projects\app\", @"C:\projects\app")]
    [InlineData("C:/projects/app/", @"C:\projects\app")]
    [InlineData(@"C:\", @"C:\")]
    public void Normalize_UsesOneSeparatorStyleAndDropsTheTrailingSeparator(string input, string expected)
    {
        Assert.Equal(expected, ProjectPaths.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_ReadsBlankAsNull(string? input)
    {
        Assert.Null(ProjectPaths.Normalize(input));
    }

    [Fact]
    public void AreSame_IgnoresCaseSeparatorsAndTrailingSlash()
    {
        Assert.True(ProjectPaths.AreSame(@"C:\Projects\App", "c:/projects/app/"));
        Assert.False(ProjectPaths.AreSame(@"C:\projects\app", @"C:\projects\other"));
        Assert.False(ProjectPaths.AreSame(null, @"C:\projects\app"));
    }

    [Fact]
    public void IsSameOrUnder_MatchesWholeSegmentsOnly()
    {
        Assert.True(ProjectPaths.IsSameOrUnder(@"C:\projects\app", @"C:\projects\app"));
        Assert.True(ProjectPaths.IsSameOrUnder(@"C:\projects\app\src\Program.cs", @"C:\projects\app"));
        Assert.True(ProjectPaths.IsSameOrUnder(@"C:\projects\app\src", "C:/projects/app/"));
        Assert.False(ProjectPaths.IsSameOrUnder(@"C:\projects\app-old\x.cs", @"C:\projects\app"));
        Assert.False(ProjectPaths.IsSameOrUnder(@"C:\projects", @"C:\projects\app"));
    }

    [Fact]
    public void LongestContainingRoot_PrefersTheNestedRoot_AndIsNullWhenNothingContainsThePath()
    {
        var roots = new[] { @"C:\projects", @"C:\projects\app", @"D:\other" };

        Assert.Equal(@"C:\projects\app", ProjectPaths.LongestContainingRoot(@"C:\projects\app\src\a.cs", roots));
        Assert.Equal(@"C:\projects", ProjectPaths.LongestContainingRoot(@"C:\projects\tools\a.cs", roots));
        Assert.Null(ProjectPaths.LongestContainingRoot(@"E:\elsewhere\a.cs", roots));
    }
}

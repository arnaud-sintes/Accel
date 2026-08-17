namespace Accel.Tests;

using Accel.App.ViewModels;
using Xunit;

/// <summary>
/// Unit tests for the pure, WPF-free <see cref="AgentGraphLayout.Compute"/> - panel E's horizontal
/// left-to-right, column-major layout math (design doc "claude-agentgraph.md" §7.2/§7.3). No WPF
/// type appears anywhere here; this runs in the plain xUnit process, same as
/// <c>SessionVisualStateResolverTests</c>/<c>ModelBadgeTableTests</c>.
/// </summary>
public class AgentGraphLayoutTests
{
    /// <summary>
    /// <see cref="AgentGraphLayoutOptions"/>' documented defaults (176/52/56/10/12), built via the
    /// primary constructor with explicit named values - deliberately NOT <c>new AgentGraphLayoutOptions()</c>,
    /// which (being a struct) always invokes the compiler-synthesized, field-zeroing parameterless
    /// constructor rather than the primary constructor's default parameter values.
    /// </summary>
    private static AgentGraphLayoutOptions DocumentedDefaults() => new(
        CardWidth: 176.0, CardHeight: 52.0, ColumnGap: 56.0, RowGap: 10.0, Padding: 12.0);


    [Fact]
    public void Compute_ZeroChildren_YieldsNoColumnsAndParentOnlyWidth()
    {
        var result = AgentGraphLayout.Compute(0, 160);

        Assert.Equal(0, result.ColumnCount);
        Assert.Empty(result.Children);
        Assert.Empty(result.Edges);
        Assert.Equal(12 + 176 + 12, result.ContentWidth);
    }

    [Fact]
    public void Compute_OneChild_AlignsChildVerticallyWithParent()
    {
        var result = AgentGraphLayout.Compute(1, 160);

        Assert.Single(result.Children);
        Assert.Equal(result.Parent.Y, result.Children[0].Y);
    }

    [Fact]
    public void Compute_ChildrenFitInOneColumn_AreStackedWithRowGap()
    {
        var options = DocumentedDefaults();
        var result = AgentGraphLayout.Compute(2, 500, options);

        Assert.Equal(1, result.ColumnCount);
        Assert.Equal(result.Children[0].X, result.Children[1].X);
        Assert.Equal(result.Children[0].Y + options.CardHeight + options.RowGap, result.Children[1].Y);
    }

    [Fact]
    public void Compute_MoreChildrenThanRowsPerColumn_WrapsIntoASecondColumnToTheRight()
    {
        // At MinHeight=64: rowsPerColumn == 1 (per the design doc's own worked example), so a 2nd
        // child must wrap into a second, rightward column.
        var result = AgentGraphLayout.Compute(2, 64);

        Assert.Equal(1, result.RowsPerColumn);
        Assert.Equal(2, result.ColumnCount);
        Assert.True(result.Children[1].X > result.Children[0].X);
        Assert.Equal(result.Children[0].Y, result.Children[1].Y);
    }

    [Fact]
    public void Compute_ShortPanel_YieldsOneRowPerColumn()
    {
        var result = AgentGraphLayout.Compute(1, 64);
        Assert.Equal(1, result.RowsPerColumn);
    }

    [Fact]
    public void Compute_TallerPanel_YieldsMoreRowsPerColumn()
    {
        // Pins the exact numbers §7.2 used to choose MainWindow.xaml's RowDefinition bounds.
        Assert.Equal(2, AgentGraphLayout.Compute(1, 160).RowsPerColumn);
        Assert.Equal(3, AgentGraphLayout.Compute(1, 220).RowsPerColumn);
    }

    [Fact]
    public void Compute_TrailingPartialColumn_IsVerticallyCentredIndependently()
    {
        // rowsPerColumn == 2 at height 160; 3 children -> column 0 has 2 rows (full block), column 1
        // has 1 row (a partial, independently-centred block).
        var result = AgentGraphLayout.Compute(3, 160);

        Assert.Equal(2, result.RowsPerColumn);
        Assert.Equal(2, result.ColumnCount);

        var options = DocumentedDefaults();
        double fullColumnTop = System.Math.Max(options.Padding, (160 - ((2 * options.CardHeight) + options.RowGap)) / 2.0);
        double partialColumnTop = System.Math.Max(options.Padding, (160 - options.CardHeight) / 2.0);

        Assert.Equal(fullColumnTop, result.Children[0].Y);
        Assert.Equal(partialColumnTop, result.Children[2].Y);
    }

    [Fact]
    public void Compute_ContentWidth_GrowsOnePitchPerColumn()
    {
        var options = DocumentedDefaults();
        var oneColumn = AgentGraphLayout.Compute(1, 500, options);
        var twoColumns = AgentGraphLayout.Compute((int)(500 / (options.CardHeight + options.RowGap)) + 1, 500, options);

        Assert.True(twoColumns.ColumnCount > oneColumn.ColumnCount);
        Assert.Equal(
            oneColumn.ContentWidth + ((twoColumns.ColumnCount - oneColumn.ColumnCount) * (options.ColumnGap + options.CardWidth)),
            twoColumns.ContentWidth);
    }

    [Fact]
    public void Compute_NegativeChildCountOrDegenerateHeight_ClampsAndDoesNotThrow()
    {
        var negative = AgentGraphLayout.Compute(-5, 160);
        Assert.Equal(0, negative.ColumnCount);

        var badHeight = AgentGraphLayout.Compute(2, double.NaN);
        Assert.True(badHeight.RowsPerColumn >= 1);

        var tinyHeight = AgentGraphLayout.Compute(2, 1);
        Assert.True(tinyHeight.RowsPerColumn >= 1);
    }

    [Fact]
    public void Compute_DefaultOptions_AreSubstitutedForZeroMembers()
    {
        var defaultOptions = AgentGraphLayout.Compute(0, 160, default);
        var explicitDefaults = AgentGraphLayout.Compute(0, 160, DocumentedDefaults());

        Assert.Equal(explicitDefaults.ContentWidth, defaultOptions.ContentWidth);
        Assert.Equal(explicitDefaults.Parent, defaultOptions.Parent);
    }

    [Fact]
    public void Compute_Edge_AnchorsAtParentRightEdgeAndChildLeftEdge()
    {
        var result = AgentGraphLayout.Compute(1, 160);
        var edge = result.Edges[0];

        Assert.Equal(result.Parent.X + result.Parent.Width, edge.StartX);
        Assert.Equal(result.Parent.Y + (result.Parent.Height / 2.0), edge.StartY);
        Assert.Equal(result.Children[0].X, edge.EndX);
        Assert.Equal(result.Children[0].Y + (result.Children[0].Height / 2.0), edge.EndY);
    }

    [Fact]
    public void Compute_Edge_ControlPointsShareTheirAnchorY()
    {
        var result = AgentGraphLayout.Compute(3, 160);

        foreach (var edge in result.Edges)
        {
            Assert.Equal(edge.StartY, edge.C1Y);
            Assert.Equal(edge.EndY, edge.C2Y);
        }
    }

    [Theory]
    [InlineData(10.0)]   // short hop -> clamps to the lower bound (24)
    [InlineData(100.0)]  // medium hop -> unclamped (dx*0.5)
    [InlineData(1000.0)] // long hop -> clamps to the upper bound (96)
    public void Compute_Edge_ControlPointOffsetIsClampedBetween24And96(double columnGap)
    {
        // ColumnGap is the tunable that directly drives dx for a single-column, single-child layout
        // (dx == ColumnGap here, since parent/child card widths are fixed) - real Compute() output,
        // not a re-derivation of the clamp formula under test.
        var options = new AgentGraphLayoutOptions(ColumnGap: columnGap);
        var result = AgentGraphLayout.Compute(1, 160, options);
        var edge = result.Edges[0];

        double k = edge.C1X - edge.StartX;
        double expected = System.Math.Clamp(columnGap * 0.5, 24.0, 96.0);

        Assert.InRange(k, 24.0, 96.0);
        Assert.Equal(expected, k, 3);
        Assert.Equal(expected, edge.EndX - edge.C2X, 3);
    }

    [Fact]
    public void Compute_SameYChildEdge_IsAStraightHorizontalCurve()
    {
        var result = AgentGraphLayout.Compute(1, 160);
        var edge = result.Edges[0];

        Assert.Equal(edge.StartY, edge.EndY);
        Assert.Equal(edge.StartY, edge.C1Y);
        Assert.Equal(edge.StartY, edge.C2Y);
    }
}

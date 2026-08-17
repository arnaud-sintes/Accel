namespace Accel.App.ViewModels;

using System;

/// <summary>
/// Panel E's tunable geometry constants (design doc "claude-agentgraph.md" §7.2). A
/// <c>readonly record struct</c> - deliberately WPF-free, like every other member of this file -
/// so tests can drive degenerate/non-default values without touching <c>AgentGraphControl</c>.
/// <c>default(AgentGraphLayoutOptions)</c> has every member zeroed; <see cref="AgentGraphLayout.Compute"/>
/// substitutes the documented defaults below for any non-positive member (see its own remarks).
/// </summary>
/// <param name="CardWidth">Fits the badge + effort ring + a trimmed name on row 1 and the detail
/// line ("7m 04s · 148.2K · 12.3%") on row 2 at <c>FontSizeCaption</c>.</param>
/// <param name="CardHeight">Two text rows at 14/12px plus 8px vertical padding, on the 4px spacing grid.</param>
/// <param name="ColumnGap">The horizontal run a bezier connector needs to read as a curve rather than a kink.</param>
/// <param name="RowGap">Approximately <c>SpacingSm</c>, deliberately distinct from <see cref="ColumnGap"/>.</param>
/// <param name="Padding">Approximately <c>SpacingMd</c>, applied uniformly on every edge.</param>
public readonly record struct AgentGraphLayoutOptions(
    double CardWidth = 176.0,
    double CardHeight = 52.0,
    double ColumnGap = 56.0,
    double RowGap = 10.0,
    double Padding = 12.0);

/// <summary>One card's resolved position/size, indexed by its position in the caller's child list
/// (or <c>-1</c> for the parent, which <see cref="AgentGraphLayoutResult"/> carries separately).</summary>
public readonly record struct AgentGraphNodeRect(int Index, double X, double Y, double Width, double Height);

/// <summary>One bezier connector's anchor and control points, per the design's control-point
/// formula (§7.3): both control points share their anchor's Y, so the curve leaves the parent and
/// enters the child horizontally.</summary>
public readonly record struct AgentGraphEdge(
    int ChildIndex,
    double StartX, double StartY,
    double C1X, double C1Y,
    double C2X, double C2Y,
    double EndX, double EndY);

/// <summary>The whole computed layout for one parent + its children.</summary>
public sealed record AgentGraphLayoutResult(
    AgentGraphNodeRect Parent,
    AgentGraphNodeRect[] Children,
    AgentGraphEdge[] Edges,
    double ContentWidth,
    double ContentHeight,
    int RowsPerColumn,
    int ColumnCount);

/// <summary>
/// Pure, WPF-free horizontal left-to-right, column-major tree layout for panel E (design doc
/// §7.2/§7.3). No <c>System.Windows</c> type appears anywhere in this file - the split
/// <c>CLAUDE_DESIGN.md</c> §5 describes for <c>SessionVisualStateResolver</c>/<c>ModelBadgeTable</c>/
/// <c>EffortBarLevel</c>, applied to layout arithmetic so it is unit-testable in a non-STA xUnit
/// process.
/// </summary>
public static class AgentGraphLayout
{
    /// <summary>Lower/upper clamp for a connector's control-point X offset from its anchor (§7.3):
    /// the lower bound keeps a short hop curved, the upper bound stops a far-right column's edge
    /// from ballooning into a flat, unreadable arc.</summary>
    private const double MinControlOffset = 24.0;
    private const double MaxControlOffset = 96.0;

    /// <summary>
    /// Computes the parent card, every child card, and the bezier edge from the parent to each
    /// child, for a panel whose available height is <paramref name="availableHeight"/>. Never
    /// throws: <paramref name="childCount"/> below zero clamps to zero, a non-finite or too-small
    /// <paramref name="availableHeight"/> clamps to the minimum a single row needs, and
    /// <paramref name="options"/> left at <c>default</c> (all-zero members) is normalized to the
    /// documented defaults.
    /// </summary>
    public static AgentGraphLayoutResult Compute(int childCount, double availableHeight, AgentGraphLayoutOptions options = default)
    {
        var o = Normalize(options);
        int count = Math.Max(0, childCount);
        double height = NormalizeHeight(availableHeight, o);

        int rowsPerColumn = Math.Max(1, (int)Math.Floor((height - (2 * o.Padding) + o.RowGap) / (o.CardHeight + o.RowGap)));
        int columnCount = count == 0 ? 0 : (int)Math.Ceiling(count / (double)rowsPerColumn);

        double parentX = o.Padding;
        double parentY = Math.Max(o.Padding, (height - o.CardHeight) / 2.0);
        var parent = new AgentGraphNodeRect(-1, parentX, parentY, o.CardWidth, o.CardHeight);

        var children = new AgentGraphNodeRect[count];
        var edges = new AgentGraphEdge[count];

        for (int i = 0; i < count; i++)
        {
            int c = i / rowsPerColumn;
            int r = i % rowsPerColumn;
            int countInColumn = Math.Min(rowsPerColumn, count - (c * rowsPerColumn));
            double blockHeight = (countInColumn * o.CardHeight) + ((countInColumn - 1) * o.RowGap);
            double columnTop = Math.Max(o.Padding, (height - blockHeight) / 2.0);

            double childX = o.Padding + o.CardWidth + o.ColumnGap + (c * (o.CardWidth + o.ColumnGap));
            double childY = columnTop + (r * (o.CardHeight + o.RowGap));

            children[i] = new AgentGraphNodeRect(i, childX, childY, o.CardWidth, o.CardHeight);
            edges[i] = ComputeEdge(i, parent, children[i]);
        }

        double contentWidth = o.Padding + o.CardWidth + (columnCount * (o.ColumnGap + o.CardWidth)) + o.Padding;
        double contentHeight = Math.Max(height, o.Padding + o.CardHeight + o.Padding);

        return new AgentGraphLayoutResult(parent, children, edges, contentWidth, contentHeight, rowsPerColumn, columnCount);
    }

    /// <summary>The control-point formula from §7.3: both control points share their anchor's Y, so
    /// the curve leaves the parent's right edge and enters the child's left edge horizontally - a
    /// same-Y parent/child pair (the one-child case) therefore degenerates to a straight horizontal
    /// line, not a special case in this code.</summary>
    private static AgentGraphEdge ComputeEdge(int childIndex, AgentGraphNodeRect parent, AgentGraphNodeRect child)
    {
        double startX = parent.X + parent.Width;
        double startY = parent.Y + (parent.Height / 2.0);
        double endX = child.X;
        double endY = child.Y + (child.Height / 2.0);

        double dx = endX - startX;
        double k = Math.Clamp(dx * 0.5, MinControlOffset, MaxControlOffset);

        double c1X = startX + k;
        double c1Y = startY;
        double c2X = endX - k;
        double c2Y = endY;

        return new AgentGraphEdge(childIndex, startX, startY, c1X, c1Y, c2X, c2Y, endX, endY);
    }

    // NOTE: deliberately NOT "new AgentGraphLayoutOptions()" - for a struct (record structs
    // included), "new S()" always binds to the compiler-synthesized, field-zeroing parameterless
    // constructor, never to a user-declared primary constructor's default *values*, even when every
    // primary-constructor parameter has a default. Named constants are the only reliable way to get
    // AgentGraphLayoutOptions' documented defaults (176/52/56/10/12) back out once a caller has
    // already collapsed them to `default` (all-zero).
    private const double DefaultCardWidth = 176.0;
    private const double DefaultCardHeight = 52.0;
    private const double DefaultColumnGap = 56.0;
    private const double DefaultRowGap = 10.0;
    private const double DefaultPadding = 12.0;

    private static AgentGraphLayoutOptions Normalize(AgentGraphLayoutOptions options) => new(
        CardWidth: options.CardWidth > 0 ? options.CardWidth : DefaultCardWidth,
        CardHeight: options.CardHeight > 0 ? options.CardHeight : DefaultCardHeight,
        ColumnGap: options.ColumnGap > 0 ? options.ColumnGap : DefaultColumnGap,
        RowGap: options.RowGap > 0 ? options.RowGap : DefaultRowGap,
        Padding: options.Padding > 0 ? options.Padding : DefaultPadding);

    private static double NormalizeHeight(double availableHeight, AgentGraphLayoutOptions o)
    {
        double minimum = (2 * o.Padding) + o.CardHeight;
        return double.IsFinite(availableHeight) && availableHeight >= minimum ? availableHeight : minimum;
    }
}

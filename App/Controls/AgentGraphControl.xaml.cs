namespace Accel.App.Controls;

using System;
using System.Collections;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Accel.App.ViewModels;

/// <summary>
/// Panel E's control (design doc "claude-agentgraph.md" §7.2/§7.3/§7.7): renders the cards declared
/// in <c>AgentGraphControl.xaml</c>'s <c>DataTemplate</c> over a <c>Canvas</c>, plus the bezier
/// connectors built here in code - the same split <see cref="EffortBarsControl"/> uses (XAML for
/// the invariant part, code-behind for geometry that depends on measured/computed positions).
///
/// <para><see cref="Relayout"/> is the single entry point, called from <c>Loaded</c>,
/// <c>SizeChanged</c>, and whenever the bound <see cref="Nodes"/> collection raises
/// <see cref="INotifyCollectionChanged.CollectionChanged"/> - never from a fourth, divergent code
/// path.</para>
/// </summary>
public partial class AgentGraphControl : UserControl
{
    public static readonly DependencyProperty NodesProperty = DependencyProperty.Register(
        nameof(Nodes),
        typeof(IEnumerable),
        typeof(AgentGraphControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnNodesChanged));

    private INotifyCollectionChanged? _hookedCollection;

    public AgentGraphControl()
    {
        InitializeComponent();
        Loaded += (_, _) => Relayout();
        SizeChanged += (_, _) => Relayout();
    }

    public IEnumerable? Nodes
    {
        get => (IEnumerable?)GetValue(NodesProperty);
        set => SetValue(NodesProperty, value);
    }

    /// <summary>Re-hooks <see cref="INotifyCollectionChanged"/> (unsubscribing the old value - the
    /// one leak this control can have) and relayouts.</summary>
    private static void OnNodesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not AgentGraphControl control)
        {
            return;
        }

        if (control._hookedCollection is not null)
        {
            control._hookedCollection.CollectionChanged -= control.OnNodesCollectionChanged;
            control._hookedCollection = null;
        }

        if (e.NewValue is INotifyCollectionChanged incc)
        {
            incc.CollectionChanged += control.OnNodesCollectionChanged;
            control._hookedCollection = incc;
        }

        control.Relayout();
    }

    private void OnNodesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Relayout();

    /// <summary>
    /// Recomputes every card's <c>X</c>/<c>Y</c> via <see cref="AgentGraphLayout.Compute"/> against
    /// this control's current <see cref="FrameworkElement.ActualHeight"/>, and rebuilds every bezier
    /// connector wholesale (correct at this scale - a session has single-digit live agents; if the
    /// count ever grows, the fix is pooling <see cref="Path"/> instances here, not a second update
    /// path).
    /// </summary>
    public void Relayout()
    {
        var nodes = Nodes?.Cast<AgentGraphNodeViewModel>().ToArray() ?? Array.Empty<AgentGraphNodeViewModel>();

        ConnectorLayer.Children.Clear();

        var parent = nodes.FirstOrDefault(n => n.Role == AgentGraphNodeRole.Parent);
        if (parent is null)
        {
            ContentHost.Width = double.NaN;
            ContentHost.Height = double.NaN;
            return;
        }

        var children = nodes.Where(n => n.Role == AgentGraphNodeRole.Child).ToArray();
        double height = ActualHeight > 0 ? ActualHeight : MinHeight;
        var result = AgentGraphLayout.Compute(children.Length, height);

        parent.X = result.Parent.X;
        parent.Y = result.Parent.Y;

        for (int i = 0; i < children.Length; i++)
        {
            children[i].X = result.Children[i].X;
            children[i].Y = result.Children[i].Y;

            var path = BuildConnector(result.Edges[i]);
            if (children[i].IsRunning)
            {
                // A live agent's edge is visibly warmer, without becoming the only signal for that
                // state (§7.6) - IsRunning is still carried by the card's own glyph/weight/colour.
                path.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            }

            ConnectorLayer.Children.Add(path);
        }

        ContentHost.Width = result.ContentWidth;
        ContentHost.Height = result.ContentHeight;
    }

    /// <summary>Builds one connector's geometry from an already-computed <see cref="AgentGraphEdge"/> -
    /// same <c>PathFigure</c> + <c>BezierSegment</c> + <c>PathGeometry</c> pattern as
    /// <see cref="EffortBarsControl.xaml.cs"/>'s <c>BuildArcGeometry</c>. Marked non-accessible
    /// (§7.6): a bezier conveys nothing to a screen reader, and the parent/child relationship it
    /// draws is instead carried textually by <see cref="AgentGraphNodeViewModel.AutomationDescription"/>'s
    /// "child of session ..." clause.</summary>
    private Shape BuildConnector(AgentGraphEdge edge)
    {
        var figure = new PathFigure { StartPoint = new Point(edge.StartX, edge.StartY), IsClosed = false };
        figure.Segments.Add(new BezierSegment(
            new Point(edge.C1X, edge.C1Y),
            new Point(edge.C2X, edge.C2Y),
            new Point(edge.EndX, edge.EndY),
            isStroked: true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        var path = new NonAccessiblePath(geometry)
        {
            IsHitTestVisible = false,
            Focusable = false,
        };

        if (TryFindResource("AgentGraphConnectorPathStyle") is Style style)
        {
            path.Style = style;
        }

        return path;
    }

    /// <summary>
    /// A bezier connector, deliberately equivalent to a <see cref="Path"/> whose <c>Data</c> is
    /// <paramref name="Geometry"/> - not a subclass of <see cref="Path"/> itself, because
    /// <see cref="Path"/> is <c>sealed</c> in WPF. Overrides <see cref="DefiningGeometry"/> instead of
    /// setting <c>Path.Data</c>, and <see cref="OnCreateAutomationPeer"/> to suppress this element's
    /// automation peer entirely (§7.6's "never appears as an unnamed element in the automation tree"
    /// rule) - WPF has no <c>AutomationProperties.AccessibilityView</c> attached property (that API is
    /// UWP/WinUI-only; the design doc's assumption did not match the actual WPF automation surface),
    /// so returning a null peer is the equivalent WPF mechanism.
    /// </summary>
    private sealed class NonAccessiblePath : Shape
    {
        private readonly Geometry _geometry;

        public NonAccessiblePath(Geometry geometry) => _geometry = geometry;

        protected override Geometry DefiningGeometry => _geometry;

        protected override AutomationPeer OnCreateAutomationPeer() => null!;
    }
}

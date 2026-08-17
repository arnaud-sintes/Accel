namespace Accel.App.Controls;

using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Accel.Metrics;

/// <summary>
/// P1-T4's effort badge, revised to a radial ring gauge: <see cref="Level"/> (0-4, straight from
/// <see cref="EffortBarLevel.Resolve(string?)"/>) fills <c>TrackRing</c>'s outline clockwise from
/// the top by one quarter-turn per level, except level 4 (max) which renders as a solid filled disc
/// - see EffortBarsControl.xaml's comment for why. Colour is not the only signal here either: an
/// unfilled/partially-filled ring differs from the solid max disc in shape, not just colour.
/// </summary>
public partial class EffortBarsControl : UserControl
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(int),
        typeof(EffortBarsControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, OnLevelChanged));

    /// <summary>Per-level colour, matching App/Theme.xaml's accent progression: the pastel-orange
    /// primary accent (#F0A868) for low, warming across a blend into the teal-blue complementary
    /// accent (#6EC1D6) for max. Index 0 = low ... index 3 = max, same ramp the old bars used.</summary>
    private static readonly SolidColorBrush[] LevelBrushes =
    [
        Freeze(Color.FromRgb(0xF0, 0xA8, 0x68)),
        Freeze(Color.FromRgb(0xCF, 0xAF, 0x8A)),
        Freeze(Color.FromRgb(0xA0, 0xB8, 0xAF)),
        Freeze(Color.FromRgb(0x6E, 0xC1, 0xD6)),
    ];

    /// <summary>Outline of the (always-visible) track ring - Theme.xaml's TextMutedBrush (#6E6E6E).</summary>
    private static readonly SolidColorBrush TrackStroke = Freeze(Color.FromRgb(0x6E, 0x6E, 0x6E));

    private const double Center = 7.0;
    private const double Radius = 6.0;

    public EffortBarsControl()
    {
        InitializeComponent();
        TrackRing.Stroke = TrackStroke;
        ApplyLevel(0);
    }

    public int Level
    {
        get => (int)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is EffortBarsControl control)
        {
            control.ApplyLevel((int)e.NewValue);
        }
    }

    private void ApplyLevel(int level)
    {
        int clamped = level < 0 ? 0 : level > EffortBarLevel.MaxBars ? EffortBarLevel.MaxBars : level;

        if (clamped == 0)
        {
            FillArc.Visibility = Visibility.Collapsed;
            FillDisc.Visibility = Visibility.Collapsed;
        }
        else if (clamped == EffortBarLevel.MaxBars)
        {
            FillArc.Visibility = Visibility.Collapsed;
            FillDisc.Visibility = Visibility.Visible;
            FillDisc.Fill = LevelBrushes[clamped - 1];
        }
        else
        {
            FillDisc.Visibility = Visibility.Collapsed;
            FillArc.Visibility = Visibility.Visible;
            FillArc.Stroke = LevelBrushes[clamped - 1];
            FillArc.Data = BuildArcGeometry((double)clamped / EffortBarLevel.MaxBars);
        }

        AutomationProperties.SetName(this, clamped == 0
            ? "Effort level unknown"
            : $"Effort level {clamped} of {EffortBarLevel.MaxBars}");
    }

    /// <summary>Builds a clockwise arc from the top (12 o'clock) covering <paramref name="fraction"/>
    /// of the full circle - callers never pass 1.0 (a full circle degenerates to a zero-length
    /// ArcSegment, since its start and end points coincide; level 4/max uses <c>FillDisc</c>
    /// instead, never this method).</summary>
    private static Geometry BuildArcGeometry(double fraction)
    {
        const double StartAngleDegrees = -90.0;
        double sweepDegrees = 360.0 * fraction;
        double endAngleDegrees = StartAngleDegrees + sweepDegrees;

        Point start = PointOnCircle(StartAngleDegrees);
        Point end = PointOnCircle(endAngleDegrees);
        bool isLargeArc = sweepDegrees > 180.0;

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(Radius, Radius), 0, isLargeArc, SweepDirection.Clockwise, isStroked: true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Point PointOnCircle(double angleDegrees)
    {
        double angleRadians = angleDegrees * Math.PI / 180.0;
        return new Point(Center + (Radius * Math.Cos(angleRadians)), Center + (Radius * Math.Sin(angleRadians)));
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

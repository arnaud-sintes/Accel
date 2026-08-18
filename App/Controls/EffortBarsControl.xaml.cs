namespace Accel.App.Controls;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Accel.Metrics;

/// <summary>
/// The effort badge: <see cref="Level"/> (0-4, straight from <see cref="EffortBarLevel.Resolve(string?)"/>)
/// shows as a hollow, unfilled ring for 0 (unknown/irrelevant), or a waxing moon phase for 1-4 -
/// see EffortBarsControl.xaml's comment for the full rationale. The phase geometry is the classic
/// two-overlapping-circles technique used by moon-phase icons: a "shadow" circle the same radius as
/// the disc, slid horizontally across it. At level 0 the shadow sits exactly on top of the disc
/// (fully new), at the max level it has slid clear off the far edge (fully full), and in between it
/// produces a crescent/half/gibbous shape - all from the one <see cref="GeometryCombineMode.Exclude"/>
/// combination, no per-level hardcoded shapes needed.
/// </summary>
public partial class EffortBarsControl : UserControl
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(int),
        typeof(EffortBarsControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, OnLevelChanged));

    /// <summary>Moonlight colour for the lit phase - pale and warm rather than any of Theme.xaml's
    /// semantic success/warning/danger colours, since a fuller moon is not meant to read as "hotter"
    /// or "worse", only "more".</summary>
    private static readonly SolidColorBrush LitBrush = Freeze(Color.FromRgb(0xE9, 0xE4, 0xC8));

    /// <summary>Outline of the (always-visible) track ring - Theme.xaml's TextMutedBrush (#6E6E6E).</summary>
    private static readonly SolidColorBrush TrackStroke = Freeze(Color.FromRgb(0x6E, 0x6E, 0x6E));

    private const double Radius = 6.0;
    private static readonly Point Center = new(6.0, 6.0);

    public EffortBarsControl()
    {
        InitializeComponent();
        TrackRing.Stroke = TrackStroke;
        LitPath.Fill = LitBrush;
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
            LitPath.Visibility = Visibility.Collapsed;
        }
        else
        {
            LitPath.Visibility = Visibility.Visible;
            LitPath.Data = BuildPhaseGeometry(clamped);
        }

        AutomationProperties.SetName(this, clamped == 0
            ? "Effort level unknown"
            : $"Effort level {clamped} of {EffortBarLevel.MaxBars}");
    }

    private static Geometry BuildPhaseGeometry(int clamped)
    {
        if (clamped >= EffortBarLevel.MaxBars)
        {
            return new EllipseGeometry(Center, Radius, Radius);
        }

        double t = (double)clamped / EffortBarLevel.MaxBars;
        double shadowOffset = 2 * Radius * t;

        var full = new EllipseGeometry(Center, Radius, Radius);
        var shadow = new EllipseGeometry(new Point(Center.X + shadowOffset, Center.Y), Radius, Radius);
        var phase = new CombinedGeometry(GeometryCombineMode.Exclude, full, shadow);
        phase.Freeze();
        return phase;
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

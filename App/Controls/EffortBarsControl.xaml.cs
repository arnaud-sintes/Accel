namespace Accel.App.Controls;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Accel.Metrics;

/// <summary>
/// The effort badge: <see cref="Level"/> (0-4, straight from <see cref="EffortBarLevel.Resolve(string?)"/>)
/// shows as a hollow, unfilled ring for 0 (unknown/irrelevant), or a solid filled disc for 1-4
/// colored along a green -> amber -> red ramp (<see cref="LevelBrushes"/>) - see
/// EffortBarsControl.xaml's comment for the full rationale, including why colour alone is not this
/// control's only signal.
/// </summary>
public partial class EffortBarsControl : UserControl
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(int),
        typeof(EffortBarsControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, OnLevelChanged));

    /// <summary>App/Theme.xaml's own semantic colours (SuccessColor/WarningColor/DangerColor) as the
    /// three stops of the ramp, so this control's palette stays in sync with every other
    /// success/warning/danger use in the app rather than inventing its own scale.</summary>
    private static readonly Color SuccessColor = Color.FromRgb(0x8F, 0xCB, 0x9B);
    private static readonly Color WarningColor = Color.FromRgb(0xE8, 0xC0, 0x7D);
    private static readonly Color DangerColor = Color.FromRgb(0xE9, 0x8F, 0x8F);

    /// <summary>One frozen brush per level (index 0 = level 1 ... index <see cref="EffortBarLevel.MaxBars"/> - 1
    /// = max), precomputed once since <see cref="EffortBarLevel.MaxBars"/> never changes at
    /// runtime.</summary>
    private static readonly SolidColorBrush[] LevelBrushes = BuildLevelBrushes();

    /// <summary>Outline of the (always-visible) track ring - Theme.xaml's TextMutedBrush (#6E6E6E).</summary>
    private static readonly SolidColorBrush TrackStroke = Freeze(Color.FromRgb(0x6E, 0x6E, 0x6E));

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
            FillDisc.Visibility = Visibility.Collapsed;
        }
        else
        {
            FillDisc.Visibility = Visibility.Visible;
            FillDisc.Fill = LevelBrushes[clamped - 1];
        }

        AutomationProperties.SetName(this, clamped == 0
            ? "Effort level unknown"
            : $"Effort level {clamped} of {EffortBarLevel.MaxBars}");
    }

    private static SolidColorBrush[] BuildLevelBrushes()
    {
        var brushes = new SolidColorBrush[EffortBarLevel.MaxBars];

        for (int level = 1; level <= EffortBarLevel.MaxBars; level++)
        {
            double t = EffortBarLevel.MaxBars <= 1 ? 0.0 : (double)(level - 1) / (EffortBarLevel.MaxBars - 1);
            Color color = t <= 0.5
                ? Lerp(SuccessColor, WarningColor, t / 0.5)
                : Lerp(WarningColor, DangerColor, (t - 0.5) / 0.5);
            brushes[level - 1] = Freeze(color);
        }

        return brushes;
    }

    private static Color Lerp(Color from, Color to, double t)
    {
        byte LerpChannel(byte a, byte b) => (byte)(a + ((b - a) * t));
        return Color.FromRgb(LerpChannel(from.R, to.R), LerpChannel(from.G, to.G), LerpChannel(from.B, to.B));
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

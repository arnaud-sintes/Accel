namespace Glaude.App.Controls;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Glaude.Metrics;

/// <summary>
/// P1-T4's effort-bar badge: renders <see cref="Level"/> (0-4, straight from
/// <see cref="EffortBarLevel.Resolve(string?)"/>) as that many filled bars out of four, the rest
/// left as a hollow outline - see the XAML's own comment for why unfilled bars stay visible
/// rather than vanishing. Colour is not the only signal here either: a filled bar's solid fill
/// vs. an unfilled bar's outline-only stroke differ in shape, not just colour.
/// </summary>
public partial class EffortBarsControl : UserControl
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(int),
        typeof(EffortBarsControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, OnLevelChanged));

    private static readonly SolidColorBrush FilledBrush = Freeze(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly SolidColorBrush UnfilledStroke = Freeze(Color.FromRgb(0xA0, 0xA0, 0xA0));

    public EffortBarsControl()
    {
        InitializeComponent();
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

        ApplyBar(Bar1, clamped >= 1);
        ApplyBar(Bar2, clamped >= 2);
        ApplyBar(Bar3, clamped >= 3);
        ApplyBar(Bar4, clamped >= 4);

        AutomationProperties.SetName(this, clamped == 0
            ? "Effort level unknown"
            : $"Effort level {clamped} of {EffortBarLevel.MaxBars}");
    }

    private static void ApplyBar(System.Windows.Shapes.Rectangle bar, bool filled)
    {
        bar.Fill = filled ? FilledBrush : Brushes.Transparent;
        bar.Stroke = filled ? FilledBrush : UnfilledStroke;
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

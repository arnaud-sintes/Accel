namespace Accel.App.Converters;

using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

/// <summary>
/// One-way <c>"#AARRGGBB"</c> string -> frozen <see cref="SolidColorBrush"/> converter, so the
/// hex colours <see cref="Accel.Metrics.ModelBadgeTable"/> and
/// <see cref="Accel.App.ViewModels.SessionVisualStateResolver"/> resolve (plain strings, kept
/// WPF-free so they stay unit-testable) can be bound straight to a <c>Fill</c>/<c>Foreground</c>
/// in XAML without either helper needing a WPF dependency. Unrecognized/empty input falls back to
/// a neutral gray rather than throwing, so a binding error never crashes the row.
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Fallback = Freeze(Colors.Gray);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                return Freeze(color);
            }
            catch (FormatException)
            {
                // fall through to Fallback below
            }
        }

        return Fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("HexToBrushConverter is one-way.");

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

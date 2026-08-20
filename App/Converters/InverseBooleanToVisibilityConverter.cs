namespace Accel.App.Converters;

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

/// <summary>One-way <c>bool</c> -&gt; <see cref="Visibility"/> converter, inverted relative to WPF's
/// built-in <see cref="System.Windows.Controls.BooleanToVisibilityConverter"/>: <c>false</c> maps to
/// <c>Visible</c>, <c>true</c> to <c>Collapsed</c>.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag && flag ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("InverseBooleanToVisibilityConverter is one-way.");
}

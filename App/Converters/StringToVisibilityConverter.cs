namespace Accel.App.Converters;

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

/// <summary>
/// One-way <c>string</c> -> <see cref="Visibility"/> converter: <c>Collapsed</c> for null/empty,
/// <c>Visible</c> otherwise. Used by <see cref="Accel.App.Controls.CustomTitleBar"/> to hide its
/// optional version caption entirely when no version text was supplied (dialogs), rather than
/// showing an empty gap next to the title.
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && !string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("StringToVisibilityConverter is one-way.");
}

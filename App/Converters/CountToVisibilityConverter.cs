namespace Accel.App.Converters;

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

/// <summary>
/// One-way <c>int</c> (an <see cref="System.Collections.ObjectModel.ObservableCollection{T}.Count"/>)
/// -> <see cref="Visibility"/> converter: <c>Collapsed</c> for zero, <c>Visible</c> otherwise. Used
/// to hide panel B's git status "STAGED CHANGES"/"CHANGES" group headers when that group is empty,
/// same collapse-when-empty pattern <see cref="BooleanToVisibilityConverter"/> already gives other
/// panels for a plain bool.
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("CountToVisibilityConverter is one-way.");
}

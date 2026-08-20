namespace Accel.App.Converters;

using System;
using System.Globalization;
using System.Windows.Data;

/// <summary>
/// One-way <c>bool</c> negation - used to bind e.g. <c>IsEnabled="{Binding IsBusy, Converter=...}"</c>
/// where the source property already means the opposite of what the target property needs.
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag && !flag;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("InverseBooleanConverter is one-way.");
}

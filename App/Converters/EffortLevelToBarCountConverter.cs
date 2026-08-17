namespace Accel.App.Converters;

using System;
using System.Globalization;
using System.Windows.Data;
using Accel.Metrics;

/// <summary>
/// One-way free-text effort-level string (e.g. "low"/"medium"/"high"/"max", the "Create session"
/// dialog's own <c>--effort</c> vocabulary - see <see cref="EffortBarLevel.Levels"/>) -> 0-4 bar
/// count converter, so <see cref="Accel.App.Controls.EffortBarsControl.Level"/> can bind directly
/// to it in the dialog's Effort <c>ComboBox</c> item template, exactly the same
/// <see cref="EffortBarLevel.Resolve(string?)"/> mapping panel A/E's own effort badges use - one
/// vocabulary, one resolver, everywhere it's shown.
/// </summary>
public sealed class EffortLevelToBarCountConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        EffortBarLevel.Resolve(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("EffortLevelToBarCountConverter is one-way.");
}

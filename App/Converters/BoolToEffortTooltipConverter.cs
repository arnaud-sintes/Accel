namespace Accel.App.Converters;

using System;
using System.Globalization;
using System.Windows.Data;

/// <summary>
/// <see langword="false"/> (<c>CreateSessionDialogViewModel.EffortSupported</c>, i.e. Haiku is
/// selected) -> the explanatory tooltip shown on the disabled Effort combo; <see langword="true"/>
/// -> <see langword="null"/> (no tooltip) - see <c>Accel.Metrics.ModelEffortTable</c> for why Haiku
/// has no effort concept to explain otherwise.
/// </summary>
public sealed class BoolToEffortTooltipConverter : IValueConverter
{
    public const string Message = "Haiku has no reasoning-effort levels - this field is not applicable.";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? null : Message;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

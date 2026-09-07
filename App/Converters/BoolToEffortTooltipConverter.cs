namespace Accel.App.Converters;

using System;
using System.Globalization;
using System.Windows.Data;

/// <summary>
/// <see langword="false"/> (<c>CreateSessionDialogViewModel.EffortSupported</c>, i.e. the selected
/// model has no effort ladder) -> the explanatory tooltip shown on the disabled Effort combo;
/// <see langword="true"/> -> <see langword="null"/> (no tooltip) - see
/// <c>Accel.Metrics.ModelEffortTable</c> for how that is decided.
///
/// <para>The message deliberately does not name Haiku. Which models lack the knob is a per-model
/// capability now read from Claude Code itself (<c>Accel.Metrics.ModelCatalog</c>) rather than a
/// fact Accel hardcodes, so a message that said "Haiku has no..." would be wrong the moment the
/// answer came back for some other model.</para>
/// </summary>
public sealed class BoolToEffortTooltipConverter : IValueConverter
{
    public const string Message = "This model has no reasoning-effort levels - this field is not applicable.";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? null : Message;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

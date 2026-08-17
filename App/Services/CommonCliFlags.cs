namespace Accel.App.Services;

using System;
using System.Collections.Generic;

/// <summary>
/// `claude --permission-mode &lt;value&gt;`'s vocabulary, mirrored here as an enum so the Create/Resume
/// dialogs can offer it as a single-select control (a ComboBox) rather than four independent
/// checkboxes - the four values are mutually exclusive on the real CLI, so independent checkboxes
/// would let a user build a nonsensical combination the CLI itself would reject.
/// </summary>
public enum PermissionModeOption
{
    /// <summary>No <c>--permission-mode</c> flag at all - `claude`'s own default behaviour.</summary>
    None,
    AcceptEdits,
    BypassPermissions,
    Plan,
}

/// <summary>One selectable permission-mode entry for a ComboBox: the enum value plus the label the
/// user actually sees. Same shape as <see cref="Accel.Metrics.ModelFamilyOption"/>.</summary>
public sealed record PermissionModeChoice(PermissionModeOption Value, string DisplayName);

/// <summary>
/// The one `claude` CLI setting common enough to deserve a dedicated single-select control in the
/// Create-session and Edit-launch-args dialogs (permission mode), instead of forcing every user to
/// know and type it into the free-text extra-args box. Kept separate from
/// <see cref="ExtraArgsParser"/> (which only tokenizes free text) and from the dialogs' own
/// ViewModels (which own the actual selected state) - this class is pure translation from "selected
/// option" to "argv tail".
/// </summary>
public static class CommonCliFlags
{
    /// <summary>Combo-box source: every <see cref="PermissionModeOption"/> paired with its display label,
    /// in the order the combo should list them (no-flag default first).</summary>
    public static readonly IReadOnlyList<PermissionModeChoice> PermissionModeChoices = new[]
    {
        new PermissionModeChoice(PermissionModeOption.None, "Default"),
        new PermissionModeChoice(PermissionModeOption.AcceptEdits, "Auto-accept edits"),
        new PermissionModeChoice(PermissionModeOption.BypassPermissions, "Bypass permissions"),
        new PermissionModeChoice(PermissionModeOption.Plan, "Plan mode"),
    };

    /// <summary>The literal value `--permission-mode` takes for <paramref name="mode"/>, or null for
    /// <see cref="PermissionModeOption.None"/> (no flag at all).</summary>
    public static string? ToFlagValue(PermissionModeOption mode) => mode switch
    {
        PermissionModeOption.AcceptEdits => "acceptEdits",
        PermissionModeOption.BypassPermissions => "bypassPermissions",
        PermissionModeOption.Plan => "plan",
        _ => null,
    };

    /// <summary>
    /// Builds the argv tail for the combo selection: <c>--permission-mode &lt;value&gt;</c> (two
    /// elements) if <paramref name="permissionMode"/> is not <see cref="PermissionModeOption.None"/>,
    /// otherwise empty - the flag is only ever added when the choice differs from the default.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(PermissionModeOption permissionMode)
    {
        var flagValue = ToFlagValue(permissionMode);
        return flagValue is null ? Array.Empty<string>() : new[] { "--permission-mode", flagValue };
    }
}

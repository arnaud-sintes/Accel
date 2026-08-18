namespace Accel.Metrics;

/// <summary>
/// Which model families recognize the reasoning-"effort" knob at all. Per Anthropic's current API
/// (Thinking &amp; Effort reference): Sonnet, Opus and Fable all share the exact same five-tier
/// scale (<see cref="EffortBarLevel.Levels"/>) - there is exactly one effort vocabulary in this
/// codebase, not one per family. Haiku is the one family with no effort control at all (the API
/// rejects an explicit effort value for it) - not merely a smaller top rung of the same scale, so
/// callers must treat it as "not applicable", never as "effort level 0/unknown" (which would read
/// as "we don't know this session's effort" rather than "this model has no such setting").
/// </summary>
public static class ModelEffortTable
{
    /// <summary>Case-insensitive model-family names (the <see cref="ModelBadgeTable.Families"/>/
    /// <c>CliValue</c> vocabulary, e.g. "Haiku") that recognize no effort level at all.</summary>
    private static readonly HashSet<string> UnsupportedFamilies = new(StringComparer.OrdinalIgnoreCase) { "Haiku" };

    /// <summary>
    /// Whether <paramref name="family"/> (a <see cref="ModelBadgeTable.Families"/> value, e.g.
    /// "Haiku"/"Sonnet"/"Opus"/"Fable" - the "Create session" dialog's own vocabulary) supports the
    /// effort knob. An unrecognized/null/empty family degrades to <see langword="true"/> - only a
    /// family this table explicitly knows to be unsupported should ever hide the control.
    /// </summary>
    public static bool SupportsEffort(string? family) =>
        string.IsNullOrEmpty(family) || !UnsupportedFamilies.Contains(family);

    /// <summary>
    /// Same check keyed on a resolved <see cref="ModelBadge"/> (panel A/E's own model badge,
    /// already computed from a session's raw model id) rather than the dialog's family string - the
    /// letter is the one vocabulary both share. An unmatched badge (<see cref="ModelBadge.Matched"/>
    /// false, i.e. an unrecognized model id) degrades to <see langword="true"/>, same rationale as
    /// the family overload: only a model this table can actually identify as Haiku should hide the
    /// effort badge.
    /// </summary>
    public static bool SupportsEffort(ModelBadge badge) =>
        !badge.Matched || !string.Equals(badge.Letter, "H", StringComparison.Ordinal);
}

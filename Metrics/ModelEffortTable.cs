namespace Accel.Metrics;

using System;
using System.Collections.Generic;

/// <summary>
/// Which models recognize the reasoning-"effort" knob, and with which tiers.
///
/// <para><b>Prefer the catalog; fall back to the rule.</b> Every method here takes an optional
/// <see cref="ModelCatalog"/>. When a discovered catalog is supplied, the answer is whatever Claude
/// Code itself reported for that model (see <see cref="Accel.Orchestration.ModelCatalogProbe"/>) -
/// which is the only trustworthy source, since effort support is a per-model capability the CLI
/// clamps silently rather than a property Accel can derive. Without one, the answer falls back to
/// the single rule Accel can state on its own: Haiku has no effort control, everything else takes
/// the full <see cref="EffortBarLevel.Levels"/> ladder.</para>
///
/// <para><b>Why the fallback is a rule and not a table.</b> Haiku's exception is the one piece of
/// per-model effort knowledge that has held across CLI versions, and it was confirmed empirically:
/// its picker row renders no effort slider at all, while every other row walked a full six-rung
/// ladder. Note that "no effort control" is not "effort level 0" - callers must render it as "not
/// applicable", never as "we don't know this session's effort", which is why
/// <see cref="SupportsEffort(string?, ModelCatalog?)"/> exists separately from
/// <see cref="EffortBarLevel.Resolve(string?)"/> returning 0.</para>
/// </summary>
public static class ModelEffortTable
{
    /// <summary>Case-insensitive model-family names (the <see cref="ModelBadgeTable.Families"/>/
    /// <c>CliValue</c> vocabulary, e.g. "Haiku") that recognize no effort level at all.</summary>
    private static readonly HashSet<string> UnsupportedFamilies = new(StringComparer.OrdinalIgnoreCase) { "Haiku" };

    /// <summary>
    /// The compiled-in rule on its own, with no catalog consulted: false only for a family this class
    /// explicitly knows to be effort-less. An unrecognized/null/empty family degrades to
    /// <see langword="true"/> - only a family Accel positively knows about should ever hide the
    /// control, since offering a tier the CLI then clamps is a much smaller harm than hiding one it
    /// would have accepted. Public because <see cref="ModelCatalog.BuiltIn"/> is built from it.
    /// </summary>
    public static bool BuiltInSupportsEffort(string? family) =>
        string.IsNullOrWhiteSpace(family) || !UnsupportedFamilies.Contains(family.Trim());

    /// <summary>
    /// Whether <paramref name="family"/> (a <c>--model</c> value, e.g. "Haiku"/"Sonnet"/"Opus") takes
    /// <c>--effort</c>. Answered from <paramref name="catalog"/> when it knows the model, otherwise by
    /// <see cref="BuiltInSupportsEffort"/>.
    /// </summary>
    public static bool SupportsEffort(string? family, ModelCatalog? catalog = null)
    {
        var entry = catalog?.Find(family);
        return entry?.SupportsEffort ?? BuiltInSupportsEffort(family);
    }

    /// <summary>
    /// <paramref name="family"/>'s effort ladder, ascending; empty when the model has no effort
    /// control. Delegates to <see cref="ModelCatalog.EffortTiersFor"/> when a catalog is supplied so
    /// there is one lookup path, not two.
    /// </summary>
    public static IReadOnlyList<string> TiersFor(string? family, ModelCatalog? catalog = null)
    {
        if (catalog is not null)
        {
            return catalog.EffortTiersFor(family);
        }

        return BuiltInSupportsEffort(family) ? EffortBarLevel.Levels : Array.Empty<string>();
    }

    /// <summary>
    /// Same check keyed on a resolved <see cref="ModelBadge"/> (panel A/E's own model badge, already
    /// computed from a session's raw model id) rather than a <c>--model</c> value - the letter is the
    /// one vocabulary both share. An unmatched badge (<see cref="ModelBadge.Matched"/> false, i.e. an
    /// unrecognized model id) degrades to <see langword="true"/>, same rationale as the family
    /// overload.
    ///
    /// <para>No catalog overload: a badge is derived from a <i>resolved</i> model id off the wire
    /// (<c>claude-haiku-4-5-20251001</c>), not from a picker row's <c>--model</c> value, so a catalog
    /// keyed on the latter cannot answer for it. The letter is sufficient here precisely because
    /// effort-less-ness is a family-level property.</para>
    /// </summary>
    public static bool SupportsEffort(ModelBadge badge) =>
        !badge.Matched || !string.Equals(badge.Letter, "H", StringComparison.Ordinal);
}

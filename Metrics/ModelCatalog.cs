namespace Accel.Metrics;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Where a <see cref="ModelCatalog"/>'s contents came from. Surfaced in the UI (as a
/// tooltip on the model picker) rather than kept internal, because "these are the models this
/// machine actually offers" and "these are the four families Accel was compiled believing in" are
/// very different claims and the user is entitled to know which one they are looking at.</summary>
public enum ModelCatalogSource
{
    /// <summary>Accel's own compiled-in table - <see cref="ModelBadgeTable.FamilyDisplayNames"/> plus
    /// <see cref="EffortBarLevel.Levels"/>. Correct only as long as the CLI's lineup and the user's
    /// entitlements happen to match what Accel was built against.</summary>
    BuiltIn,

    /// <summary>Read out of Claude Code itself by <see cref="Accel.Orchestration.ModelCatalogProbe"/> -
    /// the rows this machine's `claude` actually offers this account, with each row's real effort
    /// ladder.</summary>
    Discovered,
}

/// <summary>
/// One selectable model. <see cref="CliValue"/> is what goes after <c>--model</c>;
/// <see cref="DisplayName"/> is what the user sees; <see cref="EffortTiers"/> is that model's own
/// effort ladder, ascending, and <b>empty means the model has no effort control at all</b> (Haiku) -
/// which is deliberately a different state from "we don't know its tiers", since flattening the two
/// is what makes a UI show "effort: unknown" for a model that has no such setting.
/// </summary>
public sealed record ModelCatalogEntry(
    string CliValue,
    string DisplayName,
    string? Description,
    IReadOnlyList<string> EffortTiers)
{
    /// <summary>Whether this model takes <c>--effort</c> at all.</summary>
    public bool SupportsEffort => EffortTiers.Count > 0;

    /// <summary>The tier to preselect for this model: the middle of its own ladder, so a six-rung
    /// ladder does not open pinned to "low" just because that is index 0.</summary>
    public string? DefaultEffortTier => EffortTiers.Count == 0 ? null : EffortTiers[EffortTiers.Count / 2];
}

/// <summary>
/// The set of models Accel offers, and each one's effort ladder - the single source both the
/// "Create session" dialog and <see cref="ModelEffortTable"/> read, replacing what used to be two
/// independently hardcoded lists (<see cref="ModelBadgeTable.Families"/> for the models,
/// <see cref="EffortBarLevel.Levels"/> for one shared ladder assumed to apply to all of them).
///
/// <para><b>Why this exists.</b> The hardcoded assumption was measurably wrong on a real machine:
/// the CLI offered four rows that did not include Fable (which Accel listed) but did include a
/// "Default (recommended)" row and an "Opus (1M context)" variant (neither of which Accel could
/// express), and every effort-capable row had a <i>six</i>-rung ladder ending in "ultracode" rather
/// than the five Accel knew. The lineup also depends on the account's entitlements, so no compiled-in
/// list can be right for every user. <see cref="Accel.Orchestration.ModelCatalogProbe"/> discovers
/// the real one; <see cref="BuiltIn"/> remains the fallback for when discovery has not run or failed.</para>
///
/// <para>Immutable and free of any I/O, so every consumer can treat it as a value and it is fully
/// unit-testable - all the fragility lives in the probe that produces one.</para>
/// </summary>
public sealed record ModelCatalog(
    IReadOnlyList<ModelCatalogEntry> Entries,
    ModelCatalogSource Source,
    string? ClaudeCliVersion = null,
    DateTimeOffset? DiscoveredAtUtc = null,
    string? DefaultModelHint = null)
{
    /// <summary>
    /// Last-resort preference when nothing else identifies a default: the model Claude Code's own
    /// "Default (recommended)" row pointed at when this was written. A tiebreak, not a claim - it is
    /// consulted only when the user has configured no model and
    /// <see cref="DefaultModelHint"/> is absent (i.e. the built-in catalog, where by definition
    /// nothing has been discovered). Preferring it over <c>Entries[0]</c> matters because the entry
    /// order is ascending capability, so the first row is the <i>least</i> capable model - opening the
    /// dialog on Haiku by default would be a worse guess than opening it on Sonnet.
    /// </summary>
    private const string FallbackDefaultModel = "Sonnet";

    /// <summary>
    /// Accel's compiled-in lineup, derived from <see cref="ModelBadgeTable.FamilyDisplayNames"/> and
    /// <see cref="EffortBarLevel.Levels"/> rather than typed out again here - so the fallback and the
    /// badge table cannot drift apart, which was the original sin this whole type exists to fix. The
    /// one piece of per-model knowledge it still hardcodes is Haiku's lack of an effort control (see
    /// <see cref="ModelEffortTable"/>), because that is the only such fact Accel can state without
    /// having asked the CLI.
    /// </summary>
    public static readonly ModelCatalog BuiltIn = new(
        ModelBadgeTable.FamilyDisplayNames
            .Select(family => new ModelCatalogEntry(
                family.Family,
                family.DisplayName,
                null,
                ModelEffortTable.BuiltInSupportsEffort(family.Family)
                    ? EffortBarLevel.Levels
                    : Array.Empty<string>()))
            .ToArray(),
        ModelCatalogSource.BuiltIn);

    /// <summary>True when this catalog came from the CLI rather than from Accel's own table.</summary>
    public bool IsDiscovered => Source == ModelCatalogSource.Discovered;

    /// <summary>
    /// The model to preselect when the user has expressed no preference: whatever
    /// <see cref="DefaultModelHint"/> names (which is what Claude Code's own "Default (recommended)"
    /// row pointed at, matched on either the <c>--model</c> value or the display name, since the row
    /// describes itself with the latter), then <see cref="FallbackDefaultModel"/>, then the first
    /// entry. Null only for an empty catalog.
    /// </summary>
    public string? ResolveDefaultModel()
    {
        if (Find(DefaultModelHint) is { } byCliValue)
        {
            return byCliValue.CliValue;
        }

        if (!string.IsNullOrWhiteSpace(DefaultModelHint))
        {
            foreach (var entry in Entries)
            {
                if (string.Equals(entry.DisplayName, DefaultModelHint.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return entry.CliValue;
                }
            }
        }

        return Find(FallbackDefaultModel)?.CliValue ?? Entries.FirstOrDefault()?.CliValue;
    }

    /// <summary>
    /// The entry whose <see cref="ModelCatalogEntry.CliValue"/> matches <paramref name="cliValue"/>
    /// case-insensitively, or null. Case-insensitive because the value can arrive from three places
    /// that disagree on casing: the CLI's own picker ("Sonnet"), a user's <c>settings.json</c>
    /// (<c>"model": "Opus"</c>), and a previously stored Accel selection.
    /// </summary>
    public ModelCatalogEntry? Find(string? cliValue) =>
        string.IsNullOrWhiteSpace(cliValue)
            ? null
            : Entries.FirstOrDefault(entry =>
                string.Equals(entry.CliValue, cliValue.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <paramref name="cliValue"/>'s effort ladder. Falls back to <see cref="EffortBarLevel.Levels"/>
    /// for a model this catalog has never heard of - the same "only hide the control for a model we
    /// positively know has none" bias <see cref="ModelEffortTable"/> applies, since offering a tier
    /// the CLI then clamps is a far smaller harm than hiding one it would have accepted.
    /// </summary>
    public IReadOnlyList<string> EffortTiersFor(string? cliValue)
    {
        var entry = Find(cliValue);
        if (entry is not null)
        {
            return entry.EffortTiers;
        }

        return ModelEffortTable.BuiltInSupportsEffort(cliValue)
            ? EffortBarLevel.Levels
            : Array.Empty<string>();
    }

    /// <summary>
    /// A one-line provenance description for the model picker's tooltip - so a user seeing an
    /// unexpected lineup can tell whether they are looking at what their CLI reported or at Accel's
    /// own guess.
    /// </summary>
    public string DescribeSource() => Source switch
    {
        ModelCatalogSource.Discovered =>
            $"Read from Claude Code{(ClaudeCliVersion is null ? string.Empty : " " + ClaudeCliVersion)}" +
            $"{(DiscoveredAtUtc is null ? string.Empty : $" on {DiscoveredAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}")}.",
        _ => "Accel's built-in list - Claude Code has not been queried yet, so this may not match what your account offers.",
    };
}

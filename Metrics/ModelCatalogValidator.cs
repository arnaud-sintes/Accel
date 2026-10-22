namespace Accel.Metrics;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// The one place that decides whether a <see cref="ModelCatalog"/> may be trusted. The catalog is scraped
/// off a repainting terminal screen, and every earlier fix for a mangled one cleaned strings after the
/// fact; this refuses the result instead. It is applied at every boundary - after discovery (the probe),
/// before a catalog is cached (<c>ModelCatalogCache.TrySave</c>), when one is read back
/// (<c>TryLoad</c>) and before the service publishes one - so a bad catalog has no path to the cache or the
/// UI, and a bad cache written by an older version is simply not read. A catalog is accepted or rejected
/// as a whole: a partly-mangled list is not a list to repair.
///
/// <para>The checks are shapes, not allow-lists of names: they cannot say a model exists, but they reject
/// the corruptions seen so far (residue glued onto names and versions, usage-meter text in descriptions,
/// truncated effort ladders, duplicate or empty entries) without needing to change when a new model
/// arrives.</para>
/// </summary>
public static class ModelCatalogValidator
{
    private const int MaxEntries = 30;
    private const int MaxDisplayNameLength = 40;
    private const int MaxDescriptionLength = 120;

    // A capitalised family word, then version tokens that are digits and dots and nothing else, then the one
    // variant note a model row carries: "Opus", "Sonnet 5.5", "Opus 4.8 (1M context)". A letter or digit
    // glued to a version ("Sonnet 5.5up") or a lowercase start is not a model name.
    private static readonly Regex DisplayNameShape = new(
        @"^[A-Z][A-Za-z]*(?: [0-9]+(?:\.[0-9]+)*)*(?: \(1M context\))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Prose. Deliberately excludes the characters that mark neighbouring screen content bleeding into a
    // description - '~' and '%' (the context meter), '>' and '"' (the prompt's suggestion line), box and
    // bar glyphs - none of which a one-line model description needs.
    private static readonly Regex DescriptionShape = new(
        @"^[A-Za-z0-9][A-Za-z0-9 ,.'/\-·()+:;!?&]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TierShape = new(@"^[a-z]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Whether <paramref name="description"/> is something a model description could be.</summary>
    public static bool IsPlausibleDescription(string? description) =>
        !string.IsNullOrWhiteSpace(description) &&
        description.Length <= MaxDescriptionLength &&
        DescriptionShape.IsMatch(description);

    /// <summary>Whether <paramref name="name"/> is something a model's display name could be.</summary>
    public static bool IsPlausibleDisplayName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Length <= MaxDisplayNameLength &&
        DisplayNameShape.IsMatch(name);

    /// <summary>Every reason <paramref name="catalog"/> must not be trusted; empty when it may be.</summary>
    public static IReadOnlyList<string> Validate(ModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var problems = new List<string>();
        var entries = catalog.Entries;

        if (entries.Count == 0)
        {
            problems.Add("the catalog has no models");
            return problems;
        }

        if (entries.Count > MaxEntries)
        {
            problems.Add($"implausibly many models ({entries.Count})");
        }

        foreach (var entry in entries)
        {
            ValidateEntry(entry, problems);
        }

        foreach (var duplicate in entries
                     .GroupBy(e => e.CliValue.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            problems.Add($"launch value '{duplicate.Key}' appears {duplicate.Count()} times");
        }

        // The shape a truncated walk leaves behind: a ladder is read by pressing the slider key until the
        // tier stops changing, and a missed repaint ends that after one tier. One such entry can be real;
        // every effort-capable model having exactly one tier is the failure.
        var capable = entries.Where(e => e.EffortTiers.Count > 0).ToList();
        if (capable.Count >= 2 && capable.All(e => e.EffortTiers.Count == 1))
        {
            problems.Add("every effort ladder has a single tier (a truncated walk)");
        }

        return problems;
    }

    private static void ValidateEntry(ModelCatalogEntry entry, List<string> problems)
    {
        if (!ModelLaunchValue.IsPlausible(entry.CliValue))
        {
            problems.Add($"launch value '{entry.CliValue}' is not a plausible --model value");
        }

        if (!IsPlausibleDisplayName(entry.DisplayName))
        {
            problems.Add($"display name '{entry.DisplayName}' is not a plausible model name");
        }
        else if (FamilyOf(entry.CliValue) is { } family &&
                 !entry.DisplayName.StartsWith(family, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"display name '{entry.DisplayName}' does not match launch value '{entry.CliValue}'");
        }

        if (entry.Description is not null && !IsPlausibleDescription(entry.Description))
        {
            problems.Add($"description of '{entry.CliValue}' is not plausible prose");
        }

        var tiers = entry.EffortTiers;
        if (tiers.Any(t => !TierShape.IsMatch(t)) || tiers.Distinct(StringComparer.Ordinal).Count() != tiers.Count || tiers.Count > 10)
        {
            problems.Add($"effort tiers of '{entry.CliValue}' are not a plausible ladder");
        }
    }

    /// <summary>The model family a launch value names ("sonnet" for <c>claude-sonnet-5-5</c> and for the
    /// bare alias <c>sonnet</c>), or null when none can be read.</summary>
    private static string? FamilyOf(string cliValue)
    {
        var value = cliValue.Trim();
        var bracket = value.IndexOf('[');
        if (bracket >= 0)
        {
            value = value[..bracket];
        }

        if (!value.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
        {
            return value.Length > 0 && value.All(char.IsLetter) ? value : null;
        }

        // First all-letters part after "claude-": handles both claude-sonnet-5-5 and claude-3-5-sonnet-2024...
        return value["claude-".Length..]
            .Split('-')
            .FirstOrDefault(part => part.Length > 0 && part.All(char.IsLetter));
    }
}

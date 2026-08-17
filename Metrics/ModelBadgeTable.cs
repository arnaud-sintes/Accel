namespace Accel.Metrics;

/// <summary>
/// One resolved model badge: a single letter to render inside a small chip, plus the chip's
/// colour, plus whether anything in <see cref="ModelBadgeTable"/>'s table actually matched
/// <see cref="ModelBadgeTable.Resolve(string?)"/>'s input (vs. falling through to the
/// "unrecognized" default) - the same true/false "matched" shape <see cref="ModelWindowTable"/>
/// reports, for the same reason: callers that want to distinguish "we know this model family"
/// from "we have no idea" need it.
/// </summary>
/// <summary>One entry in the "Create session" dialog's model picker: the bare family prefix sent
/// as the <c>--model</c> value, plus the version-specific label actually shown to the user (e.g.
/// "Haiku 4.5") - see <see cref="ModelBadgeTable.FamilyDisplayNames"/>.</summary>
public sealed record ModelFamilyOption(string Family, string DisplayName);

public readonly record struct ModelBadge(string Letter, string ColorHex, bool Matched)
{
    /// <summary>Rendered for any model id that matches nothing in the table.</summary>
    public static readonly ModelBadge Unmatched = new("?", UnmatchedColorHex, false);

    public const string UnmatchedColorHex = "#FF6B7280"; // neutral gray - never the only signal (the letter differs too)
}

/// <summary>
/// Resolves an opaque model-id string (e.g. "claude-opus-4-8", "claude-haiku-4-5-20251001") to a
/// letter-in-chip badge for panel A (P1-T4, locked-in decision 9): <c>O</c>=Opus, <c>S</c>=Sonnet,
/// <c>H</c>=Haiku, <c>F</c>=Fable, <c>?</c>=unmatched. Colour differs per family, but colour is
/// never the only signal here either - the letter always differs alongside it, same principle as
/// <c>MonitorTreeBuilder.GlyphFor</c>'s state glyphs.
///
/// <para>Deliberately mirrors <see cref="ModelWindowTable.Resolve(string?,out bool)"/>'s exact
/// algorithm - exact match first, then longest-prefix match, then a fixed "unmatched" default -
/// so there is exactly one model-id matching strategy in this codebase, not two independently
/// maintained ones. The table here is keyed on the model *family* prefix (e.g.
/// <c>"claude-opus"</c>) rather than <see cref="ModelWindowTable"/>'s specific dated/tiered ids,
/// since every observed id for a family (dated or not, "-1m" or not) shares the same family
/// prefix and therefore the same badge.</para>
/// </summary>
public static class ModelBadgeTable
{
    private static readonly (string Prefix, string Letter, string ColorHex, string DisplayName)[] Table =
    {
        // Green - Haiku family (least complex).
        ("claude-haiku", "H", "#FF10B981", "Haiku 4.5"),

        // Blue - Sonnet family.
        ("claude-sonnet", "S", "#FF3B82F6", "Sonnet 5"),

        // Purple - Opus family.
        ("claude-opus", "O", "#FF9061F9", "Opus 5"),

        // Amber - Fable family.
        ("claude-fable", "F", "#FFF59E0B", "Fable 5"),
    };

    /// <summary>
    /// The model-family vocabulary this table knows, in the order above (haiku, sonnet, opus,
    /// fable - ascending complexity) - the canonical list P2-T6's "Create session" dialog picks a
    /// <c>--model</c> value from, so that dialog and panel A's badges are provably reading the
    /// same table rather than maintaining two independently-typed lists of model family names.
    /// </summary>
    public static readonly IReadOnlyList<string> Families = Array.ConvertAll(Table, entry => entry.Prefix);

    /// <summary>
    /// The same families as <see cref="Families"/>, paired with a human-readable label that names
    /// the current version (e.g. "Haiku 4.5") - what the "Create session" dialog actually shows in
    /// its model picker, while still sending <see cref="Families"/>' bare family prefix as the
    /// <c>--model</c> value. A named record rather than a <see cref="ValueTuple"/> - WPF's XAML
    /// binding engine resolves properties by reflection, and value-tuple element names ("Family",
    /// "DisplayName") are erased at runtime (only "Item1"/"Item2" actually exist as fields), so a
    /// tuple here would silently fail to bind in <c>CreateSessionDialog.xaml</c>.
    /// </summary>
    public static readonly IReadOnlyList<ModelFamilyOption> FamilyDisplayNames =
        Array.ConvertAll(Table, entry => new ModelFamilyOption(entry.Prefix, entry.DisplayName));

    /// <summary>Case-insensitive keyword fallback for the rendered <c>ModelDisplayName</c> form
    /// (e.g. "Sonnet 5", "Opus 4.5") that <c>MonitorTreeBuilder.BuildSessionNode</c> prefers over
    /// the raw model id for live sessions - the raw id (<c>claude-sonnet-5</c>) the exact/prefix
    /// tier above matches is only what historical (transcript-derived) sessions actually carry in
    /// that same column. Same letter/colour per family either way; this is purely a second, more
    /// lenient tier for a different input shape, not a second matching strategy.</summary>
    private static readonly (string Keyword, string Letter, string ColorHex)[] DisplayNameKeywords =
    {
        ("opus", "O", "#FF9061F9"),
        ("sonnet", "S", "#FF3B82F6"),
        ("haiku", "H", "#FF10B981"),
        ("fable", "F", "#FFF59E0B"),
    };

    /// <summary>
    /// Resolves <paramref name="modelId"/> to a badge. Never throws on null/empty input - returns
    /// <see cref="ModelBadge.Unmatched"/> in that case, same as for any unrecognized string.
    /// </summary>
    public static ModelBadge Resolve(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return ModelBadge.Unmatched;
        }

        foreach (var (prefix, letter, colorHex, _) in Table)
        {
            if (string.Equals(prefix, modelId, StringComparison.Ordinal))
            {
                return new ModelBadge(letter, colorHex, true);
            }
        }

        int bestPrefixLength = -1;
        string bestLetter = ModelBadge.Unmatched.Letter;
        string bestColorHex = ModelBadge.Unmatched.ColorHex;

        foreach (var (prefix, letter, colorHex, _) in Table)
        {
            if (prefix.Length > bestPrefixLength && modelId.StartsWith(prefix, StringComparison.Ordinal))
            {
                bestPrefixLength = prefix.Length;
                bestLetter = letter;
                bestColorHex = colorHex;
            }
        }

        if (bestPrefixLength >= 0)
        {
            return new ModelBadge(bestLetter, bestColorHex, true);
        }

        foreach (var (keyword, letter, colorHex) in DisplayNameKeywords)
        {
            if (modelId.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return new ModelBadge(letter, colorHex, true);
            }
        }

        return ModelBadge.Unmatched;
    }
}

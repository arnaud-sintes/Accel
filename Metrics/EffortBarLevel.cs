namespace Accel.Metrics;

/// <summary>
/// Resolves a session/agent's free-text <c>EffortLevel</c> string (the wire vocabulary already
/// carried on <see cref="Accel.Cli.MonitorRowColumns.Effort"/>) to a 0-5 signal-bar count for
/// P1-T4's effort-bar badge: 1 bar for the lowest reasoning-effort tier, up to 5 for the highest,
/// 0 (no bars filled) for anything unrecognized/missing - never throws.
///
/// <para>Five tiers, not four: Claude's current-generation models (Sonnet/Opus/Fable - see
/// <see cref="ModelEffortTable"/> for which families this applies to at all) added a fifth
/// "xhigh" tier between "high" and "max". There is exactly one effort vocabulary in this codebase
/// - <see cref="ModelEffortTable"/> decides per-family whether it applies, not a second, smaller
/// scale for some families.</para>
///
/// <para>Pure and side-effect-free like <see cref="ModelWindowTable"/>/<see cref="ModelBadgeTable"/>,
/// so it is unit-testable without any UI, and kept separate from the WPF-facing
/// <c>EffortBarsControl</c> so the level→bar-count mapping is directly testable per this task's
/// requirement to unit test any state→visual helper logic, not only through XAML.</para>
/// </summary>
public static class EffortBarLevel
{
    /// <summary>Highest bar count this resolver ever returns.</summary>
    public const int MaxBars = 5;

    /// <summary>
    /// The canonical effort-level vocabulary this resolver recognizes, one representative spelling
    /// per tier (lowest to highest bar count) - the list P2-T6's "Create session" dialog picks a
    /// <c>--effort</c> value from, so that dialog and panel A's effort bars are provably reading the
    /// same tiers rather than maintaining two independently-typed effort vocabularies. Each entry
    /// here must resolve to a strictly increasing bar count via <see cref="Resolve"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> Levels = new[] { "low", "medium", "high", "xhigh", "max" };

    public static int Resolve(string? effortLevel)
    {
        if (string.IsNullOrWhiteSpace(effortLevel))
        {
            return 0;
        }

        return effortLevel.Trim().ToLowerInvariant() switch
        {
            "minimal" or "low" => 1,
            "medium" or "mid" => 2,
            "high" => 3,
            "xhigh" => 4,
            "max" or "maximum" or "highest" => 5,
            _ => 0, // unrecognized (including the "?" placeholder MonitorTreeBuilder uses) - honestly unmatched
        };
    }
}

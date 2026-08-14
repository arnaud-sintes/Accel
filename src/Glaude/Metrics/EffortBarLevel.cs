namespace Glaude.Metrics;

/// <summary>
/// Resolves a session/agent's free-text <c>EffortLevel</c> string (the wire vocabulary already
/// carried on <see cref="Glaude.Cli.MonitorRowColumns.Effort"/>) to a 0-4 signal-bar count for
/// P1-T4's effort-bar badge: 1 bar for the lowest reasoning-effort tier, up to 4 for the highest,
/// 0 (no bars filled) for anything unrecognized/missing - never throws.
///
/// <para>Pure and side-effect-free like <see cref="ModelWindowTable"/>/<see cref="ModelBadgeTable"/>,
/// so it is unit-testable without any UI, and kept separate from the WPF-facing
/// <c>EffortBarsControl</c> so the level→bar-count mapping is directly testable per this task's
/// requirement to unit test any state→visual helper logic, not only through XAML.</para>
/// </summary>
public static class EffortBarLevel
{
    /// <summary>Highest bar count this resolver ever returns.</summary>
    public const int MaxBars = 4;

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
            "max" or "xhigh" or "maximum" or "highest" => 4,
            _ => 0, // unrecognized (including the "?" placeholder MonitorTreeBuilder uses) - honestly unmatched
        };
    }
}

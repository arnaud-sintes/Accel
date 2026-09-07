namespace Accel.Metrics;

/// <summary>
/// Resolves a session/agent's free-text <c>EffortLevel</c> string (the wire vocabulary already
/// carried on <see cref="Accel.Cli.MonitorRowColumns.Effort"/>) to a 0-6 signal-bar count for
/// P1-T4's effort-bar badge: 1 bar for the lowest reasoning-effort tier, up to
/// <see cref="MaxBars"/> for the highest, 0 (nothing filled) for anything unrecognized/missing -
/// never throws.
///
/// <para><b>Six tiers, and why the count is measured rather than assumed.</b> This list used to end
/// at "max", which was wrong: driving Claude Code's own <c>/model</c> effort slider through a full
/// cycle (see <see cref="Accel.Orchestration.ModelCatalogProbe"/>) walks
/// <c>low → medium → high → xhigh → max → ultracode</c> and then wraps, so there is a sixth tier
/// above "max" that Accel previously could neither name nor render. That is exactly the drift a
/// hardcoded vocabulary invites, which is why <see cref="Accel.Metrics.ModelCatalog"/> now prefers a
/// discovered ladder over this list wherever a per-model answer is available. This list remains the
/// built-in floor: the resolver still has to map a tier name to a bar count when no discovery has
/// run, and older transcripts still carry tier names for models that no longer exist.</para>
///
/// <para>Which models recognize the knob at all is a separate question, answered by
/// <see cref="ModelEffortTable"/> - there is exactly one effort vocabulary in this codebase, not a
/// second, smaller scale for some families.</para>
///
/// <para>Pure and side-effect-free like <see cref="ModelWindowTable"/>/<see cref="ModelBadgeTable"/>,
/// so it is unit-testable without any UI, and kept separate from the WPF-facing
/// <c>EffortBarsControl</c> so the level→bar-count mapping is directly testable per this task's
/// requirement to unit test any state→visual helper logic, not only through XAML.</para>
/// </summary>
public static class EffortBarLevel
{
    /// <summary>
    /// The canonical effort-level vocabulary this resolver recognizes, one representative spelling
    /// per tier, ascending. Each entry here must resolve to a strictly increasing bar count via
    /// <see cref="Resolve"/>, and <see cref="MaxBars"/> must equal this list's length - both pinned
    /// by unit tests, so a tier cannot be added here without the badge geometry following.
    /// </summary>
    public static readonly IReadOnlyList<string> Levels =
        new[] { "low", "medium", "high", "xhigh", "max", "ultracode" };

    /// <summary>Highest bar count this resolver ever returns - one per <see cref="Levels"/> entry.</summary>
    public static readonly int MaxBars = Levels.Count;

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

            // The tier Claude Code's own slider calls "ultracode". "ultra" is accepted alongside it
            // because that is the spelling the CLI uses internally for the same rung (its capability
            // flag is `supportsUltra`), and a transcript is free to carry either.
            "ultracode" or "ultra" => 6,

            _ => 0, // unrecognized (including the "?" placeholder MonitorTreeBuilder uses) - honestly unmatched
        };
    }

    /// <summary>
    /// The reverse of <see cref="Resolve"/> for the canonical spellings only: bar count →
    /// <see cref="Levels"/> entry, or null for 0/out-of-range. Used where a stored bar count has to be
    /// turned back into a <c>--effort</c> value.
    /// </summary>
    public static string? FromBarCount(int barCount) =>
        barCount >= 1 && barCount <= Levels.Count ? Levels[barCount - 1] : null;
}

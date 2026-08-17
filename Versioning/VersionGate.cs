namespace Accel.Versioning;

/// <summary>
/// Feature flags that are gated behind specific Claude Code versions.
/// </summary>
public enum Feature
{
    /// <summary>
    /// SubagentStart event (confirmed emitted on 2.1.224).
    /// </summary>
    SubagentStartEvent,

    /// <summary>
    /// subagentStatusLine model and contextWindowSize fields (v2.1.205+).
    /// </summary>
    SubagentStatusLineModelAndContextWindow,

    /// <summary>
    /// subagentStatusLine effort field (v2.1.214+).
    /// </summary>
    SubagentStatusLineEffort,

    /// <summary>
    /// context_window.total_* as current (not cumulative) usage (v2.1.132+).
    /// </summary>
    ContextWindowCurrentNotCumulative,

    /// <summary>
    /// statusLine prompt_id field (v2.1.196+).
    /// </summary>
    StatusLinePromptId,
}

/// <summary>
/// Version gate logic for feature availability based on Claude Code version.
/// All thresholds are from project.md Phase 6 requirements.
/// </summary>
public static class VersionGate
{
    // Version thresholds for each feature
    private static readonly ClaudeVersion SubagentStartEventMinVersion = new(2, 1, 224);
    private static readonly ClaudeVersion SubagentStatusLineModelAndContextWindowMinVersion = new(2, 1, 205);
    private static readonly ClaudeVersion SubagentStatusLineEffortMinVersion = new(2, 1, 214);
    private static readonly ClaudeVersion ContextWindowCurrentNotCumulativeMinVersion = new(2, 1, 132);
    private static readonly ClaudeVersion StatusLinePromptIdMinVersion = new(2, 1, 196);

    /// <summary>
    /// Determines if a given feature is supported by the given Claude version.
    /// Defaults to false (conservative) if the version is null/unparseable.
    /// </summary>
    /// <param name="version">The current Claude Code version, or null if unparseable/unavailable</param>
    /// <param name="feature">The feature to check</param>
    /// <returns>true if the version supports the feature; false otherwise</returns>
    public static bool Supports(ClaudeVersion? version, Feature feature)
    {
        // If version is null/unparseable, degrade conservatively: no features supported
        if (version == null)
            return false;

        return feature switch
        {
            Feature.SubagentStartEvent => version >= SubagentStartEventMinVersion,
            Feature.SubagentStatusLineModelAndContextWindow => version >= SubagentStatusLineModelAndContextWindowMinVersion,
            Feature.SubagentStatusLineEffort => version >= SubagentStatusLineEffortMinVersion,
            Feature.ContextWindowCurrentNotCumulative => version >= ContextWindowCurrentNotCumulativeMinVersion,
            Feature.StatusLinePromptId => version >= StatusLinePromptIdMinVersion,
            _ => false, // Unknown feature: conservative default
        };
    }

    /// <summary>
    /// Whether Accel should register the top-level `subagentStatusLine` hook at all for a
    /// given Claude Code version (Phase 3c). This is a *registration* gate only, checked
    /// before the hook is installed into settings.json (that install itself is Phase 4's
    /// job, calling into Phase 2's chaining machinery) - it is not about hiding fields that
    /// happen to arrive in a payload. Per project.md's gating table, the hook is worth
    /// registering once at least `model`/`contextWindowSize` are supported (v2.1.205+); the
    /// later-arriving `effort` field (v2.1.214+) does not require re-registration - whatever
    /// fields the running version actually sends are printed tolerantly regardless.
    /// Null/unparseable versions degrade conservatively to "do not register".
    /// </summary>
    public static bool ShouldRegisterSubagentStatusLine(ClaudeVersion? version) =>
        Supports(version, Feature.SubagentStatusLineModelAndContextWindow);
}

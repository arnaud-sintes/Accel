namespace Accel.Metrics;

/// <summary>
/// Resolves an opaque model-id string to a context-window token count, via exact match
/// first, then longest-prefix match, then a fixed default. Per project.md, observed
/// model-id values are not uniformly short ids (e.g. "claude-opus-5" vs. dated forms like
/// "claude-haiku-4-5-20251001"), so prefix matching (not exact-only) is required.
///
/// Bug-fix pass (UI-H): the table used to hold ONLY 5 placeholder entries where every real
/// model id observed on this machine (claude-opus-5, claude-sonnet-5, claude-opus-4-8,
/// claude-fable-5, claude-haiku-4-5-*) fell through to a 200,000-token default -- wrong for
/// several of them, producing used_percentage values over 100% (up to ~204%) for real
/// historical sessions. Entries below marked "[VERIFIED-DISK]" were derived by this pass by
/// directly scanning every real transcript under %USERPROFILE%\.claude\projects\C--projects\
/// for the last assistant entry's usage.{input_tokens,cache_creation_input_tokens,
/// cache_read_input_tokens} per model id, on 2026-08-13 (~40 real session files, several
/// thousand real assistant entries). A model whose real observed usage on this machine
/// exceeds 200,000 in any single successful turn PROVES the 200,000 default is wrong for it
/// (Anthropic's API would reject the request otherwise) -- that is real, on-disk evidence,
/// not a guess. Entries NOT re-confirmed this pass (or with no contradicting evidence) are
/// left out of the table on purpose so they honestly fall through to the unmatched default
/// (see Resolve's `matched=false`) rather than silently claiming a verified value they don't
/// have -- see RootsTreeBuilder's use of `matched` for AgentTreeDto.ContextWindowSizeAssumed.
/// </summary>
public static class ModelWindowTable
{
    /// <summary>
    /// The fallback context-window size for any model id that matches nothing in the
    /// table below. Per project.md, this is a hard default - unlike other metrics fields,
    /// context-window size is never reported as fully "unknown".
    /// </summary>
    public const int DefaultWindow = 200000;

    private static readonly (string Prefix, int Window)[] Table =
    {
        // Pre-existing entries (project.md: 1,000,000 for "extended-context [1m]" ids) - kept
        // as-is, not re-verified this pass, but not contradicted by anything found either.
        ("claude-opus-4-1m", 1_000_000),
        ("claude-sonnet-4-1m", 1_000_000),

        // [VERIFIED-DISK], 2026-08-13: claude-opus-4-8's real observed usage repeatedly
        // clusters just UNDER 1,000,000 (top values 996706 / 996706 / 996706 / 992493 /
        // 991860... - a clear ceiling, never once exceeding 1,000,000 across ~2600 real
        // assistant entries scanned) in
        // C:\Users\a.sintes\.claude\projects\C--projects\787d9fa7-a916-4b73-9d9d-86de8402e618.jsonl
        // and others. This is the strongest single piece of real evidence found this pass -
        // a hard 1,000,000 ceiling that is never crossed is exactly what a real context-window
        // limit looks like on disk.
        ("claude-opus-4-8", 1_000_000),

        // [VERIFIED-DISK], 2026-08-13: claude-sonnet-5's real observed usage reaches 408,165
        // tokens in a single successful turn (session
        // 5604b0d8-b3b1-409b-85a8-dde5fb675a5b.jsonl, ~3573 real assistant entries scanned,
        // 1091 of them already over 200,000) - conclusively rules out the 200,000 default.
        // No session on this machine has yet driven usage close enough to 1,000,000 to
        // confirm that exact ceiling the way claude-opus-4-8's data does, but 1,000,000 is
        // the account-wide extended-context tier already confirmed for claude-opus-4-8 above,
        // is consistent with (not contradicted by) the 408,165 lower bound, and matches this
        // project's own handoff notes citing an earlier live statusLine capture of
        // "claude-sonnet-5" reporting context_window_size:1000000. Flagged here rather than
        // silently trusted: a fresh `accel run --dump-raw` capture against a live statusLine
        // tick would be the strongest possible confirmation and was attempted this pass (no
        // already-running server was found, and this pass could not force a live statusLine
        // tick from a subagent context) - a future pass with an interactive live session
        // should re-confirm the exact ceiling the same way it was confirmed for opus-4-8.
        ("claude-sonnet-5", 1_000_000),

        // [VERIFIED-DISK], 2026-08-13: claude-opus-5 reaches 312,511 tokens in a single real
        // turn (session ed1015fa-057f-42cf-b6cd-83d7c0bfd8e5.jsonl) - rules out 200,000.
        // Same reasoning/caveat as claude-sonnet-5 above for the exact 1,000,000 figure.
        ("claude-opus-5", 1_000_000),

        // [VERIFIED-DISK], 2026-08-13: claude-fable-5 reaches 430,145 tokens in a single real
        // turn (session 32856e06-0f59-49b6-822e-c2dd5ca2be1d.jsonl) - rules out 200,000.
        // Same reasoning/caveat as claude-sonnet-5 above for the exact 1,000,000 figure.
        ("claude-fable-5", 1_000_000),

        // claude-haiku-4-5-* (dated ids, e.g. "claude-haiku-4-5-20251001"): NOT added here.
        // Real observed usage on this machine tops out at 181,577 tokens (session
        // a49baaed-8989-4c21-907a-f75d95ae29a5.jsonl, ~410 real assistant entries scanned,
        // none over 200,000) - i.e. no evidence contradicts the 200,000 default, but also no
        // session has pushed hard enough against it to confirm it either. Left unmatched on
        // purpose so it falls through to DefaultWindow with `matched=false` - unverified,
        // defaults to 200K, may be wrong; a future pass with either a longer real haiku
        // session or a live statusLine capture should confirm the real number.
    };

    /// <summary>
    /// Resolves <paramref name="modelId"/> to a context-window size. Never throws on
    /// null/empty input - returns <see cref="DefaultWindow"/> in that case, same as for
    /// any unrecognized string.
    /// </summary>
    public static int Resolve(string? modelId) => Resolve(modelId, out _);

    /// <summary>
    /// Same resolution as <see cref="Resolve(string?)"/>, additionally reporting via
    /// <paramref name="matched"/> whether an exact or prefix table entry was found
    /// (<c>true</c>) versus falling through to <see cref="DefaultWindow"/> because nothing in
    /// the table matched at all (<c>false</c>). Callers that need to render a value as
    /// "assumed" (e.g. Phase UI-D's <c>GET /roots/tree</c>) use this to distinguish "we found a
    /// mapping for this model id" from "we have no idea, defaulted" - both are still values
    /// straight out of this placeholder table, not observed data, but only the latter is a
    /// blind guess about the model itself.
    /// </summary>
    public static int Resolve(string? modelId, out bool matched)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            matched = false;
            return DefaultWindow;
        }

        foreach (var (prefix, window) in Table)
        {
            if (string.Equals(prefix, modelId, StringComparison.Ordinal))
            {
                matched = true;
                return window;
            }
        }

        int bestPrefixLength = -1;
        int bestWindow = DefaultWindow;

        foreach (var (prefix, window) in Table)
        {
            if (prefix.Length > bestPrefixLength && modelId.StartsWith(prefix, StringComparison.Ordinal))
            {
                bestPrefixLength = prefix.Length;
                bestWindow = window;
            }
        }

        matched = bestPrefixLength >= 0;
        return bestWindow;
    }
}

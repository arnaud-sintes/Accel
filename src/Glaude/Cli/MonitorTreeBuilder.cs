namespace Glaude.Cli;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Glaude.Metrics;

/// <summary>Visual state a rendered node should carry - drives the WinForms styling
/// (bold + <c>●</c> for <see cref="Live"/>, muted <see cref="GrayText"/>-style with no prefix
/// for <see cref="Historical"/>, muted style with a <c>?</c> prefix for <see cref="Stale"/>).
/// Deliberately its own enum rather than a raw status string, so <see cref="MonitorForm"/>
/// never has to string-compare against the wire vocabulary.</summary>
public enum MonitorNodeState
{
    Live,
    Historical,
    Stale,
}

/// <summary>The six-column data ("ID | Name | Type | Model | Effort | Context") shown per row in
/// the owner-drawn <c>MonitorForm</c> tree - a pure, testable projection kept separate from the
/// legacy single-line <c>Text</c> (still produced alongside it for existing callers/tests).</summary>
public sealed record MonitorRowColumns(string Id, string Name, string Type, string Model, string Effort, string Context)
{
    public static readonly MonitorRowColumns Empty = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
}

/// <summary>One rendered sub-agent leaf.</summary>
public sealed record MonitorAgentNode(string AgentId, string Text, MonitorNodeState State, MonitorRowColumns Columns);

/// <summary>One rendered session node, with its (already live-filtered by the server) sub-agents.</summary>
public sealed record MonitorSessionNode(string SessionId, string Text, MonitorNodeState State, MonitorAgentNode[] Agents, MonitorRowColumns Columns);

/// <summary>One rendered root-folder node. <see cref="OrphanAgents"/> is only ever non-empty for
/// the synthetic "(unattributed)" root - a normal configured root's agents are always nested
/// under one of its <see cref="Sessions"/>.</summary>
public sealed record MonitorRootNode(string Path, string Text, MonitorSessionNode[] Sessions, MonitorAgentNode[] OrphanAgents, MonitorRowColumns Columns);

/// <summary>The whole tree the WinForms window renders: the configured roots in order, plus an
/// optional sibling "(unattributed)" node - see project-ui.md's "Rendering" section.</summary>
public sealed record MonitorTree(MonitorRootNode[] Roots, MonitorRootNode? Unattributed);

/// <summary>One computed column slot: the header text (empty for the leading state-glyph slot),
/// its X offset from the row's left edge, and its width. Returned by
/// <see cref="MonitorColumnLayout.Compute"/> - see that method's doc comment for the layout
/// rules.</summary>
public readonly record struct MonitorColumnSlot(string Header, int X, int Width);

/// <summary>
/// Pure (no <c>System.Windows.Forms</c> dependency) computation of the six-plus-one column
/// X-offsets/widths <c>MonitorForm</c>'s header strip and owner-drawn rows both use - the single
/// source of truth so the two paint paths can never drift out of alignment with each other. Kept
/// here (alongside the other WinForms-free pure logic in this file) rather than in
/// <c>MonitorForm.cs</c> so it is unit-testable headlessly.
/// </summary>
public static class MonitorColumnLayout
{
    /// <summary>Gap, in pixels, left between adjacent columns.</summary>
    private const int Gap = 2;

    /// <summary>X offset of the first (state-glyph) column - chosen to clear the native
    /// expand-glyph/indent area for the deepest node level, see <c>MonitorForm</c>'s original
    /// comment on this constant. Does not scale with the available width - only the flexible
    /// Name/Model/Context columns do.</summary>
    private const int GlyphX = 64;

    private const int GlyphWidth = 20;
    private const int IdWidth = 90;
    private const int TypeWidth = 90;
    private const int EffortWidth = 60;

    /// <summary>Smallest width any single column - fixed or flexible - is ever allowed to shrink
    /// to, even at absurdly small available widths, so layout math never goes negative/zero.</summary>
    private const int MinColumnWidth = 30;

    /// <summary>Relative share of the leftover width after the fixed columns that each flexible
    /// column gets: Name gets the most room, Model the least, Context a bit more than Model since
    /// it holds a formatted "12.3% of 200K (assumed)" string.</summary>
    private const int NameWeight = 6;
    private const int ModelWeight = 4;
    private const int ContextWeight = 5;

    /// <summary>Computes the seven column slots - state glyph, ID, Name, Type, Model, Effort,
    /// Context, in that left-to-right order - for a row/header of <paramref name="availableWidth"/>
    /// pixels. ID/Type/Effort stay fixed-width (they only ever hold short truncated ids or short
    /// words); Name/Model/Context share whatever width remains, proportionally, so widening the
    /// window gives them more room instead of leaving a fixed pixel gap. Every column is clamped to
    /// <see cref="MinColumnWidth"/> so even an extremely narrow <paramref name="availableWidth"/>
    /// (including 0 or negative) yields valid, non-negative, strictly increasing offsets rather than
    /// throwing.</summary>
    public static MonitorColumnSlot[] Compute(int availableWidth)
    {
        int fixedTotal = GlyphWidth + IdWidth + TypeWidth + EffortWidth;
        int gapsTotal = Gap * 6; // 6 gaps between the 7 columns
        int flexTotal = Math.Max(availableWidth - GlyphX - fixedTotal - gapsTotal, MinColumnWidth * 3);

        int weightSum = NameWeight + ModelWeight + ContextWeight;
        int nameWidth = Math.Max(flexTotal * NameWeight / weightSum, MinColumnWidth);
        int modelWidth = Math.Max(flexTotal * ModelWeight / weightSum, MinColumnWidth);
        int contextWidth = Math.Max(flexTotal * ContextWeight / weightSum, MinColumnWidth);

        int glyphX = GlyphX;
        int idX = glyphX + GlyphWidth + Gap;
        int nameX = idX + IdWidth + Gap;
        int typeX = nameX + nameWidth + Gap;
        int modelX = typeX + TypeWidth + Gap;
        int effortX = modelX + modelWidth + Gap;
        int contextX = effortX + EffortWidth + Gap;

        return new[]
        {
            new MonitorColumnSlot(string.Empty, glyphX, GlyphWidth),
            new MonitorColumnSlot("ID", idX, IdWidth),
            new MonitorColumnSlot("Name", nameX, nameWidth),
            new MonitorColumnSlot("Type", typeX, TypeWidth),
            new MonitorColumnSlot("Model", modelX, modelWidth),
            new MonitorColumnSlot("Effort", effortX, EffortWidth),
            new MonitorColumnSlot("Context", contextX, contextWidth),
        };
    }
}

/// <summary>
/// Pure, side-effect-free translation from the wire <see cref="RootsTreeDto"/> to the tree shape
/// <see cref="MonitorForm"/> renders. Kept entirely free of <c>System.Windows.Forms</c> so it is
/// unit-testable without a real window/message loop - <see cref="MonitorForm"/> merely walks the
/// result and creates <see cref="System.Windows.Forms.TreeNode"/>s from it.
/// </summary>
public static class MonitorTreeBuilder
{
    /// <summary>Matches <c>SessionsView.IdTruncateLength</c> - the CLI's own session-id truncation
    /// convention, reused here for the tree's session label.</summary>
    private const int IdTruncateLength = 12;

    private const string UnattributedLabel = "(unattributed)";
    private const string NoSessionsLabel = "(no sessions)";
    private const string EmDash = "—";

    public static MonitorTree Build(RootsTreeDto? dto)
    {
        var rootDtos = dto?.Roots ?? Array.Empty<RootTreeDto>();
        var roots = rootDtos.Select(BuildRootNode).ToArray();

        var unattributedSessions = dto?.UnattributedSessions ?? Array.Empty<SessionTreeDto>();
        var unattributedAgents = dto?.UnattributedAgents ?? Array.Empty<AgentTreeDto>();

        MonitorRootNode? unattributed = null;
        if (unattributedSessions.Length > 0 || unattributedAgents.Length > 0)
        {
            var sessionNodes = unattributedSessions.Select(BuildSessionNode).ToArray();
            var agentNodes = unattributedAgents.Select(BuildAgentNode).ToArray();
            var unattributedColumns = new MonitorRowColumns(EmDash, UnattributedLabel, string.Empty, string.Empty, string.Empty, string.Empty);
            unattributed = new MonitorRootNode(UnattributedLabel, UnattributedLabel, sessionNodes, agentNodes, unattributedColumns);
        }

        return new MonitorTree(roots, unattributed);
    }

    private static MonitorRootNode BuildRootNode(RootTreeDto root)
    {
        string path = root.Path ?? "(unknown root)";
        var sessionDtos = root.Sessions ?? Array.Empty<SessionTreeDto>();
        var sessions = sessionDtos.Select(BuildSessionNode).ToArray();

        int liveCount = sessions.Count(s => s.State == MonitorNodeState.Live);
        string text = sessions.Length == 0
            ? path
            : $"{path} ({sessions.Length} sessions, {liveCount} running)";
        string contextSummary = sessions.Length == 0 ? string.Empty : $"{sessions.Length} sessions, {liveCount} running";

        var columns = new MonitorRowColumns(EmDash, path, string.Empty, string.Empty, string.Empty, contextSummary);

        return new MonitorRootNode(path, text, sessions, Array.Empty<MonitorAgentNode>(), columns);
    }

    /// <summary>Renders a root's single "(no sessions)" placeholder child - kept as a helper so
    /// <see cref="MonitorForm"/> doesn't need to special-case an empty root's UI itself.</summary>
    public static string NoSessionsPlaceholder() => NoSessionsLabel;

    private static MonitorSessionNode BuildSessionNode(SessionTreeDto session)
    {
        bool isLive = session.IsLive;
        MonitorNodeState state = isLive ? MonitorNodeState.Live : MonitorNodeState.Historical;
        string prefix = isLive ? "● " : string.Empty; // "● "

        string name = string.IsNullOrEmpty(session.Name) ? "(unnamed)" : session.Name;
        string id = TruncateId(session.SessionId);
        string model = session.ModelDisplayName ?? session.ModelId ?? "unknown-model";
        string effort = session.EffortLevel ?? "?";
        string pct = FormatPercentage(session.UsedPercentage);
        string window = FormatWindowSize(session.ContextWindowSize);
        string assumed = session.ContextWindowSizeAssumed ? " (assumed)" : string.Empty;

        string text = $"{prefix}{name} — {id}… — {model} — effort={effort} — {pct}% of {window}{assumed}";
        string context = $"{pct}% of {window}{assumed}";
        var columns = new MonitorRowColumns(id, name, "session", model, effort, context);

        var agents = (session.Agents ?? Array.Empty<AgentTreeDto>()).Select(BuildAgentNode).ToArray();

        return new MonitorSessionNode(session.SessionId ?? string.Empty, text, state, agents, columns);
    }

    private static MonitorAgentNode BuildAgentNode(AgentTreeDto agent)
    {
        MonitorNodeState state = agent.Status switch
        {
            "live" => MonitorNodeState.Live,
            "stale" => MonitorNodeState.Stale,
            _ => MonitorNodeState.Historical,
        };

        string prefix = state switch
        {
            MonitorNodeState.Live => "● ", // "● "
            MonitorNodeState.Stale => "? ",
            _ => string.Empty,
        };

        string type = agent.AgentType ?? "unknown-type";
        string nameSuffix = string.IsNullOrEmpty(agent.Name) ? string.Empty : $" · {agent.Name}"; // " · name"
        string model = agent.ModelId ?? "unknown-model";
        string effort = agent.EffortLevel ?? "?";
        string pct = FormatPercentage(agent.UsedPercentage);
        string window = FormatWindowSize(agent.ContextWindowSize);
        string assumed = agent.ContextWindowSizeAssumed == true ? " (assumed)" : string.Empty;

        string text = $"{prefix}{type}{nameSuffix} — {model} — effort={effort} — {pct}%{assumed}";
        string id = TruncateId(agent.AgentId);
        string name = agent.Name ?? string.Empty;
        string context = $"{pct}% of {window}{assumed}";
        var columns = new MonitorRowColumns(id, name, type, model, effort, context);

        return new MonitorAgentNode(agent.AgentId ?? string.Empty, text, state, columns);
    }

    /// <summary>The leading state glyph rendered as its own small element (e.g. in the ID
    /// column's left margin) rather than baked into any column's text - kept as a pure function
    /// so <c>MonitorForm</c>'s owner-draw code never has to re-derive it from the enum inline.
    /// Colour is never the only signal (project-ui.md's "Rendering" section): live rows get
    /// <c>●</c>, stale rows get <c>?</c>, historical rows get nothing.</summary>
    public static string GlyphFor(MonitorNodeState state) => state switch
    {
        MonitorNodeState.Live => "●",
        MonitorNodeState.Stale => "?",
        _ => string.Empty,
    };

    private static string TruncateId(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return "(unknown)";
        }

        return id.Length <= IdTruncateLength ? id : id[..IdTruncateLength];
    }

    private static string FormatPercentage(double? value) =>
        (value ?? 0).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>"1000000" -&gt; "1M", "200000" -&gt; "200K", anything else -&gt; the raw number,
    /// null -&gt; "?". Deliberately simple (exact-multiple check only) - <c>ModelWindowTable</c>'s
    /// own placeholder table only ever produces round K/M values today.</summary>
    private static string FormatWindowSize(long? size)
    {
        if (!size.HasValue)
        {
            return "?";
        }

        long v = size.Value;
        if (v > 0 && v % 1_000_000 == 0)
        {
            return (v / 1_000_000).ToString(CultureInfo.InvariantCulture) + "M";
        }

        if (v > 0 && v % 1_000 == 0)
        {
            return (v / 1_000).ToString(CultureInfo.InvariantCulture) + "K";
        }

        return v.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Pure logic for preserving <see cref="System.Windows.Forms.TreeView"/> expand state across the
/// full-tree rebuild <see cref="MonitorForm"/> does every refresh tick - see project-ui.md's
/// "Rendering" section ("if done, key it on the stable ids (`path` / `session_id` / `agent_id`),
/// never on node index"). <see cref="MonitorRootNode.Path"/>, <see cref="MonitorSessionNode.SessionId"/>
/// and <see cref="MonitorAgentNode.AgentId"/> already are those stable ids - this type just walks
/// a <see cref="MonitorTree"/> using them as keys, with no <c>System.Windows.Forms</c> dependency
/// so it is unit-testable headlessly.
/// </summary>
public static class MonitorTreeExpansion
{
    /// <summary>Given the stable keys that were expanded in the TreeView before a rebuild, and
    /// the freshly-built <paramref name="newTree"/>, returns exactly the keys that should have
    /// <c>TreeNode.Expand()</c> re-applied: still-present nodes that were previously expanded. A
    /// previously-expanded node that no longer exists in <paramref name="newTree"/> (e.g. its
    /// session ended and got filtered, or its agent is no longer live) is simply absent from the
    /// result - no error. A brand-new node that wasn't previously expanded is likewise absent, so
    /// it starts collapsed as expected.</summary>
    public static HashSet<string> ComputeKeysToExpand(MonitorTree newTree, IReadOnlySet<string> previouslyExpandedKeys)
    {
        var toExpand = new HashSet<string>();
        if (previouslyExpandedKeys.Count == 0)
        {
            return toExpand;
        }

        foreach (var root in newTree.Roots)
        {
            VisitRoot(root, previouslyExpandedKeys, toExpand);
        }

        if (newTree.Unattributed is not null)
        {
            VisitRoot(newTree.Unattributed, previouslyExpandedKeys, toExpand);
        }

        return toExpand;
    }

    private static void VisitRoot(MonitorRootNode root, IReadOnlySet<string> expanded, HashSet<string> toExpand)
    {
        TryMark(root.Path, expanded, toExpand);

        foreach (var session in root.Sessions)
        {
            VisitSession(session, expanded, toExpand);
        }

        foreach (var agent in root.OrphanAgents)
        {
            TryMark(agent.AgentId, expanded, toExpand);
        }
    }

    private static void VisitSession(MonitorSessionNode session, IReadOnlySet<string> expanded, HashSet<string> toExpand)
    {
        TryMark(session.SessionId, expanded, toExpand);

        foreach (var agent in session.Agents)
        {
            TryMark(agent.AgentId, expanded, toExpand);
        }
    }

    private static void TryMark(string key, IReadOnlySet<string> expanded, HashSet<string> toExpand)
    {
        // Empty keys come from degraded/"(unknown ...)" nodes (see MonitorTreeBuilder's null-dto
        // fallbacks) - never treat them as a stable identity to match on.
        if (!string.IsNullOrEmpty(key) && expanded.Contains(key))
        {
            toExpand.Add(key);
        }
    }

    /// <summary>Returns every stable key present in <paramref name="tree"/> (root paths, session
    /// ids, agent ids) - <c>MonitorForm</c> unions this into its "keys ever seen" set after each
    /// render so <see cref="ComputeDefaultExpansionForNewKeys"/> can tell brand-new nodes apart
    /// from ones it has already rendered (and therefore already has a preserved/user-chosen
    /// expand state for) on a later tick.</summary>
    public static HashSet<string> CollectAllKeys(MonitorTree tree)
    {
        var keys = new HashSet<string>();

        foreach (var root in tree.Roots)
        {
            CollectRootKeys(root, keys);
        }

        if (tree.Unattributed is not null)
        {
            CollectRootKeys(tree.Unattributed, keys);
        }

        return keys;
    }

    private static void CollectRootKeys(MonitorRootNode root, HashSet<string> keys)
    {
        if (!string.IsNullOrEmpty(root.Path))
        {
            keys.Add(root.Path);
        }

        foreach (var session in root.Sessions)
        {
            CollectSessionKeys(session, keys);
        }

        foreach (var agent in root.OrphanAgents)
        {
            if (!string.IsNullOrEmpty(agent.AgentId))
            {
                keys.Add(agent.AgentId);
            }
        }
    }

    private static void CollectSessionKeys(MonitorSessionNode session, HashSet<string> keys)
    {
        if (!string.IsNullOrEmpty(session.SessionId))
        {
            keys.Add(session.SessionId);
        }

        foreach (var agent in session.Agents)
        {
            if (!string.IsNullOrEmpty(agent.AgentId))
            {
                keys.Add(agent.AgentId);
            }
        }
    }

    /// <summary>The "expand by default" rule for nodes <see cref="MonitorForm"/> has never
    /// rendered before (i.e. their stable key is absent from <paramref name="everSeenKeys"/>):
    /// a root or session default-expands the first time it is seen if it, or any descendant of
    /// it, is <see cref="MonitorNodeState.Live"/>. This is deliberately a one-shot default, not a
    /// standing rule - once a key has been seen (it's in <paramref name="everSeenKeys"/>), its
    /// expand state is governed entirely by <see cref="ComputeKeysToExpand"/>'s preservation of
    /// whatever the user (or a previous default) last left it as, even if it is still live on
    /// this tick. Without that distinction, a user who deliberately collapses a still-live session
    /// to declutter the view would find it snapping back open every ~2s refresh.
    ///
    /// On top of that per-node "my own key is new" check, the signal also cascades UP: a node
    /// default-expands if any of its descendants is itself newly-appeared (key not in
    /// <paramref name="everSeenKeys"/>) AND live - not just when the node's own key happens to be
    /// new at the same tick. This is what makes a sub-agent that starts five minutes into an
    /// already-rendered (and therefore already "seen") session actually visible: the session's
    /// own key stops being "new" the instant it is first rendered, long before any agent under it
    /// exists, so without the cascade the session would never re-qualify for default-expansion and
    /// the new agent would stay hidden under a collapsed parent. The cascade only fires on the tick
    /// where a genuinely new live descendant first appears - it does not mean "keep re-opening a
    /// session for as long as it contains any live agent" (that would refight the user's manual
    /// collapse); a session/root that already had that live agent last tick is not re-added by this
    /// rule, only by the ordinary "was already expanded" preservation in
    /// <see cref="ComputeKeysToExpand"/>.</summary>
    public static HashSet<string> ComputeDefaultExpansionForNewKeys(MonitorTree newTree, IReadOnlySet<string> everSeenKeys)
    {
        var toExpand = new HashSet<string>();

        foreach (var root in newTree.Roots)
        {
            VisitRootForDefaultExpansion(root, everSeenKeys, toExpand);
        }

        if (newTree.Unattributed is not null)
        {
            VisitRootForDefaultExpansion(newTree.Unattributed, everSeenKeys, toExpand);
        }

        return toExpand;
    }

    private static void VisitRootForDefaultExpansion(MonitorRootNode root, IReadOnlySet<string> everSeenKeys, HashSet<string> toExpand)
    {
        bool anyLiveDescendant = false;
        bool anySessionQualified = false;

        foreach (var session in root.Sessions)
        {
            var (liveOrHasLiveDescendant, sessionQualified) = VisitSessionForDefaultExpansion(session, everSeenKeys, toExpand);
            anyLiveDescendant |= liveOrHasLiveDescendant;
            anySessionQualified |= sessionQualified;
        }

        anyLiveDescendant |= root.OrphanAgents.Any(a => a.State == MonitorNodeState.Live);
        bool anyNewLiveOrphanAgent = root.OrphanAgents.Any(a => IsNewlyAppearedLive(a, everSeenKeys));

        bool ownNewAndLive = anyLiveDescendant && !string.IsNullOrEmpty(root.Path) && !everSeenKeys.Contains(root.Path);

        if ((ownNewAndLive || anySessionQualified || anyNewLiveOrphanAgent) && !string.IsNullOrEmpty(root.Path))
        {
            toExpand.Add(root.Path);
        }
    }

    /// <returns>
    /// <c>liveOrHasLiveDescendant</c>: <c>true</c> if this session, or any of its agents, is live
    /// (the pre-existing signal, unrelated to "new") - kept for the root's own new+live rule.
    /// <c>qualified</c>: <c>true</c> if this session was (or already had been) added to
    /// <paramref name="toExpand"/> this tick, either because its own key is new-and-live, or
    /// because it has a newly-appeared live agent - used by the caller to decide whether an
    /// ancestor root should also cascade-expand.
    /// </returns>
    private static (bool liveOrHasLiveDescendant, bool qualified) VisitSessionForDefaultExpansion(MonitorSessionNode session, IReadOnlySet<string> everSeenKeys, HashSet<string> toExpand)
    {
        bool liveOrHasLiveDescendant = session.State == MonitorNodeState.Live
            || session.Agents.Any(a => a.State == MonitorNodeState.Live);

        bool ownNewAndLive = liveOrHasLiveDescendant && !string.IsNullOrEmpty(session.SessionId) && !everSeenKeys.Contains(session.SessionId);
        bool anyNewLiveAgent = session.Agents.Any(a => IsNewlyAppearedLive(a, everSeenKeys));

        bool qualified = false;
        if ((ownNewAndLive || anyNewLiveAgent) && !string.IsNullOrEmpty(session.SessionId))
        {
            toExpand.Add(session.SessionId);
            qualified = true;
        }

        return (liveOrHasLiveDescendant, qualified);
    }

    /// <summary>An agent's own "is newly-appeared-and-live" check: its key has never been seen
    /// before AND it is currently live. This is the leaf-level signal that cascades up through
    /// <see cref="VisitSessionForDefaultExpansion"/> and <see cref="VisitRootForDefaultExpansion"/>.</summary>
    private static bool IsNewlyAppearedLive(MonitorAgentNode agent, IReadOnlySet<string> everSeenKeys) =>
        agent.State == MonitorNodeState.Live && !string.IsNullOrEmpty(agent.AgentId) && !everSeenKeys.Contains(agent.AgentId);
}

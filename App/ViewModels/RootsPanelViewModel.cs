namespace Accel.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Accel.App.Services;
using Accel.Cli;
using Accel.Metrics;

/// <summary>What a <see cref="RootsPanelNodeViewModel"/> represents - the WPF equivalent of the
/// three <c>Build*TreeNode</c> helpers in <c>MonitorForm</c>, plus the "(no sessions)" placeholder
/// row it synthesizes for an empty root.</summary>
public enum RootsPanelNodeKind
{
    Root,
    Session,
    Agent,
    Placeholder,
}

/// <summary>
/// One row in panel A's tree. Shape and content come straight from the pure
/// <see cref="MonitorTreeBuilder"/> DTOs (which in turn come from
/// <see cref="RootsTreeBuilder"/>'s <see cref="RootsTreeDto"/>), so the WPF panel and the WinForms
/// window render the same data with the same formatting until CX-T1 retires the latter.
///
/// <para><see cref="Key"/> is the <b>stable</b> identity used to preserve expansion and selection
/// across the full rebuild each telemetry tick performs: root path / session id / agent id, exactly
/// the keys <c>MonitorForm</c> stores in <c>TreeNode.Tag</c> and
/// <see cref="MonitorTreeExpansion"/> matches on. Placeholder rows have an empty key and are
/// therefore never matched - the same rule <see cref="MonitorTreeExpansion"/> applies to degraded
/// nodes.</para>
/// </summary>
public sealed partial class RootsPanelNodeViewModel : ObservableObject
{
    private readonly RootsPanelViewModel? _owner;
    private SessionVisualState _visualState;
    private string _automationDescription = string.Empty;

    public RootsPanelNodeViewModel(
        string key,
        string text,
        RootsPanelNodeKind kind,
        MonitorNodeState state,
        MonitorRowColumns columns,
        RootsPanelViewModel? owner = null,
        bool isFocused = false,
        string projectDir = "",
        long? durationMs = null,
        long? consumedTokens = null,
        DateTime? waitingSinceUtc = null,
        bool isWaiting = false,
        string cwd = "")
    {
        Key = key ?? string.Empty;
        Text = text ?? string.Empty;
        Kind = kind;
        State = state;
        Columns = columns ?? MonitorRowColumns.Empty;
        ProjectDir = projectDir ?? string.Empty;
        Cwd = cwd ?? string.Empty;
        DurationMs = durationMs;
        ConsumedTokens = consumedTokens;
        _owner = owner;
        _isFocused = isFocused;
        WaitingSinceUtc = waitingSinceUtc;
        _isWaiting = isWaiting;

        _visualState = SessionVisualStateResolver.Resolve(IsRunning, IsFocused);

        ModelBadge = (Kind == RootsPanelNodeKind.Session || Kind == RootsPanelNodeKind.Agent) && !string.IsNullOrEmpty(Columns.Model)
            ? ModelBadgeTable.Resolve(Columns.Model)
            : ModelBadge.Unmatched;
        ShowModelBadge = Kind == RootsPanelNodeKind.Session || Kind == RootsPanelNodeKind.Agent;

        EffortLevel = ShowModelBadge ? EffortBarLevel.Resolve(Columns.Effort) : 0;
        ShowEffortBars = ShowModelBadge && ModelEffortTable.SupportsEffort(ModelBadge);

        _automationDescription = BuildAutomationDescription();
        TooltipText = BuildTooltipText();
    }

    /// <summary>Stable id (root path / session id / agent id); empty for placeholder rows.</summary>
    public string Key { get; }

    /// <summary>The single-line label, identical to the WinForms row text (id/model/effort/context
    /// baked in for session/agent rows, "(N sessions, M running)" baked in for a root). Kept only for
    /// the WinForms/CLI parity tests and as <see cref="DisplayText"/>'s fallback - the tree itself
    /// renders <see cref="DisplayText"/>.</summary>
    public string Text { get; }

    /// <summary>What the tree actually shows for this row: just the plain name (
    /// <see cref="Columns"/>'s Name column) for a root's directory path or a session's display name -
    /// none of <see cref="Text"/>'s baked-in id/model/effort/context/session-count decoration, which
    /// now lives in <see cref="TooltipText"/> instead. Agent/placeholder rows keep rendering the full
    /// <see cref="Text"/> unchanged: an agent's <see cref="MonitorRowColumns.Name"/> can be empty (an
    /// unnamed agent), where falling back to the plain name would render a blank row.</summary>
    public string DisplayText => Kind switch
    {
        RootsPanelNodeKind.Root or RootsPanelNodeKind.Session when !string.IsNullOrEmpty(Columns.Name) => Columns.Name,
        _ => Text,
    };

    /// <summary>A root row's "(N sessions, M running)" summary, in the least-emphasized form (a
    /// plain, non-bold suffix appended after <see cref="DisplayText"/>'s bold path) - the one piece
    /// of <see cref="Text"/>'s decoration that stays visible in the row itself rather than moving
    /// into <see cref="TooltipText"/>, since it is a summary of the row's own children, not detail
    /// about a single session. Empty for every non-root row, and for an empty root (no
    /// "(0 sessions...)" - see <c>MonitorTreeBuilder.BuildRootNode</c>, which likewise never
    /// synthesizes that count for a root with no sessions).</summary>
    public string RootSummarySuffix =>
        Kind == RootsPanelNodeKind.Root && !string.IsNullOrEmpty(Columns.Context)
            ? $" ({Columns.Context})"
            : string.Empty;

    public RootsPanelNodeKind Kind { get; }

    /// <summary>Live/Historical/Stale - P1-T4 turns this into glyphs/weights; P1-T2 only carries it.</summary>
    public MonitorNodeState State { get; }

    /// <summary>The six-column projection (ID | Name | Type | Model | Effort | Context).</summary>
    public MonitorRowColumns Columns { get; }

    /// <summary>
    /// The transcript's project slug (empty for root/agent/placeholder rows) - carried through from
    /// <see cref="MonitorSessionNode.ProjectDir"/> purely so P4-T4's "Remove session" action can resolve
    /// <c>projects/&lt;slug&gt;/&lt;sessionId&gt;[.jsonl]</c> (<see cref="SessionRemover.Plan"/>'s
    /// <c>projectDir</c> parameter) without a second disk scan.
    /// </summary>
    public string ProjectDir { get; }

    /// <summary>
    /// The session's own recorded working directory (<see cref="MonitorSessionNode.Cwd"/> -> the
    /// transcript head's <c>cwd</c>), empty for root/agent/placeholder rows and for a session whose
    /// transcript never recorded one. <b>Not</b> the same thing as <see cref="ProjectDir"/>, which is
    /// a <c>~/.claude/projects</c> slug and is not a usable filesystem path.
    ///
    /// <para>This is what "Resume session" launches <c>claude</c> in
    /// (<see cref="RootsPanelViewModel.ResolveWorkingDirectoryFor"/>): the owning root row's key is
    /// the wrong answer for a session under the synthetic "(unattributed)" root, because that key is
    /// the literal label <c>"(unattributed)"</c>, not a directory. Unvalidated here - callers go
    /// through <see cref="RootsPanelViewModel.ResolveWorkingDirectoryFor"/>, which is the one place
    /// that checks the directory still exists.</para>
    /// </summary>
    public string Cwd { get; }

    /// <summary>Raw, unformatted duration/consumed-tokens values behind
    /// <see cref="DurationText"/>/<see cref="TokensText"/> - null for rows with no data (root/
    /// placeholder rows, or a session/agent whose start time couldn't be resolved), distinct
    /// from a real <c>0</c>, mirroring <see cref="MonitorSessionNode"/>/<see cref="MonitorAgentNode"/>.</summary>
    public long? DurationMs { get; }

    /// <summary>See <see cref="DurationMs"/>.</summary>
    public long? ConsumedTokens { get; }

    /// <summary>The already-formatted duration string (e.g. "7m 04s", or "—" when unknown) - binding
    /// symmetry with the existing <see cref="Columns"/>-derived surface.</summary>
    public string DurationText => Columns.Duration;

    /// <summary>The already-formatted consumed-tokens string (e.g. "148.2K", or "—" when unknown).</summary>
    public string TokensText => Columns.Tokens;

    /// <summary>Whether this row currently represents a live/running session or agent - the
    /// "IsRunning" half of P1-T4 / locked-in decision 9's IsRunning x IsFocused visual-state axis.
    /// Derived from <see cref="State"/>: both <see cref="MonitorNodeState.Historical"/> and
    /// <see cref="MonitorNodeState.Stale"/> collapse onto "not running" for this axis - only
    /// <see cref="MonitorNodeState.Live"/> counts as running.</summary>
    public bool IsRunning => State == MonitorNodeState.Live;

    /// <summary>
    /// The "IsFocused" half of the same axis - <b>wired for real as of P3-T1</b>: it is
    /// <c>Key == ISessionSelectionService.FocusedSessionId</c> (case-insensitive), pushed in by
    /// <see cref="RootsPanelViewModel"/> both at construction and whenever the selection service
    /// broadcasts a change. Only session rows can ever match, since the focused id is a session GUID.
    ///
    /// <para>Setting it re-derives <see cref="VisualState"/> and <see cref="AutomationDescription"/> and
    /// raises change notifications for both, so panel A's four-state style (and its screen-reader text)
    /// follow a selection change live, without a full tree rebuild. Panel C's <c>TabsViewModel</c> remains
    /// the only writer of the selection itself; this setter is a projection of it, not a second
    /// authority.</para>
    /// </summary>
    [ObservableProperty]
    private bool _isFocused;

    partial void OnIsFocusedChanged(bool value)
    {
        VisualState = SessionVisualStateResolver.Resolve(IsRunning, value);
        AutomationDescription = BuildAutomationDescription();
    }

    /// <summary>
    /// The most recent <c>Stop</c>-hook timestamp for this session (only ever set for
    /// <see cref="RootsPanelNodeViewModel.Kind"/> <see cref="RootsPanelNodeKind.Session"/>) - null
    /// when the session has never gone through a Stop event, or when this row isn't a session row
    /// at all. Purely a raw value carried through from <see cref="MonitorSessionNode.WaitingSinceUtc"/>
    /// so <see cref="RootsPanelViewModel"/>'s acknowledgment tracking can compare it against the
    /// last-focused timestamp without a second lookup back into the tree DTO.
    /// </summary>
    public DateTime? WaitingSinceUtc { get; }

    /// <summary>
    /// Set by <see cref="RootsPanelViewModel"/> at build time (unlike <see cref="IsFocused"/>, this is
    /// not itself re-derived on a focus change - see <see cref="RootsPanelViewModel.ApplyFocus"/>,
    /// which flips it straight to false the instant this row becomes focused, without waiting for the
    /// next full rebuild): true while this session has a <see cref="WaitingSinceUtc"/> timestamp the
    /// user has not yet acknowledged by focusing this row's tab - the TODO's "highlighted until the
    /// user focuses the tab" behaviour. Drives <c>MainWindow.xaml</c>'s row-highlight trigger.
    /// </summary>
    [ObservableProperty]
    private bool _isWaiting;

    /// <summary>Glyph/weight/colour/automation-name for this row's current IsRunning x IsFocused
    /// combination - see <see cref="SessionVisualStateResolver"/>. Re-derived when
    /// <see cref="IsFocused"/> changes (<see cref="IsRunning"/> only ever changes via a rebuild, which
    /// creates fresh nodes).</summary>
    public SessionVisualState VisualState
    {
        get => _visualState;
        private set => SetProperty(ref _visualState, value);
    }

    /// <summary>The letter-in-chip model badge (O/S/H/F/?) for this row, per
    /// <see cref="ModelBadgeTable"/> - only meaningful for <see cref="RootsPanelNodeKind.Session"/>
    /// and <see cref="RootsPanelNodeKind.Agent"/> rows; see <see cref="ShowModelBadge"/>.</summary>
    public ModelBadge ModelBadge { get; }

    /// <summary>Whether a model badge should render for this row at all (root/placeholder rows
    /// never have a model).</summary>
    public bool ShowModelBadge { get; }

    /// <summary>1-4 stacked signal bars representing effort level (0 = unknown/unset), per
    /// <see cref="EffortBarLevel"/>.</summary>
    public int EffortLevel { get; }

    /// <summary>Whether the effort-bar badge should render for this row at all - false for a
    /// placeholder/root row (no model at all) or a Haiku row (a model family with no effort concept,
    /// per <see cref="ModelEffortTable"/>).</summary>
    public bool ShowEffortBars { get; }

    /// <summary>Accessible text description of this row's state, for
    /// <c>AutomationProperties.Name</c> - never rely on colour alone for accessibility (P1-T4's
    /// hard requirement), so screen readers get the same running/focused information sighted
    /// users get from the glyph/weight/colour.</summary>
    public string AutomationDescription
    {
        get => _automationDescription;
        private set => SetProperty(ref _automationDescription, value);
    }

    /// <summary>Hover tooltip text: id, model, effort, and context-window size, all already
    /// available on <see cref="Columns"/> (e.g. "Session 5604b0d8… — Sonnet 5 — effort=high — 12.3%
    /// of 1M (assumed)") - the detail <see cref="DisplayText"/> deliberately no longer bakes into the
    /// row's own label.</summary>
    public string TooltipText { get; }

    public ObservableCollection<RootsPanelNodeViewModel> Children { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Whether the search box's current text matches this row or any descendant - see
    /// <see cref="RootsPanelViewModel.ApplyFilter"/>. Defaults to true (every row visible) so a
    /// panel with no search text active never needs to touch this at all.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    partial void OnIsSelectedChanged(bool value) => _owner?.OnNodeSelectionChanged(this, value);

    private string BuildAutomationDescription()
    {
        string kindLabel = Kind switch
        {
            RootsPanelNodeKind.Root => "Root",
            RootsPanelNodeKind.Session => "Session",
            RootsPanelNodeKind.Agent => "Agent",
            _ => "Row",
        };

        return Kind is RootsPanelNodeKind.Session or RootsPanelNodeKind.Agent
            ? $"{kindLabel}: {Text}. {VisualState.AutomationName}."
            : $"{kindLabel}: {Text}.";
    }

    private string BuildTooltipText()
    {
        if (!ShowModelBadge || string.IsNullOrEmpty(Columns.Id))
        {
            return Text;
        }

        string kindLabel = Kind == RootsPanelNodeKind.Agent ? "Agent" : "Session";
        string model = string.IsNullOrEmpty(Columns.Model) ? "unknown-model" : Columns.Model;
        string effort = string.IsNullOrEmpty(Columns.Effort) ? "?" : Columns.Effort;

        string tooltip = string.IsNullOrEmpty(Columns.Context)
            ? $"{kindLabel} {Columns.Id} — {model} — effort={effort}"
            : $"{kindLabel} {Columns.Id} — {model} — effort={effort} — {Columns.Context}";

        // Appended only when there is actual data - "" (root/placeholder rows built with the
        // legacy 6-arg MonitorRowColumns constructor) and the formatted "no data" em dash
        // (MonitorTreeBuilder.FormatDuration/FormatTokenCount's null -> "—") both mean "nothing
        // to show" here, so the tooltip string for a row without duration/tokens data is
        // byte-for-byte unchanged - a compatibility guarantee pinned by RootsPanelViewModelTests.
        const string noData = "—";
        if (!string.IsNullOrEmpty(Columns.Duration) && Columns.Duration != noData)
        {
            tooltip += $" — {Columns.Duration}";
        }

        if (!string.IsNullOrEmpty(Columns.Tokens) && Columns.Tokens != noData)
        {
            tooltip += $" — {Columns.Tokens}";
        }

        return tooltip;
    }

    public override string ToString() => Text;
}

/// <summary>
/// Panel A's ViewModel (P1-T2): a read-only tree of every configured root, the sessions found under
/// it, and each live session's live sub-agents - fed exclusively by <see cref="ITelemetryFeed"/>.
///
/// <para><b>No direct event wiring:</b> this class never touches <see cref="SessionState.Changed"/>,
/// a <c>FileSystemWatcher</c>, a timer, or <c>HttpClient</c>. It receives whole
/// <see cref="RootsTreeDto"/> snapshots from the feed (already debounced at 250 ms and already
/// marshalled onto the UI thread) and additionally posts its own handler through the same
/// <see cref="IUiThreadDispatcher"/> so that even a feed double that publishes off-thread cannot
/// mutate the <see cref="ObservableCollection{T}"/>s from the wrong thread.</para>
///
/// <para><b>Rebuild semantics are <c>MonitorForm.RenderTree</c>'s, not an approximation of them:</b>
/// each snapshot is turned into a <c>MonitorTree</c> by the same
/// <see cref="MonitorTreeBuilder.Build"/>; the currently-expanded stable keys and the selected
/// stable key are captured <i>before</i> the collections are cleared; the rebuilt tree's
/// re-expansion set is computed by the same pure
/// <see cref="MonitorTreeExpansion.ComputeKeysToExpand"/> unioned with
/// <see cref="MonitorTreeExpansion.ComputeDefaultExpansionForNewKeys"/> against the same
/// ever-seen-keys set (so a first-time-seen live node auto-expands exactly once, and a user's
/// deliberate collapse of a still-live session is never refought); and the previous selection is
/// re-applied by key lookup, silently dropped if that node no longer exists. Scroll position is the
/// one thing not restored: WPF's <c>TreeView</c> has no <c>TopNode</c> equivalent to key on, and
/// P1-T4 owns panel A's presentation.</para>
/// </summary>
public sealed partial class RootsPanelViewModel : ObservableObject, IDisposable
{
    private readonly ITelemetryFeed _feed;
    private readonly IUiThreadDispatcher _dispatcher;

    /// <summary>Every stable key this ViewModel instance has ever rendered - the exact role of
    /// <c>MonitorForm._everSeenKeys</c>, including its "only ever grows, one instance per window"
    /// lifetime.</summary>
    private readonly HashSet<string> _everSeenKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-session id, the <see cref="MonitorSessionNode.WaitingSinceUtc"/> value that was already
    /// "seen" by the user focusing that session's tab - the TODO's "highlighted until the user
    /// focuses the tab" acknowledgment. Persisted across rebuilds (every <see cref="Rebuild"/>
    /// creates brand-new node instances, so this cannot live on the node itself) the same way
    /// <see cref="_everSeenKeys"/> is. A session absent from this dictionary, or whose current
    /// <see cref="MonitorSessionNode.WaitingSinceUtc"/> is later than the recorded value, is still
    /// "waiting" - see <see cref="ApplyFocus"/> (which writes into it) and <see cref="BuildSessionNode"/>
    /// (which reads it).
    /// </summary>
    private readonly Dictionary<string, DateTime> _waitingAcknowledgedUtc = new(StringComparer.Ordinal);

    /// <summary>
    /// Every session id currently rendered as <see cref="RootsPanelNodeViewModel.IsWaiting"/> as of
    /// the last <see cref="Rebuild"/> - purely so that rebuild can edge-trigger
    /// <see cref="SessionWaitingForAttention"/> only for a session that just started waiting, rather
    /// than re-raising it every ~2s tick for as long as the session stays unacknowledged.
    /// </summary>
    private readonly HashSet<string> _lastKnownWaitingSessionIds = new(StringComparer.Ordinal);

    /// <summary>Set while <see cref="Rebuild"/> mutates the tree, so the node-driven selection
    /// tracking below doesn't mistake "the old node was thrown away" for "the user changed the
    /// selection" and lose the key we are about to restore.</summary>
    private bool _rebuilding;

    private bool _disposed;

    private readonly IFolderPickerService _folderPicker;
    private readonly IUserConfirmationService _confirmation;
    private readonly string _configPath;

    /// <summary>P3-T1's focus signal, read-only (panel C writes it). Null in the pure-scaffolding /
    /// pre-P3 construction paths, in which case every row's <c>IsFocused</c> simply stays false - the
    /// same behaviour this panel had while <c>IsFocused</c> was a stub.</summary>
    private readonly ISessionSelectionService? _selection;

    /// <summary>
    /// P1-T3b's root add/remove commands need a folder picker, a confirmation prompt, and the
    /// <c>accel-folders.json</c> path to mutate - all three are optional constructor parameters
    /// (defaulting to the real dialogs / the real durable-home config path) so every existing
    /// call site, including <c>RootsPanelViewModelTests</c>'s two-argument construction, keeps
    /// compiling unchanged.
    /// </summary>
    public RootsPanelViewModel(
        ITelemetryFeed feed,
        IUiThreadDispatcher dispatcher,
        IFolderPickerService? folderPicker = null,
        IUserConfirmationService? confirmation = null,
        string? configPath = null,
        ISessionSelectionService? selection = null)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _folderPicker = folderPicker ?? new WinFormsFolderPickerService();
        _confirmation = confirmation ?? new MessageBoxConfirmationService();
        _configPath = configPath ?? Accel.Server.RootFoldersConfig.ResolveWritePath();
        _selection = selection;

        _feed.SnapshotAvailable += OnSnapshotAvailable;
        _feed.SnapshotFailed += OnSnapshotFailed;

        // P3-T1: read-only consumer of the selection hub - panel C is the only writer. The
        // subscription is weak (see SessionSelectionService), so a panel that is dropped without
        // Dispose still cannot be resurrected by the service; Dispose unsubscribes anyway.
        _selection?.Subscribe(this, OnFocusedSessionChanged);

        // A feed that already has a snapshot (e.g. Start() was called before this panel was
        // constructed) must not leave the panel blank until the next change signal.
        if (_feed.Latest is { } latest)
        {
            Rebuild(latest);
        }
    }

    /// <summary>The root-level rows: every configured root, in config order, plus the synthetic
    /// "(unattributed)" node last when it exists - same order as <c>MonitorForm.RenderTree</c>.</summary>
    public ObservableCollection<RootsPanelNodeViewModel> Roots { get; } = new();

    /// <summary>Human-readable feed status, mirroring <c>MonitorForm</c>'s status strip (including
    /// its "Refresh failed: …" text on a failed rebuild) plus the counts that make an
    /// empty-but-working tree distinguishable from a broken one.</summary>
    [ObservableProperty]
    private string _statusText = "Waiting for telemetry…";

    /// <summary>Stable key of the currently selected node (root path / session id / agent id), or
    /// null. Preserved across rebuilds. <see cref="ISessionSelectionService"/> (P3-T1, written only by
    /// panel C) is the authority for the <i>focused</i> session and drives each node's
    /// <see cref="RootsPanelNodeViewModel.IsFocused"/>; this property is only panel A's own tree
    /// selection, which is a different thing (clicking a row does not steal focus from panel C).</summary>
    [ObservableProperty]
    private string? _selectedKey;

    /// <summary>
    /// The filesystem path of the root that <see cref="SelectedKey"/> belongs to - the root's own
    /// path if a root row is selected, or the owning root's path if a session/agent row under it is
    /// selected - or null if nothing is selected or the selection has since disappeared. Root node
    /// keys are already the root's real path (see <see cref="BuildRootNode"/>), so no additional
    /// path resolution is needed.
    ///
    /// <para>Consumed by "Create session" (P2-T6/MainWindow) to default a new session's working
    /// directory to whichever root the user currently has selected in this panel, rather than
    /// making them re-pick a folder that's already visible on screen.</para>
    /// </summary>
    public string? SelectedRootPath => RootPathFor(SelectedKey);

    /// <summary>
    /// The filesystem path of the root that owns <paramref name="key"/> - the general form
    /// <see cref="SelectedRootPath"/> is built on, exposed separately so a caller acting on a specific
    /// row (e.g. P4-T4's "Resume" action, which needs a session row's cwd regardless of whether that
    /// row happens to be the panel's current tree selection) never has to first force a selection change
    /// just to reuse this resolution.
    /// </summary>
    public string? RootPathFor(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        foreach (var root in Roots)
        {
            if (root.Key == key)
            {
                return root.Key;
            }

            if (EnumerateAll(root.Children).Any(n => n.Key == key))
            {
                return root.Key;
            }
        }

        return null;
    }

    /// <summary>
    /// The first configured root that actually exists on disk, or null if there are none (no roots
    /// configured at all, or every configured root has since been deleted/renamed). Filters out the
    /// synthetic "(unattributed)" root node the same way <see cref="Directory.Exists"/> naturally
    /// would - that placeholder key is never a real path.
    ///
    /// <para>Consumed by "Create session" (P2-T6/MainWindow) as the fallback default working
    /// directory when nothing is selected in this panel (see <see cref="SelectedRootPath"/>) - never
    /// leave a new session's working directory unset, since that has <c>claude</c> inherit Accel's
    /// own process directory (its build output folder) as the child's cwd, which is neither a
    /// meaningful project root nor a folder the user has ever seen or trusted, and Claude Code's
    /// first-run trust prompt then blocks the session indefinitely until someone notices and answers
    /// it in the terminal - which is exactly what made a freshly created session look like it never
    /// started at all.</para>
    /// </summary>
    public string? FirstAvailableRootPath =>
        Roots.FirstOrDefault(r => r.Kind == RootsPanelNodeKind.Root && Directory.Exists(r.Key))?.Key;

    /// <summary>
    /// The working directory a session row should be <i>launched</i> in ("Resume session" /
    /// "Resume as fork", <c>MainWindow.ResumeSessionCore</c>) - deliberately a different question
    /// from <see cref="RootPathFor"/>'s "which row owns this one", and the only one of the two whose
    /// result is safe to hand to <c>CreateProcessW</c>'s <c>lpCurrentDirectory</c>.
    ///
    /// <para><b>Invariant: every non-null value this returns is a directory that exists on disk.</b>
    /// <see cref="RootPathFor"/> guarantees no such thing, and two ways of violating it both ended in
    /// the same hard failure - <c>CreateProcessW</c> returning Win32 error 267 (<c>ERROR_DIRECTORY</c>,
    /// "the directory name is invalid") and the resume producing no session at all:
    /// <list type="number">
    /// <item>a session under the synthetic "(unattributed)" root, whose root key is the literal label
    /// <c>"(unattributed)"</c> (<c>MonitorTreeBuilder</c>'s <c>UnattributedLabel</c>, used as both
    /// <c>Path</c> and <c>Text</c>) rather than any path at all;</item>
    /// <item>a session under a perfectly real root folder that has since been deleted, renamed, or
    /// unmounted.</item>
    /// </list></para>
    ///
    /// <para>Precedence, first hit that exists on disk wins:
    /// <list type="number">
    /// <item><paramref name="node"/>'s own <see cref="RootsPanelNodeViewModel.Cwd"/> - the directory
    /// the session actually recorded in its transcript, so resuming lands the process back where it
    /// was. Preferred over the owning root even when that root is valid: the root is only the
    /// <i>ancestor</i> the session was attributed to, and it is the only meaningful answer at all for
    /// the "(unattributed)" case;</item>
    /// <item>the owning root (<see cref="RootPathFor"/>), for an older/degraded snapshot with no
    /// recorded cwd;</item>
    /// <item><see cref="FirstAvailableRootPath"/>, matching what "Create session" already falls back
    /// to;</item>
    /// <item><c>null</c> - last resort, letting <c>claude</c> inherit Accel's own process directory.
    /// That is a genuinely bad outcome (see <see cref="FirstAvailableRootPath"/>'s remarks: an
    /// untrusted cwd makes Claude Code's first-run trust prompt block the session until someone
    /// answers it in the terminal), but it is a <i>visible, recoverable</i> bad outcome inside a tab
    /// the user can see and act on, whereas passing the invalid path through produced no tab and no
    /// session whatsoever.</item>
    /// </list></para>
    /// </summary>
    public string? ResolveWorkingDirectoryFor(RootsPanelNodeViewModel? node)
    {
        if (node is not null && ExistingDirectoryOrNull(node.Cwd) is { } sessionCwd)
        {
            return sessionCwd;
        }

        if (ExistingDirectoryOrNull(RootPathFor(node?.Key)) is { } owningRoot)
        {
            return owningRoot;
        }

        return FirstAvailableRootPath;
    }

    /// <summary>The one gate every launch path's working directory goes through - null/empty/
    /// whitespace, the "(unattributed)" placeholder, and any path that is not a directory right now
    /// all collapse to <c>null</c> so the caller falls through to its next candidate.
    /// <see cref="Directory.Exists"/> never throws (it returns <c>false</c> for a malformed path, a
    /// permission failure, or a disconnected UNC share), so no guard around it is needed.</summary>
    private static string? ExistingDirectoryOrNull(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;

    /// <summary>
    /// <see cref="ResolveWorkingDirectoryFor"/> applied to the panel's own current selection - the
    /// "Create session" dialog's pre-filled working directory (<c>MainWindow.CreateSessionCore</c>).
    /// Same existence invariant, for the same reason, one step earlier in the flow: the dialog's own
    /// <see cref="Directory.Exists"/> guard already stops an invalid path from reaching
    /// <c>CreateProcessW</c>, so this is not a second crash path - it just means a user who happens to
    /// have an "(unattributed)" row selected gets a usable folder pre-filled instead of a label they
    /// have to notice and replace before the dialog will let them confirm.
    /// </summary>
    public string? SelectedWorkingDirectory =>
        ResolveWorkingDirectoryFor(string.IsNullOrEmpty(SelectedKey) ? null : FindByKey(Roots, SelectedKey!));

    /// <summary>Number of configured roots in the last snapshot.</summary>
    [ObservableProperty]
    private int _rootCount;

    /// <summary>Total sessions across all roots (plus unattributed) in the last snapshot.</summary>
    [ObservableProperty]
    private int _sessionCount;

    /// <summary>How many of <see cref="SessionCount"/> are live.</summary>
    [ObservableProperty]
    private int _liveSessionCount;

    /// <summary>True once at least one snapshot has been rendered - lets the view distinguish
    /// "no data yet" from "data arrived and there genuinely are no sessions".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoSnapshot))]
    private bool _hasSnapshot;

    /// <summary>Inverse of <see cref="HasSnapshot"/> - lets the view swap between the plain
    /// "waiting"/"failed" caption and the icon-per-counter row purely in XAML.</summary>
    public bool NoSnapshot => !HasSnapshot;

    /// <summary>"Refreshed at h:mm tt" for the last rendered snapshot - split out from
    /// <see cref="StatusText"/> so the view can put its own icon in front of just this line.</summary>
    [ObservableProperty]
    private string _refreshedAtText = string.Empty;

    /// <summary>Just the "h:mm tt" portion of <see cref="RefreshedAtText"/> - split out so the view
    /// can bold only the time value, not the "Refreshed at" label.</summary>
    [ObservableProperty]
    private string _refreshedAtValueText = string.Empty;

    /// <summary>
    /// Raised from <see cref="Rebuild"/> whenever a session row transitions into
    /// <see cref="RootsPanelNodeViewModel.IsWaiting"/> that wasn't already in that state on the
    /// previous rebuild - <c>MainWindow</c>'s signal to flash the taskbar icon. Edge-triggered (see
    /// <see cref="_lastKnownWaitingSessionIds"/>), so it fires once per Stop event rather than every
    /// ~2s tick for as long as the row stays unacknowledged.
    /// </summary>
    public event Action? SessionWaitingForAttention;

    /// <summary>The search box's current text - filters <see cref="Roots"/> down to rows whose
    /// <see cref="RootsPanelNodeViewModel.DisplayText"/>/<see cref="RootsPanelNodeViewModel.Text"/>
    /// contains it (case-insensitive), plus any ancestor of a match (so a matching session still
    /// shows under its root). Re-applied on every <see cref="Rebuild"/> too, since telemetry ticks
    /// replace every node wholesale.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Every node this VM auto-expanded purely to reveal a search match under a
    /// previously-collapsed row - collapsed back once <see cref="SearchText"/> is cleared, so
    /// clearing a search restores the tree to how the user actually left it rather than leaving
    /// everything the search happened to touch stuck open.</summary>
    private readonly HashSet<RootsPanelNodeViewModel> _autoExpandedForSearch = new();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>The search box's reset button.</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    private void ApplyFilter()
    {
        string search = SearchText.Trim();

        if (search.Length == 0)
        {
            foreach (var node in _autoExpandedForSearch)
            {
                node.IsExpanded = false;
            }

            _autoExpandedForSearch.Clear();

            foreach (var node in EnumerateAll(Roots))
            {
                node.IsVisible = true;
            }

            return;
        }

        foreach (var root in Roots)
        {
            ApplyFilterRecursive(root, search);
        }
    }

    private bool ApplyFilterRecursive(RootsPanelNodeViewModel node, string search)
    {
        bool selfMatch = node.DisplayText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            node.Text.Contains(search, StringComparison.OrdinalIgnoreCase);

        bool anyChildVisible = false;
        foreach (var child in node.Children)
        {
            if (ApplyFilterRecursive(child, search))
            {
                anyChildVisible = true;
            }
        }

        if (anyChildVisible && !node.IsExpanded)
        {
            node.IsExpanded = true;
            _autoExpandedForSearch.Add(node);
        }

        node.IsVisible = selfMatch || anyChildVisible;
        return node.IsVisible;
    }

    /// <summary>Starts the feed (idempotent) - separate from the constructor so a host can build the
    /// whole panel graph before any telemetry starts flowing.</summary>
    public void Start() => _feed.Start();

    /// <summary>Manual refresh: goes through the feed's debounce window like any other signal, so
    /// there is still exactly one throttling mechanism.</summary>
    [RelayCommand]
    private void Refresh() => _feed.RequestRefresh();

    /// <summary>Collapses every node (a cheap user affordance that also exercises the
    /// expansion-preservation path: the collapsed state survives the next rebuild).</summary>
    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var node in EnumerateAll(Roots))
        {
            node.IsExpanded = false;
        }
    }

    /// <summary>
    /// P1-T3b: prompts for a folder, creates it on disk if it doesn't already exist, appends it
    /// to <c>accel-folders.json</c> (via <see cref="RootFolderEditor.AddRoot"/>), then triggers a
    /// refresh through the existing <see cref="ITelemetryFeed.RequestRefresh"/> path - the same
    /// single refresh mechanism <see cref="Refresh"/> uses, never a second one.
    /// </summary>
    [RelayCommand]
    private void AddRoot()
    {
        string? folder = _folderPicker.PickFolder("Select a folder to monitor");
        if (string.IsNullOrWhiteSpace(folder))
        {
            return; // user cancelled
        }

        RootFolderEditor.AddRoot(_configPath, folder);
        _feed.RequestRefresh();
    }

    /// <summary>
    /// P1-T3b: scoped to a selected root node (<paramref name="node"/> is the tree row the user
    /// right-clicked / the context menu was invoked on). Confirms with "stop monitoring" copy
    /// (never "delete" - nothing is being deleted), then dereferences the root from
    /// <c>accel-folders.json</c> ONLY. This must never touch the folder or its contents on disk -
    /// see <see cref="RootFolderEditor.RemoveRoot"/>'s doc comment for the invariant and
    /// <c>RootFolderEditorTests</c> for the test that pins it down.
    /// </summary>
    [RelayCommand]
    private void RemoveRoot(RootsPanelNodeViewModel? node)
    {
        if (node is null || node.Kind != RootsPanelNodeKind.Root || string.IsNullOrEmpty(node.Key))
        {
            return; // only meaningful for a root row
        }

        if (!_confirmation.Confirm(RootFolderEditor.StopMonitoringConfirmationText, RootFolderEditor.StopMonitoringConfirmationTitle))
        {
            return;
        }

        RootFolderEditor.RemoveRoot(_configPath, node.Key);
        _feed.RequestRefresh();
    }

    /// <summary>
    /// Persists <paramref name="displayName"/> as <paramref name="sessionId"/>'s <c>accel_override</c>
    /// name (see <see cref="RootFolderEditor.SetSessionDisplayName"/>) and refreshes, so a freshly
    /// created session's row picks up the same name its tab already shows on the next tick, rather
    /// than whatever the transcript-derived tiers of the name ladder happen to fall back to.
    /// </summary>
    public void SetSessionDisplayName(string sessionId, string displayName)
    {
        RootFolderEditor.SetSessionDisplayName(_configPath, sessionId, displayName);
        _feed.RequestRefresh();
    }

    private void OnSnapshotAvailable(RootsTreeDto snapshot) => _dispatcher.Post(() =>
    {
        if (!_disposed)
        {
            Rebuild(snapshot);
        }
    });

    /// <summary>
    /// P3-T1: the focused session changed in panel C. Re-applies <c>IsFocused</c> to every existing row
    /// in place - no rebuild, no telemetry round trip - so panel A's highlight follows a tab switch
    /// immediately. Marshalled through the dispatcher for the same reason snapshots are: the message is
    /// delivered on whichever thread wrote the selection.
    /// </summary>
    private void OnFocusedSessionChanged(FocusedSessionChangedMessage message) => _dispatcher.Post(() =>
    {
        if (!_disposed)
        {
            ApplyFocus();
        }
    });

    /// <summary>Sets <c>IsFocused</c> on every row from the selection service (all false when there is no
    /// service). Called after every rebuild and on every focus change.</summary>
    private void ApplyFocus()
    {
        foreach (var node in EnumerateAll(Roots))
        {
            bool isFocused = IsNodeFocused(node.Key);
            node.IsFocused = isFocused;

            // The TODO's acknowledgment: focusing a waiting session's tab clears its highlight
            // immediately, without waiting for the next rebuild - see WaitingSinceUtc/IsWaiting's
            // doc comments and _waitingAcknowledgedUtc's class remarks.
            if (isFocused && node.Kind == RootsPanelNodeKind.Session && node.WaitingSinceUtc is { } waitingSinceUtc)
            {
                _waitingAcknowledgedUtc[node.Key] = waitingSinceUtc;
                node.IsWaiting = false;
                _lastKnownWaitingSessionIds.Remove(node.Key);
            }
        }
    }

    /// <summary>
    /// Compares this rebuild's set of <see cref="RootsPanelNodeViewModel.IsWaiting"/> session ids
    /// against <see cref="_lastKnownWaitingSessionIds"/> and raises <see cref="SessionWaitingForAttention"/>
    /// once if any session newly entered that state - see that event's doc comment for why this is
    /// edge-triggered rather than firing on every tick a session stays unacknowledged.
    /// </summary>
    private void RaiseSessionWaitingForAttentionIfNewlyWaiting()
    {
        bool raise = false;
        var currentlyWaiting = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in EnumerateAll(Roots))
        {
            if (node.Kind != RootsPanelNodeKind.Session || !node.IsWaiting)
            {
                continue;
            }

            currentlyWaiting.Add(node.Key);
            if (!_lastKnownWaitingSessionIds.Contains(node.Key))
            {
                raise = true;
            }
        }

        _lastKnownWaitingSessionIds.Clear();
        _lastKnownWaitingSessionIds.UnionWith(currentlyWaiting);

        if (raise)
        {
            SessionWaitingForAttention?.Invoke();
        }
    }

    private bool IsNodeFocused(string key) =>
        _selection is not null && !string.IsNullOrEmpty(key) && _selection.IsFocused(key);

    private void OnSnapshotFailed(string message) => _dispatcher.Post(() =>
    {
        if (!_disposed)
        {
            StatusText = $"Refresh failed: {message}";
        }
    });

    /// <summary>
    /// The full-tree rebuild. Public so tests can drive it directly with a fixture
    /// <see cref="RootsTreeDto"/>, exactly as <c>MonitorTreeBuilderTests</c> drives the pure
    /// builder.
    /// </summary>
    public void Rebuild(RootsTreeDto? snapshot)
    {
        var tree = MonitorTreeBuilder.Build(snapshot);

        _rebuilding = true;
        try
        {
            // Capture BEFORE clearing - the old node objects are the only place the current
            // expand/selection state lives (MonitorForm.RenderTree captures in the same order).
            var expandedKeys = CaptureExpandedKeys();
            string? selectedKey = SelectedKey;

            // Every rebuild replaces every node - the old ones this set references are gone either
            // way, so there is nothing to collapse back.
            _autoExpandedForSearch.Clear();

            Roots.Clear();

            foreach (var root in tree.Roots)
            {
                Roots.Add(BuildRootNode(root));
            }

            if (tree.Unattributed is not null)
            {
                Roots.Add(BuildRootNode(tree.Unattributed));
            }

            // P3-T1: every rebuild creates fresh node instances, so the focus flag has to be re-applied
            // to them from the selection service (panel C's, the single authority) - exactly like
            // expansion and selection state below, and for the same reason.
            ApplyFocus();

            var keysToExpand = MonitorTreeExpansion.ComputeKeysToExpand(tree, expandedKeys);
            keysToExpand.UnionWith(MonitorTreeExpansion.ComputeDefaultExpansionForNewKeys(tree, _everSeenKeys));
            ApplyExpansion(Roots, keysToExpand);

            _everSeenKeys.UnionWith(MonitorTreeExpansion.CollectAllKeys(tree));

            if (!string.IsNullOrEmpty(selectedKey))
            {
                var selectedNode = FindByKey(Roots, selectedKey!);
                if (selectedNode is not null)
                {
                    selectedNode.IsSelected = true;
                }
                else
                {
                    // The previously selected node is gone from this snapshot (session filtered
                    // out, agent no longer live) - drop the selection rather than pointing at a
                    // node that no longer exists. MonitorForm's equivalent is simply not finding
                    // the key and leaving SelectedNode null.
                    SelectedKey = null;
                }
            }

            RaiseSessionWaitingForAttentionIfNewlyWaiting();

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                ApplyFilter();
            }
        }
        finally
        {
            _rebuilding = false;
        }

        UpdateCounters(snapshot);
    }

    private void UpdateCounters(RootsTreeDto? snapshot)
    {
        var rootDtos = snapshot?.Roots ?? Array.Empty<RootTreeDto>();
        var unattributed = snapshot?.UnattributedSessions ?? Array.Empty<SessionTreeDto>();

        var allSessions = rootDtos.SelectMany(r => r.Sessions ?? Array.Empty<SessionTreeDto>()).Concat(unattributed).ToArray();

        RootCount = rootDtos.Length;
        SessionCount = allSessions.Length;
        LiveSessionCount = allSessions.Count(s => s.IsLive);
        HasSnapshot = snapshot is not null;

        if (snapshot is null)
        {
            StatusText = "Waiting for telemetry…";
            return;
        }

        string refreshedAt = snapshot.GeneratedAtUtc.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture);
        RefreshedAtValueText = refreshedAt;
        RefreshedAtText = $"Refreshed at {refreshedAt}";
        StatusText = string.Create(
            CultureInfo.InvariantCulture,
            $"{RootCount} root(s), {SessionCount} session(s)\n{LiveSessionCount} running — {RefreshedAtText}");
    }

    /// <summary>Called by a node whose <c>IsSelected</c> changed (the WPF <c>TreeViewItem</c> is
    /// bound two-way to it), so <see cref="SelectedKey"/> tracks the user's selection without the
    /// ViewModel needing a reference to the <c>TreeView</c> control. Ignored during a rebuild,
    /// where selection is being restored rather than chosen.</summary>
    internal void OnNodeSelectionChanged(RootsPanelNodeViewModel node, bool isSelected)
    {
        if (_rebuilding)
        {
            if (isSelected)
            {
                SelectedKey = string.IsNullOrEmpty(node.Key) ? null : node.Key;
            }

            return;
        }

        if (isSelected)
        {
            SelectedKey = string.IsNullOrEmpty(node.Key) ? null : node.Key;
        }
        else if (SelectedKey == node.Key)
        {
            SelectedKey = null;
        }
    }

    private HashSet<string> CaptureExpandedKeys()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in EnumerateAll(Roots))
        {
            if (node.IsExpanded && !string.IsNullOrEmpty(node.Key))
            {
                result.Add(node.Key);
            }
        }

        return result;
    }

    private static void ApplyExpansion(IEnumerable<RootsPanelNodeViewModel> nodes, IReadOnlySet<string> keysToExpand)
    {
        foreach (var node in EnumerateAll(nodes))
        {
            if (!string.IsNullOrEmpty(node.Key) && keysToExpand.Contains(node.Key))
            {
                node.IsExpanded = true;
            }
        }
    }

    private static RootsPanelNodeViewModel? FindByKey(IEnumerable<RootsPanelNodeViewModel> nodes, string key) =>
        EnumerateAll(nodes).FirstOrDefault(n => n.Key == key);

    private static IEnumerable<RootsPanelNodeViewModel> EnumerateAll(IEnumerable<RootsPanelNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;

            foreach (var child in EnumerateAll(node.Children))
            {
                yield return child;
            }
        }
    }

    private RootsPanelNodeViewModel BuildRootNode(MonitorRootNode root)
    {
        var node = new RootsPanelNodeViewModel(root.Path, root.Text, RootsPanelNodeKind.Root, MonitorNodeState.Historical, root.Columns, this);

        if (root.Sessions.Length == 0 && root.OrphanAgents.Length == 0)
        {
            // Same single placeholder child MonitorForm.BuildRootTreeNode adds for an empty root -
            // deliberately keyless so it never participates in expansion/selection matching.
            string placeholder = MonitorTreeBuilder.NoSessionsPlaceholder();
            var placeholderColumns = new MonitorRowColumns(string.Empty, placeholder, string.Empty, string.Empty, string.Empty, string.Empty);
            node.Children.Add(new RootsPanelNodeViewModel(string.Empty, placeholder, RootsPanelNodeKind.Placeholder, MonitorNodeState.Historical, placeholderColumns, this));
        }
        else
        {
            foreach (var session in root.Sessions)
            {
                node.Children.Add(BuildSessionNode(session));
            }

            foreach (var agent in root.OrphanAgents)
            {
                node.Children.Add(BuildAgentNode(agent));
            }
        }

        return node;
    }

    private RootsPanelNodeViewModel BuildSessionNode(MonitorSessionNode session)
    {
        bool isWaiting = session.WaitingSinceUtc is { } waitingSinceUtc &&
            (!_waitingAcknowledgedUtc.TryGetValue(session.SessionId, out var acknowledgedUtc) || waitingSinceUtc > acknowledgedUtc);

        var node = new RootsPanelNodeViewModel(
            session.SessionId, session.Text, RootsPanelNodeKind.Session, session.State, session.Columns, this,
            projectDir: session.ProjectDir, durationMs: session.DurationMs, consumedTokens: session.ConsumedTokens,
            waitingSinceUtc: session.WaitingSinceUtc, isWaiting: isWaiting, cwd: session.Cwd);

        foreach (var agent in session.Agents)
        {
            node.Children.Add(BuildAgentNode(agent));
        }

        return node;
    }

    private RootsPanelNodeViewModel BuildAgentNode(MonitorAgentNode agent) =>
        new(agent.AgentId, agent.Text, RootsPanelNodeKind.Agent, agent.State, agent.Columns, this,
            durationMs: agent.DurationMs, consumedTokens: agent.ConsumedTokens);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _feed.SnapshotAvailable -= OnSnapshotAvailable;
        _feed.SnapshotFailed -= OnSnapshotFailed;
        _selection?.Unsubscribe(this);
    }
}

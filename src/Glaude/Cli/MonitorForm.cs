namespace Glaude.Cli;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Glaude.Server;

/// <summary>
/// The Glaude monitor window: a single, non-interactive, owner-drawn <see cref="TreeView"/>
/// showing every configured root folder, the Claude Code sessions found under it (live and
/// historical), and each live session's live sub-agents - see project-ui.md's "Rendering" section
/// for the tree shape (implemented in the pure <see cref="MonitorTreeBuilder"/>, which this class
/// only walks to create the actual <see cref="TreeNode"/>s).
///
/// The native <see cref="TreeView"/> control is kept (for its hierarchy, expand/collapse glyphs,
/// and indentation) but switched to <see cref="TreeViewDrawMode.OwnerDrawAll"/> so each row paints
/// as real aligned columns - ID | Name | Type | Model | Effort | Context - matching a header
/// strip drawn above it at the same X-offsets (see <see cref="ColumnLayout"/>).
///
/// Post-combined-app refactor: this form is constructed directly against the in-process
/// <see cref="EventServer"/> instance (no HTTP, no separate `glaude run` process) and refreshes
/// on genuine push signals rather than a polling timer - see <see cref="_debounceTimer"/>'s doc
/// comment for the important distinction between this debounce mechanism and a poll loop. Two
/// sources can indicate "something changed": <see cref="Glaude.Metrics.SessionState.Changed"/>
/// (anything that arrived via a hook/statusline POST to this process) and a
/// <see cref="FileSystemWatcher"/> rooted at the same `%USERPROFILE%\.claude\projects` directory
/// <see cref="EventServer.RootsTree"/> scans (catches historical/on-disk changes that never went
/// through this process's own hooks, e.g. another Claude Code session's transcript being written).
/// Both can fire on background threads, so both are marshalled onto the UI thread via
/// <see cref="Control.BeginInvoke(Delegate)"/> before touching the debounce state or any control.
/// </summary>
public sealed class MonitorForm : Form
{
    private readonly EventServer _server;
    private readonly FileSystemWatcher? _fileWatcher;

    // The actual one-shot timer mechanism backing the pure DebounceCoalescer below - restarted
    // on every incoming change signal, and only actually rebuilds the tree once no new signal
    // has arrived for a full Interval. This is a debounce/coalescing device, NOT a polling loop:
    // it never fires on its own schedule independent of real signals - see DebounceCoalescer's
    // doc comment for the full rationale. 250ms comfortably coalesces a burst of many hook POSTs
    // per second (heavy sub-agent activity) or a burst of file-system events into one rebuild.
    private readonly System.Windows.Forms.Timer _debounceTimer;
    private readonly DebounceCoalescer _coalescer;

    private readonly TreeView _treeView;
    private readonly Panel _headerPanel;
    private readonly Label _statusLabel;

    /// <summary>Every stable key (root path / session id / agent id) this window instance has
    /// ever rendered, across the whole process lifetime (one window per `glaude ui` invocation,
    /// so a plain instance field is enough - no cross-process persistence needed). Only ever
    /// grows; used by <see cref="MonitorTreeExpansion.ComputeDefaultExpansionForNewKeys"/> to
    /// distinguish "first time we've ever shown this node" (which may default-expand if live)
    /// from "we've shown it before" (whose expand state is governed entirely by the existing
    /// preservation logic, not re-forced open just because it's still live).</summary>
    private readonly HashSet<string> _everSeenKeys = new();

    // Column layout: header text, absolute X (from the TreeView's left edge), and width. Shared
    // between the header strip's Paint handler and DrawNode so both line up - computed by the
    // pure MonitorColumnLayout.Compute(availableWidth) (see MonitorTreeBuilder.cs) from the
    // current TreeView width, and recomputed on every resize (see RecomputeColumnLayout) rather
    // than fixed at construction time, so the six columns adapt to the window's width instead of
    // staying pinned at their original pixel offsets.
    private MonitorColumnSlot[] _columnLayout = MonitorColumnLayout.Compute(0);

    private const int RowHeight = 24;
    private const int HeaderHeight = 28;

    private static readonly Color HeaderBackColor = Color.FromArgb(230, 234, 240);
    private static readonly Color HeaderBorderColor = Color.FromArgb(190, 196, 205);
    private static readonly Color GroupRowBackColor = Color.FromArgb(219, 231, 245);
    private static readonly Color EvenRowBackColor = Color.White;
    private static readonly Color OddRowBackColor = Color.FromArgb(245, 246, 248);
    private static readonly Color SelectionBackColor = Color.FromArgb(198, 219, 245);

    public MonitorForm(EventServer server)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));

        Text = "Glaude Monitor";
        Width = 990;
        Height = 650;
        MinimumSize = new Size(650, 400);
        StartPosition = FormStartPosition.CenterScreen;

        _headerPanel = new DoubleBufferedPanel
        {
            Dock = DockStyle.Top,
            Height = HeaderHeight,
            BackColor = HeaderBackColor,
        };
        _headerPanel.Paint += OnHeaderPaint;

        _treeView = new DoubleBufferedTreeView
        {
            Dock = DockStyle.Fill,
            DrawMode = TreeViewDrawMode.OwnerDrawAll,
            ItemHeight = RowHeight,
            Indent = 16,
            BorderStyle = BorderStyle.None,
            HotTracking = false,
            ShowLines = true,
        };
        _treeView.DrawNode += OnDrawNode;
        _treeView.ClientSizeChanged += OnTreeViewClientSizeChanged;

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0),
            Text = "Connecting…",
        };

        Controls.Add(_treeView);
        Controls.Add(_headerPanel);
        Controls.Add(_statusLabel);

        RecomputeColumnLayout();

        _debounceTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _coalescer = new DebounceCoalescer(
            restartTimer: () => { _debounceTimer.Stop(); _debounceTimer.Start(); },
            stopTimer: () => _debounceTimer.Stop());
        _debounceTimer.Tick += OnDebounceTimerTick;

        // Primary push signal: anything that arrived via a hook/statusline POST to this process.
        _server.State.Changed += OnBackendChanged;

        // Secondary push signal: historical/on-disk changes that never touch this process's own
        // SessionState (e.g. another Claude Code session's transcript being written, or a brand
        // new session directory appearing) - rooted at the same directory RootsTreeBuilder scans.
        _fileWatcher = TryCreateProjectsWatcher();

        Load += OnLoad;
        FormClosing += OnFormClosing;
    }

    private FileSystemWatcher? TryCreateProjectsWatcher()
    {
        try
        {
            string projectsDir = _server.ProjectsDirOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude",
                "projects");

            if (!Directory.Exists(projectsDir))
            {
                return null; // Nothing to watch yet - SessionState.Changed still covers live activity.
            }

            var watcher = new FileSystemWatcher(projectsDir, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };

            watcher.Changed += OnProjectsDirChanged;
            watcher.Created += OnProjectsDirChanged;
            watcher.Renamed += OnProjectsDirChanged;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch
        {
            // Best-effort only - a watcher failure (permissions, race) must never prevent the
            // window from opening; SessionState.Changed still covers live hook/statusline activity.
            return null;
        }
    }

    // FileSystemWatcher events fire on a background (thread-pool) thread - marshal onto the UI
    // thread before touching the debounce timer/state, exactly as any WinForms cross-thread
    // update must.
    private void OnProjectsDirChanged(object sender, FileSystemEventArgs e) => SignalOnUiThread();

    // SessionState.Changed may fire from a hook-handling (Kestrel request) thread - same
    // marshalling requirement as the file-system watcher above.
    private void OnBackendChanged() => SignalOnUiThread();

    private void SignalOnUiThread()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(() => _coalescer.Signal()));
        }
        catch (ObjectDisposedException)
        {
            // Window closed between the IsDisposed check and BeginInvoke - ignore.
        }
        catch (InvalidOperationException)
        {
            // Handle torn down concurrently - ignore, same rationale as above.
        }
    }

    private void OnDebounceTimerTick(object? sender, EventArgs e)
    {
        if (_coalescer.Elapsed())
        {
            RefreshAndRender();
        }
    }

    private void OnLoad(object? sender, EventArgs e)
    {
        // Build immediately on open rather than waiting for the first change signal, so the
        // window isn't blank the first time it's shown.
        RefreshAndRender();
    }

    private void RefreshAndRender()
    {
        try
        {
            var dto = _server.RootsTree.Build(_server.Roots, _server.State, _server.ProjectsDirOverride);
            var tree = MonitorTreeBuilder.Build(dto);
            RenderTree(tree);

            string asOf = dto.GeneratedAtUtc.ToString("u");
            _statusLabel.Text = $"live state as of {asOf}; sessions started before this window opened are shown as historical";
        }
        catch (Exception ex)
        {
            // Never let a rebuild failure crash the UI thread - keep whatever was last rendered
            // and surface the failure in the status strip only.
            _statusLabel.Text = $"Refresh failed: {ex.Message}";
        }
    }

    // A from-scratch rebuild every ~2s (see project-ui.md's "Rendering" section) would otherwise
    // re-collapse everything the user just expanded, since TreeView has no notion of "this new
    // node is really the same session as before". So around the rebuild we capture the expanded
    // set (and selection/scroll) keyed on the stable ids each TreeNode.Tag already carries -
    // MonitorTreeExpansion (pure, no WinForms dependency) then tells us which of those keys still
    // exist in the new tree and should be re-expanded. On top of that preserved set we union in
    // any keys that default-expand because this is the first time we've ever rendered them and
    // they're live (or have a live descendant) - see MonitorTreeExpansion.
    // ComputeDefaultExpansionForNewKeys's doc comment for why that's a one-shot default rather
    // than a standing "always force open while live" rule.
    private void RenderTree(MonitorTree tree)
    {
        _treeView.BeginUpdate();
        try
        {
            var expandedKeys = CaptureExpandedKeys();
            string? selectedKey = _treeView.SelectedNode?.Tag as string;
            string? topKey = _treeView.TopNode?.Tag as string;

            _treeView.Nodes.Clear();

            foreach (var root in tree.Roots)
            {
                _treeView.Nodes.Add(BuildRootTreeNode(root));
            }

            if (tree.Unattributed is not null)
            {
                _treeView.Nodes.Add(BuildRootTreeNode(tree.Unattributed));
            }

            var keysToExpand = MonitorTreeExpansion.ComputeKeysToExpand(tree, expandedKeys);
            var defaultExpandKeys = MonitorTreeExpansion.ComputeDefaultExpansionForNewKeys(tree, _everSeenKeys);
            keysToExpand.UnionWith(defaultExpandKeys);
            ApplyExpansion(_treeView.Nodes, keysToExpand);

            _everSeenKeys.UnionWith(MonitorTreeExpansion.CollectAllKeys(tree));

            if (selectedKey is not null)
            {
                var selectedNode = FindByKey(_treeView.Nodes, selectedKey);
                if (selectedNode is not null)
                {
                    _treeView.SelectedNode = selectedNode;
                }
            }

            if (topKey is not null)
            {
                var topNode = FindByKey(_treeView.Nodes, topKey);
                if (topNode is not null)
                {
                    _treeView.TopNode = topNode;
                }
            }
        }
        finally
        {
            _treeView.EndUpdate();
        }
    }

    /// <summary>Walks the (about-to-be-rebuilt) TreeView and collects the stable keys of every
    /// currently-expanded node, keyed via <see cref="TreeNode.Tag"/> exactly as set by the
    /// Build*TreeNode helpers below.</summary>
    private HashSet<string> CaptureExpandedKeys()
    {
        var result = new HashSet<string>();
        CollectExpanded(_treeView.Nodes, result);
        return result;
    }

    private static void CollectExpanded(TreeNodeCollection nodes, HashSet<string> result)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.IsExpanded && node.Tag is string key && !string.IsNullOrEmpty(key))
            {
                result.Add(key);
            }

            if (node.Nodes.Count > 0)
            {
                CollectExpanded(node.Nodes, result);
            }
        }
    }

    private static void ApplyExpansion(TreeNodeCollection nodes, HashSet<string> keysToExpand)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Tag is string key && keysToExpand.Contains(key))
            {
                node.Expand();
            }

            if (node.Nodes.Count > 0)
            {
                ApplyExpansion(node.Nodes, keysToExpand);
            }
        }
    }

    private static TreeNode? FindByKey(TreeNodeCollection nodes, string key)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Tag is string k && k == key)
            {
                return node;
            }

            if (node.Nodes.Count > 0)
            {
                var found = FindByKey(node.Nodes, key);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static MonitorTreeNode BuildRootTreeNode(MonitorRootNode root)
    {
        var node = new MonitorTreeNode(root.Text, root.Columns, glyph: string.Empty, isGroupRow: true) { Tag = root.Path };

        if (root.Sessions.Length == 0 && root.OrphanAgents.Length == 0)
        {
            string placeholder = MonitorTreeBuilder.NoSessionsPlaceholder();
            var placeholderColumns = new MonitorRowColumns(string.Empty, placeholder, string.Empty, string.Empty, string.Empty, string.Empty);
            node.Nodes.Add(new MonitorTreeNode(placeholder, placeholderColumns, glyph: string.Empty, isGroupRow: false));
        }
        else
        {
            foreach (var session in root.Sessions)
            {
                node.Nodes.Add(BuildSessionTreeNode(session));
            }

            foreach (var agent in root.OrphanAgents)
            {
                node.Nodes.Add(BuildAgentTreeNode(agent));
            }
        }

        return node;
    }

    private static MonitorTreeNode BuildSessionTreeNode(MonitorSessionNode session)
    {
        string glyph = MonitorTreeBuilder.GlyphFor(session.State);
        var node = new MonitorTreeNode(session.Text, session.Columns, glyph, isGroupRow: false) { Tag = session.SessionId };
        ApplyStyle(node, session.State);

        foreach (var agent in session.Agents)
        {
            node.Nodes.Add(BuildAgentTreeNode(agent));
        }

        return node;
    }

    private static MonitorTreeNode BuildAgentTreeNode(MonitorAgentNode agent)
    {
        string glyph = MonitorTreeBuilder.GlyphFor(agent.State);
        var node = new MonitorTreeNode(agent.Text, agent.Columns, glyph, isGroupRow: false) { Tag = agent.AgentId };
        ApplyStyle(node, agent.State);
        return node;
    }

    // Colour is never the only signal here - each row's own Glyph (●/?/none, see
    // MonitorTreeBuilder.GlyphFor) is drawn as its own leading element by OnDrawNode; this only
    // adds the bold/muted weight difference on top of that.
    private static void ApplyStyle(MonitorTreeNode node, MonitorNodeState state)
    {
        switch (state)
        {
            case MonitorNodeState.Live:
                node.NodeFont = new Font(Control.DefaultFont, FontStyle.Bold);
                break;
            case MonitorNodeState.Stale:
            case MonitorNodeState.Historical:
                node.ForeColor = SystemColors.GrayText;
                break;
        }
    }

    // Fired whenever the TreeView's client area changes size - which, since it is Dock.Fill,
    // tracks the containing Form's own resizes. Recomputes the shared column layout from the new
    // width and repaints both the header strip and the rows immediately (rather than waiting for
    // the next ~2s refresh tick) so a resize is visibly responsive.
    private void OnTreeViewClientSizeChanged(object? sender, EventArgs e)
    {
        RecomputeColumnLayout();
    }

    private void RecomputeColumnLayout()
    {
        _columnLayout = MonitorColumnLayout.Compute(_treeView.ClientSize.Width);
        _headerPanel.Invalidate();
        _treeView.Invalidate();
    }

    private void OnHeaderPaint(object? sender, PaintEventArgs e)
    {
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using var headerFont = new Font(Font, FontStyle.Bold);

        foreach (var (header, x, width) in _columnLayout)
        {
            if (string.IsNullOrEmpty(header))
            {
                continue;
            }

            var bounds = new Rectangle(x, 0, width, _headerPanel.Height);
            TextRenderer.DrawText(e.Graphics, header, headerFont, bounds, SystemColors.ControlText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }

        using var borderPen = new Pen(HeaderBorderColor);
        e.Graphics.DrawLine(borderPen, 0, _headerPanel.Height - 1, _headerPanel.Width, _headerPanel.Height - 1);
    }

    private void OnDrawNode(object? sender, DrawTreeNodeEventArgs e)
    {
        var graphics = e.Graphics;
        TreeNode node = e.Node!;
        var monitorNode = node as MonitorTreeNode;
        var columns = monitorNode?.Columns ?? MonitorRowColumns.Empty;
        string glyph = monitorNode?.Glyph ?? string.Empty;
        bool isGroupRow = monitorNode?.IsGroupRow ?? false;

        int rowWidth = Math.Max(0, _treeView.ClientSize.Width - e.Bounds.X);
        var rowBounds = new Rectangle(e.Bounds.X, e.Bounds.Top, rowWidth, RowHeight);

        bool isSelected = (e.State & TreeNodeStates.Selected) != 0;
        Color backColor;
        if (isSelected)
        {
            backColor = SelectionBackColor;
        }
        else if (isGroupRow)
        {
            backColor = GroupRowBackColor;
        }
        else
        {
            int rowIndex = RowHeight > 0 ? e.Bounds.Top / RowHeight : 0;
            backColor = rowIndex % 2 == 0 ? EvenRowBackColor : OddRowBackColor;
        }

        using (var backBrush = new SolidBrush(backColor))
        {
            graphics.FillRectangle(backBrush, rowBounds);
        }

        Font baseFont = _treeView.Font!;
        Font? nodeFont = node.NodeFont;
        Font font = nodeFont ?? (isGroupRow ? new Font(baseFont, FontStyle.Bold) : baseFont);
        Color foreColor = node.ForeColor != Color.Empty ? node.ForeColor : _treeView.ForeColor;

        var textFlags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

        foreach (var (header, x, width) in _columnLayout)
        {
            int absoluteX = e.Bounds.X + x;
            var cellBounds = new Rectangle(absoluteX, e.Bounds.Top, width, RowHeight);

            string cellText = header switch
            {
                "" => glyph, // the leading state-glyph slot
                "ID" => columns.Id,
                "Name" => columns.Name,
                "Type" => columns.Type,
                "Model" => columns.Model,
                "Effort" => columns.Effort,
                "Context" => columns.Context,
                _ => string.Empty,
            };

            if (string.IsNullOrEmpty(cellText))
            {
                continue;
            }

            TextRenderer.DrawText(graphics, cellText, font, cellBounds, foreColor, textFlags);
        }

        if ((e.State & TreeNodeStates.Focused) != 0)
        {
            e.DrawDefault = false;
        }
    }

    /// <summary>A <see cref="TreeNode"/> carrying the extra per-row data <see cref="OnDrawNode"/>
    /// needs but a plain <see cref="TreeNode"/> has no slot for: the six-column data, the leading
    /// state glyph, and whether this is a group row (root/unattributed) that gets the distinct
    /// filled background. <see cref="TreeNode.Tag"/> is deliberately left free for the stable key
    /// the expand-state-preservation logic (<see cref="MonitorTreeExpansion"/>) already relies on.</summary>
    private sealed class MonitorTreeNode : TreeNode
    {
        public MonitorRowColumns Columns { get; }

        public string Glyph { get; }

        public bool IsGroupRow { get; }

        public MonitorTreeNode(string text, MonitorRowColumns columns, string glyph, bool isGroupRow)
            : base(text)
        {
            Columns = columns;
            Glyph = glyph;
            IsGroupRow = isGroupRow;
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _server.State.Changed -= OnBackendChanged;

        if (_fileWatcher is not null)
        {
            _fileWatcher.EnableRaisingEvents = false;
            _fileWatcher.Changed -= OnProjectsDirChanged;
            _fileWatcher.Created -= OnProjectsDirChanged;
            _fileWatcher.Renamed -= OnProjectsDirChanged;
            _fileWatcher.Dispose();
        }

        _debounceTimer.Stop();
        _debounceTimer.Tick -= OnDebounceTimerTick;
        _debounceTimer.Dispose();
        _treeView.ClientSizeChanged -= OnTreeViewClientSizeChanged;
    }

    /// <summary>A plain <see cref="TreeView"/>, owner-drawn every ~2s on a full <c>Nodes.Clear()</c>
    /// + rebuild (see <see cref="RenderTree"/>), visibly flickers on Windows without help: the
    /// managed <see cref="Control.DoubleBuffered"/> property (protected on <see cref="Control"/>,
    /// so only reachable from a subclass) plus the native <c>TVS_EX_DOUBLEBUFFER</c> extended
    /// style are the two well-established fixes for this, applied together here (belt-and-suspenders
    /// - either alone is usually enough, but the native style needs a live window handle, so it is
    /// applied in <see cref="OnHandleCreated"/> rather than the constructor).</summary>
    private sealed class DoubleBufferedTreeView : TreeView
    {
        private const int TvFirst = 0x1100;
        private const int TvmSetExtendedStyle = TvFirst + 44;
        private const int TvsExDoubleBuffer = 0x0004;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public DoubleBufferedTreeView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SendMessage(Handle, TvmSetExtendedStyle, (IntPtr)TvsExDoubleBuffer, (IntPtr)TvsExDoubleBuffer);
        }
    }

    /// <summary>The header strip is a plain <see cref="Panel"/> whose <see cref="Panel.Paint"/>
    /// handler (<see cref="OnHeaderPaint"/>) does nothing but <c>DrawString</c>/<c>DrawLine</c>
    /// calls, so it rarely flickers on its own - but it repaints every time
    /// <see cref="RecomputeColumnLayout"/> invalidates it (every resize) and every time the
    /// TreeView above it repaints, so it gets the same double-buffering treatment for
    /// consistency and to rule it out as a flicker source entirely.</summary>
    private sealed class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }
    }
}

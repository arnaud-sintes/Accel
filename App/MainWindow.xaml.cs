namespace Accel.App;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Rectangle = System.Windows.Shapes.Rectangle;
using ICSharpCode.AvalonEdit.Document;
using Accel.App.Services;
using Accel.App.ViewModels;
using Accel.Cli;
using Accel.Orchestration;
using Accel.Server;

/// <summary>
/// The WPF shell's main window. Layout is still P1-T1b's scaffolding (menu bar +
/// GridSplitter-separated placeholder panels B/C/D/E); P1-T2 gives panel A a real
/// <see cref="RootsPanelViewModel"/> and binds its <c>TreeView</c> to it.
///
/// <para>Composition is deliberately constructor-injected and minimal - no DI container: the window
/// takes the panel ViewModel(s) it hosts and assigns them as the corresponding panel's
/// <c>DataContext</c>. See <c>Program.cs</c>'s <c>RunCombinedAsync</c> for the composition point
/// that builds the graph (feed + dispatcher + ViewModel) on the real `accel` startup path.</para>
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Scaffolding/designer constructor - panel A renders as an empty tree.</summary>
    public MainWindow()
        : this(null)
    {
    }

    public MainWindow(RootsPanelViewModel? rootsPanel)
        : this(rootsPanel, null, 0)
    {
    }

    /// <summary>
    /// P2-T5b's overload, kept so nothing that predates P3-T1 has to change: no tab strip and no session
    /// registry means the "Create session" menu item can still launch and attach a session, it just has
    /// no tab (see <see cref="CreateSession_Click"/>).
    /// </summary>
    public MainWindow(RootsPanelViewModel? rootsPanel, PtyRouteRegistry? ptyRouteRegistry, int ptyWebSocketPort)
        : this(rootsPanel, ptyRouteRegistry, ptyWebSocketPort, null, null)
    {
    }

    /// <summary>
    /// P3-T1's real composition: <paramref name="tabs"/> is panel C's tab strip (the only writer of
    /// <c>ISessionSelectionService</c>) and <paramref name="sessionRegistry"/> is the app-lifetime
    /// <see cref="PtyRegistry"/> that owns every live session's lifetime. Together they replace P2-T6's
    /// stopgap <c>List&lt;(tabId, session)&gt;</c> ownership bridge, which disposed sessions itself and is
    /// gone from this file entirely.
    ///
    /// <para><paramref name="ptyRouteRegistry"/>/<paramref name="ptyWebSocketPort"/> stay as P2-T5b left
    /// them: the <c>tabId -&gt; IPtyEndpoint</c> map behind the <c>/pty/{tabId}</c> WebSocket route (a
    /// different registry from <see cref="PtyRegistry"/> - that one owns process lifetime, this one owns
    /// route reachability), plus the loopback port its Kestrel host is bound to. Every parameter is
    /// optional and null degrades gracefully, so the designer/scaffolding paths still work.</para>
    /// </summary>
    public MainWindow(
        RootsPanelViewModel? rootsPanel,
        PtyRouteRegistry? ptyRouteRegistry,
        int ptyWebSocketPort,
        TabsViewModel? tabs,
        PtyRegistry? sessionRegistry)
        : this(rootsPanel, ptyRouteRegistry, ptyWebSocketPort, tabs, sessionRegistry, null)
    {
    }

    /// <summary>
    /// P3-T3: <paramref name="selection"/> feeds panel B's stub <see cref="FocusedSessionStubViewModel"/>
    /// reader - the same read-only <see cref="ISessionSelectionService"/> panel A already consumes for
    /// <c>IsFocused</c>. Null degrades exactly like every other optional parameter here: the panel keeps its
    /// P1-T1b placeholder text with no status line beneath it. Panel E is wired by the
    /// <see cref="AgentGraphViewModel"/> overload below; this overload leaves it unset (null degrades to
    /// no DataContext, same as every other optional parameter).
    /// </summary>
    public MainWindow(
        RootsPanelViewModel? rootsPanel,
        PtyRouteRegistry? ptyRouteRegistry,
        int ptyWebSocketPort,
        TabsViewModel? tabs,
        PtyRegistry? sessionRegistry,
        ISessionSelectionService? selection)
        : this(rootsPanel, ptyRouteRegistry, ptyWebSocketPort, tabs, sessionRegistry, selection, null)
    {
    }

    /// <summary>
    /// Phase 6's real composition: <paramref name="agentGraph"/> is panel E's
    /// <see cref="AgentGraphViewModel"/> - a second <see cref="Accel.App.Services.ITelemetryFeed"/> reader
    /// and a second read-only <see cref="ISessionSelectionService"/> reader, fed the same
    /// feed/dispatcher/selection triple as <paramref name="rootsPanel"/> (design doc
    /// "claude-agentgraph.md" §7.7). Null degrades exactly like every other optional parameter: panel E
    /// keeps no DataContext, same as the pure-scaffolding paths above.
    /// </summary>
    public MainWindow(
        RootsPanelViewModel? rootsPanel,
        PtyRouteRegistry? ptyRouteRegistry,
        int ptyWebSocketPort,
        TabsViewModel? tabs,
        PtyRegistry? sessionRegistry,
        ISessionSelectionService? selection,
        AgentGraphViewModel? agentGraph)
        : this(rootsPanel, ptyRouteRegistry, ptyWebSocketPort, tabs, sessionRegistry, selection, agentGraph, null)
    {
    }

    /// <summary>
    /// Phase 5's real composition: <paramref name="filesPanel"/> is panel B's
    /// <see cref="FilesPanelViewModel"/>, replacing the P3-T3 <see cref="FocusedSessionStubViewModel"/>
    /// this overload's predecessor still assigns when <paramref name="filesPanel"/> is null (so nothing
    /// that predates Phase 5 has to change). Null degrades exactly like every other optional parameter:
    /// panel B falls back to the stub if <paramref name="selection"/> is given, or to no DataContext at
    /// all otherwise.
    /// </summary>
    public MainWindow(
        RootsPanelViewModel? rootsPanel,
        PtyRouteRegistry? ptyRouteRegistry,
        int ptyWebSocketPort,
        TabsViewModel? tabs,
        PtyRegistry? sessionRegistry,
        ISessionSelectionService? selection,
        AgentGraphViewModel? agentGraph,
        FilesPanelViewModel? filesPanel)
        : this(rootsPanel, ptyRouteRegistry, ptyWebSocketPort, tabs, sessionRegistry, selection, agentGraph, filesPanel, null)
    {
    }

    /// <summary>
    /// Phase 7's real composition: <paramref name="gitPanel"/> is panel B's bottom section's
    /// <see cref="GitPanelViewModel"/>, independent of <paramref name="filesPanel"/> (its own
    /// DataContext, on <c>GitSectionRoot</c> rather than <c>FilesSectionRoot</c> - see
    /// <c>MainWindow.xaml</c>'s remarks on panel B's split). Null degrades like every other optional
    /// parameter: the git section keeps no DataContext at all.
    /// </summary>
    public MainWindow(
        RootsPanelViewModel? rootsPanel,
        PtyRouteRegistry? ptyRouteRegistry,
        int ptyWebSocketPort,
        TabsViewModel? tabs,
        PtyRegistry? sessionRegistry,
        ISessionSelectionService? selection,
        AgentGraphViewModel? agentGraph,
        FilesPanelViewModel? filesPanel,
        GitPanelViewModel? gitPanel)
        : this(rootsPanel, ptyRouteRegistry, ptyWebSocketPort, tabs, sessionRegistry, selection, agentGraph, filesPanel, gitPanel, null)
    {
    }

    /// <summary>
    /// Panel A's MCP/SKILLS section: <paramref name="mcpSkillsPanel"/> is one
    /// <see cref="McpSkillsPanelViewModel"/> assigned as the DataContext of <i>both</i>
    /// <c>McpSectionRoot</c> and <c>SkillsSectionRoot</c> - one focused-session lookup feeding two
    /// collections, never a filtered view of <paramref name="rootsPanel"/>'s tree (same rule panel B's
    /// two sections follow). Null degrades like every other optional parameter: both mini-panels keep
    /// no DataContext and render their empty placeholders.
    /// </summary>
    public MainWindow(
        RootsPanelViewModel? rootsPanel,
        PtyRouteRegistry? ptyRouteRegistry,
        int ptyWebSocketPort,
        TabsViewModel? tabs,
        PtyRegistry? sessionRegistry,
        ISessionSelectionService? selection,
        AgentGraphViewModel? agentGraph,
        FilesPanelViewModel? filesPanel,
        GitPanelViewModel? gitPanel,
        McpSkillsPanelViewModel? mcpSkillsPanel)
    {
        InitializeComponent();

        // T8: the shutdown guard - see OnClosingAsync's remarks. Wired unconditionally (not just
        // inside the `tabs is not null` block below): a null Tabs just means the sweep finds nothing
        // dirty and lets the close proceed, same as every other Tabs-optional hook in this file.
        Closing += MainWindow_Closing;

        // The diff viewer's line-number gutters and its two panes' scroll sync are wired here rather
        // than in XAML since ScrollViewer.ScrollChangedEvent has no attached-property XAML shorthand -
        // see DiffOldText_ScrollChanged/DiffNewText_ScrollChanged's remarks. The single-pane file view
        // needs no equivalent: FileEditor is an AvalonEdit TextEditor whose gutter is part of its own
        // TextView, so it scrolls with the text by construction.
        DiffOldText.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(DiffOldText_ScrollChanged), true);
        DiffNewText.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(DiffNewText_ScrollChanged), true);

        // DiffNewEditor is a second AvalonEdit TextEditor (the diff view's editable "After" pane, see
        // ShowGitDiffTabAsync's remarks) - it needs the same scroll-sync handler as DiffNewText,
        // since exactly one of the two is ever visible for a given diff tab.
        DiffNewEditor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(DiffNewText_ScrollChanged), true);

        // Theme.xaml brushes for the two things AvalonEdit exposes only behind read-only TextArea/
        // TextView properties, which XAML therefore cannot reach (everything else FileEditor needs is
        // a real dependency property and is bound in MainWindow.xaml). Frozen brushes come straight
        // out of the resource dictionary, so this shares them rather than allocating new ones.
        FileEditor.TextArea.SelectionBrush = (Brush)FindResource("SelectionBrush");
        FileEditor.TextArea.SelectionBorder = null;
        FileEditor.TextArea.TextView.CurrentLineBackground = (Brush)FindResource("SurfaceElevatedBrush");
        FileEditor.TextArea.TextView.CurrentLineBorder = new Pen((Brush)FindResource("StrokeSubtleBrush"), 1);

        // Syntax colouring for the file editor comes from the same SyntaxHighlighter.Tokenize the diff
        // viewer uses (see SyntaxColorizer's remarks for why this is a line transformer over a cached
        // token map rather than an .xshd definition). The colorizer cannot redraw itself - it does not
        // own the TextView - so the rebuild notification is turned into a redraw here.
        _fileSyntaxColorizer = new SyntaxColorizer(new DispatcherDebounceTimer(Dispatcher, SyntaxColorizer.RebuildDebounce));
        _fileSyntaxColorizer.CacheRebuilt += () => FileEditor.TextArea.TextView.Redraw();
        FileEditor.TextArea.TextView.LineTransformers.Add(_fileSyntaxColorizer);

        // DiffNewEditor is a second, independent AvalonEdit surface (the diff view's editable "After"
        // pane), so it needs its own SyntaxColorizer instance - the file editor's is already bound to
        // FileEditor's TextView and cannot serve two controls at once. Same theming/wiring as above.
        DiffNewEditor.TextArea.SelectionBrush = (Brush)FindResource("SelectionBrush");
        DiffNewEditor.TextArea.SelectionBorder = null;
        DiffNewEditor.TextArea.TextView.CurrentLineBackground = (Brush)FindResource("SurfaceElevatedBrush");
        DiffNewEditor.TextArea.TextView.CurrentLineBorder = new Pen((Brush)FindResource("StrokeSubtleBrush"), 1);

        _diffSyntaxColorizer = new SyntaxColorizer(new DispatcherDebounceTimer(Dispatcher, SyntaxColorizer.RebuildDebounce));
        _diffSyntaxColorizer.CacheRebuilt += () => DiffNewEditor.TextArea.TextView.Redraw();
        DiffNewEditor.TextArea.TextView.LineTransformers.Add(_diffSyntaxColorizer);

        // Paints the "After" pane's added-line background over DiffNewEditor's live document - the
        // AvalonEdit equivalent of BuildHighlightedDocument's per-paragraph background for the
        // read-only DiffNewText pane. Its highlighted-line set is refreshed by ShowGitDiffTabAsync.
        _diffAddedLineHighlighter = new DiffLineHighlighter();
        DiffNewEditor.TextArea.TextView.LineTransformers.Add(_diffAddedLineHighlighter);

        RootsPanel = rootsPanel;
        Tabs = tabs;
        _ptyRouteRegistry = ptyRouteRegistry;
        _ptyWebSocketPort = ptyWebSocketPort;
        _sessionRegistry = sessionRegistry;

        if (rootsPanel is not null)
        {
            // Scoped to panel A only - deliberately not Window.DataContext, so the remaining
            // placeholder panels can't accidentally start binding against panel A's ViewModel
            // (locked-in decision 8: no point-to-point panel bindings).
            PanelA.DataContext = rootsPanel;
        }

        if (tabs is not null)
        {
            PanelC.DataContext = tabs;

            // Panel D hosts exactly ONE TerminalView, reattached per selected tab (see TabsViewModel's
            // class remarks for why one-and-reattach beats one-control-per-tab). This is the only place
            // that knows both the control and the port, so the attach hook is wired here rather than
            // making the ViewModel aware of WPF.
            tabs.AttachTerminalAsync = tabId => Terminal.AttachPtyAsync(tabId, _ptyWebSocketPort);

            // Closing the last open tab (no neighbour to fall back to) selects null - without this,
            // panel D kept rendering the closed session's last frame forever (TabsViewModel's
            // DetachTerminalAsync remarks).
            tabs.DetachTerminalAsync = () => Terminal.DetachPtyAsync();

            // Panel D's file-viewer hooks (a file/git-change tab's TabId is the file's own full path - see
            // TabViewModel.ForFile/ForGitChange/ForGitDiff). The pty stays attached underneath rather
            // than being detached and reattached on every flip between a file/git tab and a session
            // tab - only FileViewerHost's/DiffViewerHost's Visibility toggles (see PanelD's own XAML
            // comment for why).
            tabs.ShowFileAsync = ShowFileTabAsync;
            tabs.HideFileViewer = ShowTerminalPane;

            // T6: Save/Discard - see SaveFileTabAsync/DiscardFileTabChangesAsync's own remarks.
            tabs.SaveFileAsync = SaveFileTabAsync;
            tabs.DiscardFileAsync = DiscardFileTabChangesAsync;

            // A closed tab's edit buffer (unsaved text + undo history) must not outlive it - see
            // _fileEditBuffers and TabsViewModel.ReleaseFileTab.
            tabs.ReleaseFileTab = EvictFileEditBuffer;

            // T8: the close guard - see ConfirmCloseDirtyTabAsync's own remarks.
            tabs.ConfirmCloseDirtyTabAsync = ConfirmCloseDirtyTabAsync;
        }

        if (filesPanel is not null)
        {
            // Scoped to panel B's file-tree section only, never Window.DataContext or all of
            // PanelB, per the rule quoted above - GitSectionRoot below gets its own, independent
            // DataContext.
            FilesPanelVm = filesPanel;
            FilesSectionRoot.DataContext = filesPanel;

            // A file/folder the FILES panel just deleted, or moved/renamed away from, must not leave
            // a tab pointing at a path that no longer exists there - see
            // OnFilesPanelEntryRemovedOrMoved's remarks for why this closes the tab rather than trying
            // to rebind it.
            filesPanel.EntryRemovedOrMoved += OnFilesPanelEntryRemovedOrMoved;
        }
        else if (selection is not null)
        {
            // Pre-Phase-5 callers (no filesPanel argument) still get the P3-T3 stub.
            _panelBStub = new FocusedSessionStubViewModel(selection);
            FilesSectionRoot.DataContext = _panelBStub;
        }

        if (gitPanel is not null)
        {
            // Scoped to panel B's git section only - see the comment above.
            GitPanelVm = gitPanel;
            GitSectionRoot.DataContext = gitPanel;
        }

        if (mcpSkillsPanel is not null)
        {
            // One ViewModel, two binding roots: the MCP and SKILLS halves of panel A's bottom third
            // read two collections off the same focused-session lookup, so splitting them across two
            // ViewModels would just duplicate that lookup.
            McpSkillsPanelVm = mcpSkillsPanel;
            McpSectionRoot.DataContext = mcpSkillsPanel;
            SkillsSectionRoot.DataContext = mcpSkillsPanel;
        }

        if (agentGraph is not null)
        {
            // Scoped to panel E only, never Window.DataContext, per the rule quoted above.
            AgentGraphVm = agentGraph;
            PanelE.DataContext = agentGraph;
        }

        if (rootsPanel is not null)
        {
            // TODO (window-flash-on-waiting): flash the taskbar icon whenever a session goes
            // "waiting for feedback" (a Stop hook event) while this window doesn't have focus -
            // the row highlight itself (IsWaiting) is panel A's own concern; this is the
            // out-of-band signal for when the user isn't even looking at Accel.
            rootsPanel.SessionWaitingForAttention += OnSessionWaitingForAttention;
        }

        Closed += (_, _) =>
        {
            // No session teardown here any more: PtyRegistry is the single owner of PtySession.Dispose
            // (P3-T2) and app-exit teardown is P3-T4's job (CloseAllAsync/Dispose around the app loop).
            // This only drops the tab strip's registry subscription plus panel B's own; AgentGraphViewModel
            // is disposed by Program.cs's composition root (mirrors how rootsPanel is never disposed here).
            Tabs?.Dispose();
            _panelBStub?.Dispose();
            if (rootsPanel is not null)
            {
                rootsPanel.SessionWaitingForAttention -= OnSessionWaitingForAttention;
            }
        };

        // Custom-chrome maximize fix. Two earlier revisions of this fix were both wrong in the
        // same direction - they compensated in WPF LAYOUT space (a ChromeRoot margin of
        // SystemParameters.WindowResizeBorderThickness while maximized) for an error that lives in
        // WIN32 WINDOW space. WindowChrome's own WM_NCCALCSIZE handling already makes the client
        // area equal the ENTIRE window rect for this WindowStyle="None" window, so once the
        // WM_GETMINMAXINFO hook targets the maximize at the monitor's work area, WPF content
        // already fills that rect edge-to-edge; adding a content margin on top of that just
        // insets the content INSIDE a correctly-sized window - guaranteed visible gaps on every
        // edge (the reported bug). It was also the wrong constant: the real hidden-frame overhang
        // Windows applies to a WS_THICKFRAME window is SM_CXSIZEFRAME + SM_CXPADDEDBORDER, which
        // WindowResizeBorderThickness does not include.
        //
        // The correct mechanism is entirely native-side, in WindowProc:
        // 1. WM_GETMINMAXINFO -> maximize targets the work area, not the full monitor (the
        //    default for a borderless window covers the taskbar).
        // 2. WM_NCCALCSIZE while zoomed -> clamp the proposed client rect to the work area, so
        //    any hidden resize-frame overhang Windows still applies to the maximized window rect
        //    is measured and removed exactly, with no guessed constants (the standard
        //    borderless-window technique used by Chromium / Windows Terminal).
        SourceInitialized += WindowSourceInitialized;

        // Belt-and-suspenders for the stale ~8px contour on focus change: the WM_NCCALCSIZE
        // WVR_REDRAW fix above stopped this hook itself from ever answering a maximized
        // WM_NCCALCSIZE call with "preserve the old bitmap" (return 0), but WM_NCCALCSIZE's
        // WVR_REDRAW is still only a REQUEST that Windows invalidate the client area - it is not a
        // synchronous, guaranteed-immediate repaint, and this hook must now own every zoomed
        // WM_NCCALCSIZE call itself (see ClampMaximizedClientRectToWorkArea's remarks on why
        // delegating a "nothing to correct" case back to WindowChrome regressed the maximize-fit
        // fix), which means WindowChrome's own activation-driven invalidation logic - whatever else
        // it normally does beyond returning WVR_REDRAW - no longer runs for this window at all while
        // maximized. Forcing an explicit, synchronous full-window repaint on every activation change
        // closes that gap without needing to reverse-engineer exactly what WindowChrome's own
        // handler was doing differently.
        Activated += (_, _) => ForceFullWindowRedraw();
        Deactivated += (_, _) => ForceFullWindowRedraw();
    }

    private IntPtr _windowHandle;

    /// <summary>
    /// <see cref="RootsPanelViewModel.SessionWaitingForAttention"/>'s handler: flashes the taskbar
    /// icon (see <see cref="FlashTaskbarIcon"/>) unless this window is already the foreground
    /// window - flashing a window the user is already looking at would just be noise. Raised on
    /// the UI thread already (the ViewModel's own snapshot handling is dispatcher-marshalled), so
    /// this can touch <see cref="IntPtr"/>/native state directly.
    /// </summary>
    private void OnSessionWaitingForAttention() => FlashTaskbarIcon();

    /// <summary>
    /// Flashes this window's taskbar button until the user activates it (<c>FLASHW_TIMERNOFG</c>),
    /// per the TODO's "make Accel app window flash" requirement. No-ops before the native <c>HWND</c>
    /// exists (<see cref="_windowHandle"/> unset) or while the window already has focus - Windows
    /// itself is the authority on when to stop flashing once the user does switch to it.
    /// </summary>
    private void FlashTaskbarIcon()
    {
        if (_windowHandle == IntPtr.Zero || IsActive)
        {
            return;
        }

        var info = new NativeMethods.FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.FLASHWINFO>(),
            hwnd = _windowHandle,
            dwFlags = NativeMethods.FLASHW_TRAY | NativeMethods.FLASHW_TIMERNOFG,
            uCount = uint.MaxValue,
            dwTimeout = 0,
        };

        NativeMethods.FlashWindowEx(ref info);
    }

    /// <summary>Synchronously repaints the entire window (RDW_UPDATENOW) and every child
    /// (RDW_ALLCHILDREN) - a hard guarantee against any stale/leftover bitmap surviving an
    /// activation change, regardless of the precise Win32 message-level cause.
    /// Deliberately NOT RDW_FRAME (nor RDW_ERASE): this WindowStyle="None" window's client rect
    /// already covers the entire window (WindowChrome's / this hook's WM_NCCALCSIZE handling), so
    /// invalidating the client area reaches every visible pixel - while RDW_FRAME forces a
    /// synchronous WM_NCPAINT that DefWindowProc answers by painting the classic grey Win32 frame
    /// for the window's WS_THICKFRAME style over the edge pixels, visible as a grey border flash
    /// on every focus change until WPF's next render pass paints back over it.</summary>
    private void ForceFullWindowRedraw()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        const uint RDW_INVALIDATE = 0x0001;
        const uint RDW_ALLCHILDREN = 0x0080;
        const uint RDW_UPDATENOW = 0x0100;

        NativeMethods.RedrawWindow(
            _windowHandle,
            IntPtr.Zero,
            IntPtr.Zero,
            RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
    }

    /// <summary>
    /// Two independent native-window fixes, both needed only once the Win32 <c>HWND</c> exists:
    /// <list type="number">
    /// <item>The <see cref="WindowProc"/> hook (<c>WM_GETMINMAXINFO</c> + maximized
    /// <c>WM_NCCALCSIZE</c>) so maximizing this borderless (<c>WindowStyle="None"</c>) window fills
    /// exactly the current monitor's <b>work area</b> (excludes the taskbar) - see the
    /// constructor's comment for why both messages are needed and why no WPF-side margin is. The
    /// same hook's <c>WM_NCCALCSIZE</c> return value is also what fixes the stale ~8px contour on
    /// focus change - see <see cref="ClampMaximizedClientRectToWorkArea"/>.</item>
    /// <item><see cref="SuppressDwmWindowBorder"/> - cosmetic dark-mode/no-border opt-ins for the
    /// frame pixels DWM draws outside any WPF content (nothing in <c>Theme.xaml</c> can reach
    /// them).</item>
    /// </list>
    /// </summary>
    private void WindowSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;

        if (HwndSource.FromHwnd(_windowHandle) is { } source)
        {
            source.AddHook(WindowProc);
        }

        SuppressDwmWindowBorder(_windowHandle);
    }

    /// <summary>
    /// This hook is added AFTER WindowChrome's own (WindowChrome subscribes to
    /// <c>SourceInitialized</c> when the attached property is applied during
    /// <c>InitializeComponent</c>, before this window's constructor subscribes
    /// <see cref="WindowSourceInitialized"/>), and <see cref="HwndSource"/> invokes hooks in
    /// REVERSE order of addition, stopping once one sets <c>handled</c> - so for the two messages
    /// below this hook runs first and takes full ownership, and for everything else WindowChrome's
    /// handling (caption drag, resize-border hit-testing, etc.) is untouched. WPF's own
    /// <c>WindowChromeWorker</c> does not handle <c>WM_GETMINMAXINFO</c> at all, so nothing is
    /// being overridden there.
    ///
    /// <para>Deliberately absent: a <c>WM_NCACTIVATE</c> handler. That is the usual first guess for
    /// "native frame artifacts on activate/deactivate" in a custom-chrome Win32 app, but WPF's own
    /// <c>WindowChromeWorker</c> already registers one that calls
    /// <c>DefWindowProc(hwnd, WM_NCACTIVATE, wParam, (IntPtr)(-1))</c> - the documented trick that
    /// stops Windows repainting the caption/frame - and marks the message handled. Duplicating it
    /// here would change nothing.</para>
    ///
    /// <para><b>The return value matters as much as <c>handled</c>.</b> Because this hook marks
    /// <c>WM_NCCALCSIZE</c> handled while zoomed, WindowChrome's own <c>_HandleNCCalcSize</c> never
    /// runs in that state - so whatever this hook returns IS the message's result. See
    /// <see cref="ClampMaximizedClientRectToWorkArea"/> for why returning 0 there was the actual
    /// cause of the stale ~8px contour and why <c>WVR_REDRAW</c> is the correct answer.</para>
    /// </summary>
    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        const int WM_NCCALCSIZE = 0x0083;
        const int WM_NCPAINT = 0x0085;
        const int WM_NCUAHDRAWCAPTION = 0x00AE;
        const int WM_NCUAHDRAWFRAME = 0x00AF;

        switch (msg)
        {
            case WM_GETMINMAXINFO:
                handled = ConstrainMaximizedSizeToWorkArea(hwnd, lParam);
                return IntPtr.Zero;
            case WM_NCCALCSIZE:
                return ClampMaximizedClientRectToWorkArea(hwnd, wParam, lParam, ref handled);
            case WM_NCPAINT:
                // Swallowed entirely (the Chromium / Windows Terminal borderless-window move):
                // letting this reach DefWindowProc paints the CLASSIC grey Win32 frame for the
                // window's WS_THICKFRAME style over the outer edge pixels - the grey border flash
                // seen on every focus change, because WindowChrome re-asserts frame state on
                // activation via SetWindowPos(SWP_FRAMECHANGED), and SWP_FRAMECHANGED is the same
                // bit as SWP_DRAWFRAME ("draws a frame around the window"), so each activation
                // change carries a WM_NCPAINT with it. Nothing legitimate is lost: WM_NCCALCSIZE
                // handling (WindowChrome's, and this hook's while zoomed) makes the client rect
                // cover the entire window, so there is no non-client surface for this message to
                // paint - only DefWindowProc's style-driven frame drawing, which ignores that.
                handled = true;
                return IntPtr.Zero;
            case WM_NCUAHDRAWCAPTION:
            case WM_NCUAHDRAWFRAME:
                // Undocumented "UAH" (User32 Appearance Handler) messages Windows sends to themed
                // windows on activation change; DefWindowProc answers them by drawing the CLASSIC
                // caption/frame directly - NOT via WM_NCPAINT, which is why suppressing WM_NCPAINT
                // above does not cover this path. Swallowing both is the conventional custom-chrome
                // companion to the WM_NCPAINT suppression (same rationale: the client rect covers
                // the whole window, so there is no legitimate non-client surface to draw).
                handled = true;
                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    /// <summary>Resolves the monitor this window is (mostly) on and its geometry, in physical
    /// pixels - the coordinate space every native rect below lives in. False (callers no-op,
    /// leaving Windows' defaults in place) if the monitor can't be resolved.</summary>
    private static bool TryGetMonitorInfo(IntPtr hwnd, out NativeMethods.MONITORINFO monitorInfo)
    {
        monitorInfo = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref monitorInfo);
    }

    /// <summary>Rewrites the in-place <c>MINMAXINFO</c> Windows is about to apply so a maximize
    /// targets <see cref="NativeMethods.MONITORINFO.rcWork"/> (the monitor's work area) instead of
    /// <see cref="NativeMethods.MONITORINFO.rcMonitor"/> (its full bounds, taskbar included) -
    /// standard fix for a <c>WindowStyle="None"</c> window's maximize otherwise covering the
    /// taskbar. No-ops (leaves Windows' own defaults in place) if the monitor can't be resolved.</summary>
    private static bool ConstrainMaximizedSizeToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        if (!TryGetMonitorInfo(hwnd, out var monitorInfo))
        {
            return false;
        }

        var workArea = monitorInfo.rcWork;
        var monitorArea = monitorInfo.rcMonitor;

        var mmi = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = Math.Abs(workArea.Left - monitorArea.Left);
        mmi.ptMaxPosition.Y = Math.Abs(workArea.Top - monitorArea.Top);
        mmi.ptMaxSize.X = Math.Abs(workArea.Right - workArea.Left);
        mmi.ptMaxSize.Y = Math.Abs(workArea.Bottom - workArea.Top);
        mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
        mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;
        Marshal.StructureToPtr(mmi, lParam, true);
        return true;
    }

    /// <summary>
    /// While the window is zoomed (maximized), clamps <c>WM_NCCALCSIZE</c>'s proposed client rect
    /// to the monitor's work area. For both <c>wParam</c> shapes the first field at
    /// <paramref name="lParam"/> is the proposed window rect (screen coordinates) that becomes the
    /// client rect, so a single <see cref="NativeMethods.RECT"/> read/write covers both. This is
    /// the piece the old ChromeRoot-margin fix approximated with a guessed constant: even with
    /// <c>WM_GETMINMAXINFO</c> answered, Windows can still hand a maximized <c>WS_THICKFRAME</c>
    /// window a rect inflated by the hidden resize frame (<c>SM_CXSIZEFRAME + SM_CXPADDEDBORDER</c>
    /// per edge, hanging off-screen and over the taskbar); intersecting with the work area measures
    /// and removes exactly that overhang - the established borderless-window technique (Chromium,
    /// Windows Terminal). Not handled (WindowChrome's default client-rect handling runs instead)
    /// when not zoomed, when the monitor can't be resolved, or if the intersection is degenerate.
    ///
    /// <para><b>Why this method returns <c>WVR_REDRAW</c>, and why returning 0 was a real bug.</b>
    /// Per <c>WM_NCCALCSIZE</c>'s documentation, "if wParam is TRUE and an application returns zero,
    /// the old client area is preserved and is aligned with the upper-left corner of the new client
    /// area" - i.e. Windows blits the PREVIOUS client bitmap into the new client rect and only
    /// invalidates the sliver that blit did not cover. When this hook shrinks the proposed rect by
    /// the hidden resize frame (<c>SM_CXSIZEFRAME + SM_CXPADDEDBORDER</c>, which is 4 + 4 = 8 physical
    /// pixels per edge at 100% DPI on Windows 11), the preserved-and-top-left-aligned old bitmap
    /// leaves exactly an ~8px ring of stale pixels around the window that nothing ever repaints.
    /// It only became visible when something forced a frame recalculation on an already-maximized
    /// window - which is what an activation change does - and it was cleared piecemeal, wherever a
    /// later WPF render pass happened to cover it (hovering a caption button repaints that button's
    /// neighbourhood and "fixes" the ring just there). That is the reported symptom exactly.
    /// WPF's own <c>WindowChromeWorker._HandleNCCalcSize</c> carries the matching comment - "Returning
    /// 0 when wParam == TRUE is not appropriate - it will preserve the old client area and align it
    /// with the upper-left corner of the new client area. So we simply ask for a redraw (WVR_REDRAW)"
    /// - and this hook, by marking the message handled while zoomed, is what stopped that handler
    /// (and therefore that correct return value) from running in the maximized state. Returning
    /// <c>WVR_REDRAW</c> here restores it: Windows invalidates the whole client area, so WPF repaints
    /// every pixel of the new rect and no stale ring can survive. <c>wParam == FALSE</c> must still
    /// return 0, per the same documentation.</para>
    ///
    /// <para><b>Why this method now owns EVERY zoomed <c>WM_NCCALCSIZE</c> call, never delegating a
    /// "no correction needed" case back to WindowChrome's own handler.</b> An earlier revision left
    /// the message unhandled when <c>clamped == rect</c> already, on the theory that WindowChrome's
    /// default handling would produce an identical result (it also returns <c>WVR_REDRAW</c>) at no
    /// cost. That reintroduced the "window smaller than the work area, desktop visible around the
    /// edges" bug this whole hook exists to prevent: WindowChrome's own <c>_HandleNCCalcSize</c> has
    /// no idea this window's maximize target is the work area rather than the full monitor, and on
    /// whichever call it was allowed to run, it recomputed the client rect from its own (monitor-
    /// bounds-based) understanding, undoing the clamp. Always answering it ourselves while zoomed -
    /// writing the rect back only when it actually changed, but returning <c>WVR_REDRAW</c>/handled
    /// either way - keeps WindowChrome from ever re-deriving a rect for this state at all.</para>
    /// </summary>
    private static IntPtr ClampMaximizedClientRectToWorkArea(IntPtr hwnd, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WVR_HREDRAW | WVR_VREDRAW - "causes the entire window to be redrawn".
        const int WVR_REDRAW = 0x0300;

        if (!NativeMethods.IsZoomed(hwnd) || !TryGetMonitorInfo(hwnd, out var monitorInfo))
        {
            return IntPtr.Zero;
        }

        var workArea = monitorInfo.rcWork;
        var rect = Marshal.PtrToStructure<NativeMethods.RECT>(lParam);
        var clamped = rect;
        clamped.Left = Math.Max(rect.Left, workArea.Left);
        clamped.Top = Math.Max(rect.Top, workArea.Top);
        clamped.Right = Math.Min(rect.Right, workArea.Right);
        clamped.Bottom = Math.Min(rect.Bottom, workArea.Bottom);

        if (clamped.Right <= clamped.Left || clamped.Bottom <= clamped.Top)
        {
            return IntPtr.Zero;
        }

        if (clamped.Left != rect.Left || clamped.Top != rect.Top ||
            clamped.Right != rect.Right || clamped.Bottom != rect.Bottom)
        {
            Marshal.StructureToPtr(clamped, lParam, true);
        }

        handled = true;
        return wParam != IntPtr.Zero ? new IntPtr(WVR_REDRAW) : IntPtr.Zero;
    }

    /// <summary>
    /// Two purely cosmetic DWM opt-ins for this window's frame-adjacent pixels (shadow, snap-layout
    /// affordances, the 1px frame border DWM draws outside every scrap of WPF content -
    /// <c>DWMWA_VISIBLE_FRAME_BORDER_THICKNESS</c> reports 1px on Windows 11):
    /// <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c> keeps them dark-themed, and <c>DWMWA_BORDER_COLOR</c> is
    /// pinned to solid black rather than left at the activation-dependent system colour DWM would
    /// otherwise pick (light in most Windows themes) - that system colour is exactly what reads as a
    /// white flash around this dark window on every focus change, in or out of the app.
    ///
    /// <para><b>This is not the fix for the (unrelated) "contour on focus loss" bug.</b> Three
    /// successive revisions of this method chased THAT bug (an ~8px maximize-state artifact) through
    /// this same attribute (near-black border colour, then reapplying it on
    /// <c>Activated</c>/<c>Deactivated</c>, then <c>DWMWA_COLOR_NONE</c>) and all three were confirmed
    /// by testing to change nothing there - the real cause of that bug was the <c>WM_NCCALCSIZE</c>
    /// return value, see <see cref="ClampMaximizedClientRectToWorkArea"/>. Pinning the border colour
    /// here is a different, narrower fix for a different symptom (the focus-change flash reported
    /// separately), applied once at <c>SourceInitialized</c> - a genuinely static colour needs no
    /// re-assertion on <c>Activated</c>/<c>Deactivated</c>, unlike the activation-dependent system
    /// colour it replaces. The old <c>WM_DWMCOMPOSITIONCHANGED</c> re-assertion is gone: DWM
    /// composition cannot be turned off from Windows 8 onwards, so that message is never sent and the
    /// hook case was dead code.</para>
    ///
    /// <para>Both attributes are Windows 11 (build 22000+) additions and
    /// <c>DwmSetWindowAttribute</c> fails harmlessly with <c>E_INVALIDARG</c> on older Windows (the
    /// documented support floor, README.md), which is why the HRESULT is ignored rather than
    /// checked.</para>
    /// </summary>
    private static void SuppressDwmWindowBorder(IntPtr handle)
    {
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_BORDER_COLOR = 34;

        // COLORREF format 0x00BBGGRR - solid black, not a sentinel (DWMWA_COLOR_NONE/_DEFAULT are
        // 0xFFFFFFFE/0xFFFFFFFF respectively; this is neither).
        const int DWMWA_BORDER_COLOR_BLACK = 0x00000000;

        int darkMode = 1;
        NativeMethods.DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

        int borderColor = DWMWA_BORDER_COLOR_BLACK;
        NativeMethods.DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        /// <summary>Flash the taskbar button only (not the window's own caption/frame - this
        /// window is borderless/custom-chrome, so <c>FLASHW_CAPTION</c> would have nothing to
        /// flash).</summary>
        public const uint FLASHW_TRAY = 0x00000002;

        /// <summary>Keep flashing until the window is brought to the foreground, rather than a
        /// fixed <c>uCount</c> of flashes.</summary>
        public const uint FLASHW_TIMERNOFG = 0x0000000C;

        public const int MONITOR_DEFAULTTONEAREST = 0x00000002;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsZoomed(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
    }

    private readonly FocusedSessionStubViewModel? _panelBStub;

    /// <summary>Panel A's ViewModel, or null when the window was constructed as bare scaffolding.</summary>
    public RootsPanelViewModel? RootsPanel { get; }

    /// <summary>Panel E's ViewModel, or null when the window was constructed without one (every
    /// overload but the seven-parameter one above). Exposed so a smoke test can assert against it
    /// directly, the same way <see cref="RootsPanel"/> is.</summary>
    public AgentGraphViewModel? AgentGraphVm { get; }

    /// <summary>Panel B's ViewModel (Phase 5), or null when the window was constructed without one -
    /// every overload but the eight-parameter one above. Exposed so a smoke test can assert against it
    /// directly, the same way <see cref="RootsPanel"/>/<see cref="AgentGraphVm"/> are.</summary>
    public FilesPanelViewModel? FilesPanelVm { get; }

    /// <summary>Panel B's git status section ViewModel (Phase 7), or null when the window was
    /// constructed without one - every overload but the nine-parameter one above. Exposed so a smoke
    /// test can assert against it directly, the same way <see cref="FilesPanelVm"/> is.</summary>
    public GitPanelViewModel? GitPanelVm { get; }

    /// <summary>Panel A's MCP/SKILLS section ViewModel, or null when the window was constructed
    /// without one - every overload but the ten-parameter one above. Exposed so a smoke test can
    /// assert against it directly, the same way <see cref="GitPanelVm"/> is.</summary>
    public McpSkillsPanelViewModel? McpSkillsPanelVm { get; }

    /// <summary>Panel C's ViewModel (the tab strip), or null in the scaffolding paths.</summary>
    public TabsViewModel? Tabs { get; }

    /// <summary>
    /// The <c>tabId -&gt; IPtyEndpoint</c> registry backing whichever <c>EventServer</c> instance's
    /// Kestrel host is actually listening (null means "no terminal wiring available", e.g. the
    /// pure-scaffolding construction path).
    /// </summary>
    private readonly PtyRouteRegistry? _ptyRouteRegistry;

    /// <summary>The port that registry's owning Kestrel instance is bound to.</summary>
    private readonly int _ptyWebSocketPort;

    /// <summary>
    /// P3-T2's registry: the app-lifetime <c>tabId -&gt; PtySession</c> map and the only thing allowed to
    /// dispose a session. Null in the scaffolding paths, in which case a created session is registered
    /// for the route only and left for the job object to reap.
    /// </summary>
    private readonly PtyRegistry? _sessionRegistry;

    /// <summary>
    /// Per-session extra CLI args set via "Edit launch args…", applied the next time that session is
    /// resumed - see <see cref="SessionResumeArgsStore"/>'s own doc for why this cannot instead change
    /// an already-running `claude` process's argv. App-lifetime, same as <see cref="_sessionRegistry"/>.
    /// </summary>
    private readonly SessionResumeArgsStore _resumeArgsStore = new();

    /// <summary>
    /// The only remaining entry point into "Create session" now that the top menu bar is gone: a
    /// root row's context menu (right-click a folder in panel A) - see MainWindow.xaml's comment on
    /// that menu item. <c>Tag</c> carries the row's <see cref="RootsPanelNodeViewModel"/> (same
    /// convention as <see cref="RenameSession_Click"/> etc.); anything other than a root row is a
    /// silent no-op. The right-clicked root's own path is always used as the new session's working
    /// directory - deliberately not panel A's current *selection*, since right-clicking a row does
    /// not select it, and a menu item should act on the row the user is pointing at, not whatever
    /// happened to be selected before.
    /// </summary>
    private void CreateSessionAtRoot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not RootsPanelNodeViewModel node || node.Kind != RootsPanelNodeKind.Root)
        {
            return;
        }

        CreateSessionCore(node.Key);
    }

    /// <summary>
    /// P2-T6 + P3-T1: opens the "Create session" dialog modally. On confirm, the dialog's ViewModel has
    /// already generated the session GUID, built the argv array, resolved/validated the launch spec, and
    /// started a live <see cref="PtySession"/> (see <see cref="CreateSessionDialogViewModel.Confirm"/>).
    /// This method now gives that session a real owner and a real tab:
    /// <list type="number">
    /// <item><b>tabId = the session GUID.</b> Deliberately <see cref="CreateSessionDialogViewModel.LastGeneratedSessionId"/>
    /// rendered with <c>ToString()</c> - the same "D" (dashed) form the dialog passed to
    /// <c>--session-id</c> - and not a second, unrelated <c>Guid.NewGuid()</c> as P2-T5b's stopgap used.
    /// That equality is load-bearing rather than cosmetic: it is what lets panel A (whose session rows are
    /// keyed by the transcript's session id, dashed) light up as focused when panel C selects a tab, and
    /// it keeps the registry, the <c>/pty/{tabId}</c> route and `claude`'s own id one value instead of
    /// three. The dashed form is required for the panel-A match; the id is still an unguessable GUID, so
    /// the route's security posture is unchanged.</item>
    /// <item><b>Registered with <see cref="PtyRegistry"/> first</b>, which takes ownership of disposal
    /// (nothing in this file disposes a session any more), then with the route registry so the WebSocket
    /// can reach it.</item>
    /// <item><b>A tab is added to panel C</b>, which selects it, which writes the focused session id and
    /// reattaches panel D's terminal to this session - so the attach happens through the ordinary
    /// selection path, not a special create-time one.</item>
    /// </list>
    /// </summary>
    private void CreateSessionCore(string? initialWorkingDirectory)
    {
        // Falls back to the first configured root that still exists on disk if the caller didn't
        // have one (defensive only - CreateSessionAtRoot_Click always passes the right-clicked
        // root's own path). Deliberately never left null/blank here: an unset working directory has
        // claude inherit Accel's own process directory (its build output folder) as the child's
        // cwd - not a real project, and not something the user has ever trusted - so Claude Code's
        // first-run trust prompt blocks the session until someone notices and answers it inside the
        // terminal, which made a freshly created session look like it had simply never started
        // (reported bug: "no opened session visible in panel A"). The dialog's own working-directory
        // field is still fully editable/browsable before confirm.
        // SelectedWorkingDirectory, not SelectedRootPath: the latter happily returns the synthetic
        // "(unattributed)" root's label or a root folder that no longer exists, neither of which is a
        // directory anyone can start a session in (see RootsPanelViewModel.ResolveWorkingDirectoryFor).
        initialWorkingDirectory ??= RootsPanel?.SelectedWorkingDirectory;
        var viewModel = new CreateSessionDialogViewModel(initialWorkingDirectory: initialWorkingDirectory);
        var dialog = new CreateSessionDialog(viewModel) { Owner = this };
        dialog.ShowDialog();

        if (!dialog.Confirmed || viewModel.LastStartedSession is not { } session)
        {
            return;
        }

        // The GUID the dialog already generated for --session-id IS the tabId (see this method's doc).
        string tabId = (viewModel.LastGeneratedSessionId ?? Guid.NewGuid()).ToString();

        try
        {
            _sessionRegistry?.Register(tabId, session);
        }
        catch (Exception)
        {
            // Register only throws for a duplicate tabId (impossible for a fresh GUID) or a disposed
            // registry (the app is shutting down - it has already started closing this session, see
            // Register's own contract). Either way there is nothing useful to add to the UI here, and
            // the session must not be disposed from this file.
            return;
        }

        _ptyRouteRegistry?.RegisterSession(tabId, session);

        // Selecting the new tab is what attaches panel D (TabsViewModel.AttachTerminalAsync, wired in
        // the constructor). Nothing is awaited: AddTab is synchronous and the attach is fire-and-forget
        // by design, exactly as P2-T5b's own call site was.
        Tabs?.AddTab(tabId, string.IsNullOrWhiteSpace(viewModel.DisplayName) ? null : viewModel.DisplayName);

        // Panel A's row name comes from RootsTreeBuilder.BuildSessionDto's own tiered ladder
        // (accel_override > live /rename > transcript ai-title > first user message > truncated id),
        // none of which know about the dialog's chosen name - without this, the new tab and its
        // panel A row would show two different names until a live /rename happened to align them.
        if (!string.IsNullOrWhiteSpace(viewModel.DisplayName))
        {
            RootsPanel?.SetSessionDisplayName(tabId, viewModel.DisplayName);
        }
    }

    /// <summary>
    /// Panel A's root-folder "Open terminal here" context menu item (right-click a folder in panel A) -
    /// see MainWindow.xaml's comment on that menu item, right beside "Create session…". <c>Tag</c>
    /// carries the row's <see cref="RootsPanelNodeViewModel"/> (same convention as
    /// <see cref="CreateSessionAtRoot_Click"/>); anything other than a root row is a silent no-op. Opens
    /// a plain, unmanaged shell rooted at the right-clicked folder - entirely unrelated to Claude Code
    /// (no <c>--session-id</c>, no transcript, never a panel A row of its own).
    /// </summary>
    private void OpenTerminalAtRoot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not RootsPanelNodeViewModel node || node.Kind != RootsPanelNodeKind.Root)
        {
            return;
        }

        CreateShellSessionCore(node.Key);
    }

    /// <summary>
    /// The <see cref="TabKind.Shell"/> counterpart to <see cref="CreateSessionCore"/>: launches a plain
    /// shell (<see cref="PtySession.CreateShellSpec"/>) at <paramref name="workingDirectory"/> and gives
    /// it a tab. No dialog first - unlike "Create session", there is nothing to configure (no
    /// <c>--session-id</c>, no display name, no extra CLI args) - and no `claude`-specific launch-failure
    /// surfaces apply (<see cref="Accel.Orchestration.ClaudeCliLocator"/> resolution, the shim guard,
    /// etc. are all about `claude`, not <c>cmd.exe</c>). A launch failure (vanishingly unlikely for an
    /// in-box shell, but not impossible - a locked-down machine, a corrupted <c>ComSpec</c>) shows a
    /// message dialog rather than crashing the UI thread; everything past that point mirrors
    /// <see cref="CreateSessionCore"/>'s registration/tab steps exactly.
    /// </summary>
    private void CreateShellSessionCore(string workingDirectory)
    {
        PtySession session;
        try
        {
            session = PtySession.Start(PtySession.CreateShellSpec(workingDirectory));
        }
        catch (PtySessionLaunchException ex)
        {
            AccelMessageDialog.ShowMessage(
                this,
                $"Could not open a terminal here:\n{ex.Message}",
                "Open terminal",
                AccelDialogIcon.Warning);
            return;
        }

        string tabId = Guid.NewGuid().ToString();

        try
        {
            _sessionRegistry?.Register(tabId, session);
        }
        catch (Exception)
        {
            // Same posture as CreateSessionCore: Register only throws for a duplicate tabId (impossible
            // for a fresh GUID) or a disposed registry (the app is shutting down). Either way there is
            // nothing useful to add to the UI here, and the session must not be disposed from this file.
            return;
        }

        _ptyRouteRegistry?.RegisterSession(tabId, session);

        // Selecting the new tab is what attaches panel D, exactly like CreateSessionCore's own AddTab
        // call - Shell and Session tabs go through the identical attach path (TabViewModel.HasPtySession).
        Tabs?.AddShellTab(tabId, $"Terminal - {Path.GetFileName(workingDirectory.TrimEnd('\\', '/'))}");
    }

    /// <summary>
    /// Panel A's root-folder "Reveal in File Explorer" context menu item (right-click a folder in
    /// panel A). <c>Tag</c> carries the row's <see cref="RootsPanelNodeViewModel"/> (same convention as
    /// <see cref="CreateSessionAtRoot_Click"/>/<see cref="OpenTerminalAtRoot_Click"/>); anything other
    /// than a root row is a silent no-op. Plain OS-level convenience - no session, no tab, no
    /// <see cref="PtySession"/> involved at all.
    ///
    /// <para><c>explorer.exe &lt;folder&gt;</c> (no <c>/select,</c>) opens that folder's own contents in
    /// a new window, which is what "reveal <b>this</b> folder" means for a root row - unlike revealing
    /// a single file, there is no parent folder to select it inside. Passed via
    /// <see cref="ProcessStartInfo.ArgumentList"/> (never a hand-built argument string) so a path
    /// containing spaces or quotes cannot be misparsed or break out of the intended single argument -
    /// same discipline <see cref="PtyLaunchSpec"/> requires for a child process's own argv.</para>
    /// </summary>
    private void RevealRootInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not RootsPanelNodeViewModel node || node.Kind != RootsPanelNodeKind.Root)
        {
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            startInfo.ArgumentList.Add(node.Key);
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            // explorer.exe missing/unlaunchable is not something a user of this app can fix from
            // here - surfacing the message is strictly better than a silent no-op, but there is
            // nothing more actionable to offer.
            AccelMessageDialog.ShowMessage(
                this,
                $"Could not open File Explorer for this folder:\n{ex.Message}",
                "Reveal in File Explorer",
                AccelDialogIcon.Warning);
        }
    }

    /// <summary>
    /// P4-T2: renames a live session via <see cref="SlashCommandDriver"/> - the first real consumer of
    /// P4-T1's generic mechanism. <c>Tag</c> on the clicked <see cref="MenuItem"/> carries the row's
    /// <see cref="RootsPanelNodeViewModel"/> (see MainWindow.xaml's context-menu comment); anything other
    /// than a session row is a silent no-op, the same guard-clause convention
    /// <c>RootsPanelViewModel.RemoveRootCommand</c> already uses for a mismatched row kind.
    ///
    /// <para><b>The gate fails closed, twice, before anything is written:</b> (1) the row's key (a session
    /// GUID, which doubles as its tabId - see this file's <see cref="CreateSession_Click"/> remarks) must
    /// resolve to a session this Accel instance actually has open (<see cref="PtyRegistry.TryGet"/>) -
    /// rename can only ever act on a live tab, since that is the only way to reach the session's stdin;
    /// (2) <c>~/.claude/sessions/&lt;pid&gt;.json</c>'s <c>status</c> must read exactly
    /// <see cref="ClaudeSessionStatusFile.StatusIdle"/> - unknown/missing/busy all refuse injection rather
    /// than guess. Only once both hold does the dialog even open.</para>
    ///
    /// <para>On confirm, <see cref="SlashCommandDriver.InvokeAsync(PtySession, string, System.Collections.Generic.IReadOnlyList{string}?, System.Func{ClaudeSessionStatusSnapshot?, bool}, TimeSpan, System.Threading.CancellationToken)"/>
    /// writes <c>/rename &lt;name&gt;</c> and polls the same status file for its <c>name</c> field to
    /// actually match. A <see cref="SlashCommandOutcome.TimedOut"/> result surfaces the plan's own
    /// specified copy ("rename may not have applied") as a non-modal banner rather than a blocking
    /// MessageBox - the command was still sent, so a dialog implying failure would be actively
    /// misleading.</para>
    /// </summary>
    private async void RenameSession_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not RootsPanelNodeViewModel node || node.Kind != RootsPanelNodeKind.Session)
        {
            return;
        }

        if (_sessionRegistry is null || !_sessionRegistry.TryGet(node.Key, out var session) || session is null)
        {
            AccelMessageDialog.ShowMessage(
                this,
                "This session isn't open in a tab right now, so it can't be renamed. Open it first.",
                "Rename session",
                AccelDialogIcon.Info);
            return;
        }

        var status = ClaudeSessionStatusFile.TryRead(session.ProcessId);
        if (!ClaudeSessionStatusFile.IsIdle(status))
        {
            AccelMessageDialog.ShowMessage(
                this,
                "The session is busy right now. Wait for it to go idle before renaming.",
                "Rename session",
                AccelDialogIcon.Info);
            return;
        }

        var dialogViewModel = new RenameSessionDialogViewModel(node.DisplayText);
        var dialog = new RenameSessionDialog(dialogViewModel) { Owner = this };
        dialog.ShowDialog();

        if (!dialog.Confirmed || dialogViewModel.ConfirmedName is not { } newName)
        {
            return;
        }

        var driver = new SlashCommandDriver();
        var result = await driver.InvokeAsync(
            session,
            "/rename",
            new[] { newName },
            snapshot => string.Equals(snapshot?.Name, newName, StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        if (result.Outcome == SlashCommandOutcome.TimedOut)
        {
            ShowTransientWarning($"Rename to \"{newName}\" may not have applied - please check the session.");
            return;
        }

        // node.Key is the session id, which equals the tab's TabId (see CreateSession_Click's
        // remarks on why that equality matters) - the tab strip has no other way to learn about a
        // rename that went straight into the session's stdin, so it must be told directly here.
        Tabs?.RenameTab(node.Key, newName);

        // /rename only ever lands in the live status-line snapshot - RootsTreeBuilder stops
        // trusting that the moment the session ends (RootsTreeRoute.PersistLiveRenames captures it
        // durably on the next telemetry tick too, but writing it through immediately here means the
        // name survives even if the tab is closed before that tick lands).
        RootsPanel?.SetSessionDisplayName(node.Key, newName);
    }

    /// <summary>
    /// Opens the "Edit launch args…" popup for a session row and, on confirm, records the result in
    /// <see cref="_resumeArgsStore"/> so the <i>next</i> <c>claude --resume</c> for this session id
    /// (plain or fork - see <see cref="ResumeSessionCore"/>) includes them. Available regardless of
    /// whether the session is currently open in a tab: unlike rename (which writes into a live
    /// session's stdin), this only ever affects a future resume launch, so there is nothing here that
    /// requires the session to be running right now.
    /// </summary>
    private void EditSessionArgs_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not RootsPanelNodeViewModel node || node.Kind != RootsPanelNodeKind.Session)
        {
            return;
        }

        string sessionId = node.Key;
        var dialogViewModel = new EditSessionArgsDialogViewModel(_resumeArgsStore.Get(sessionId));
        var dialog = new EditSessionArgsDialog(dialogViewModel) { Owner = this };
        dialog.ShowDialog();

        if (dialog.Confirmed && dialogViewModel.ConfirmedArguments is { } confirmedArguments)
        {
            _resumeArgsStore.Set(sessionId, confirmedArguments);
        }
    }

    /// <summary>
    /// P4-T4: resumes a session in place - <c>claude --resume &lt;id&gt;</c>, reusing P2-T6's exact launch
    /// path (<see cref="PtySession.CreateClaudeSpec"/> then <see cref="PtySession.Start"/>). The tabId is
    /// the session's own id (<paramref name="sender"/>'s <see cref="MenuItem.Tag"/> carries the row's
    /// <see cref="RootsPanelNodeViewModel"/>, whose <see cref="RootsPanelNodeViewModel.Key"/> already is
    /// that dashed GUID - see <see cref="CreateSession_Click"/>'s remarks on why that equality matters):
    /// resuming the same session must never produce a second, differently-keyed tab for it, so panel A's
    /// row and the tab strip agree on identity exactly the way a freshly created session's do.
    /// </summary>
    private void ResumeSession_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is RootsPanelNodeViewModel node)
        {
            ResumeSessionCore(node, fork: false);
        }
    }

    /// <summary>
    /// P4-T4's fork variant - <c>claude --resume &lt;id&gt; --fork-session</c>. Claude Code itself chooses
    /// the forked copy's session id (there is no flag to pass one in alongside <c>--resume</c>), so unlike
    /// the plain resume above, this tab's id <b>cannot</b> be made to equal the eventual forked
    /// transcript's session id - a fresh GUID is used as the tabId instead, purely so the tab strip and
    /// <see cref="PtyRegistry"/> have something to key on immediately. The practical consequence: panel
    /// A's row for the new, forked transcript (once the disk scan picks it up) will not visually light up
    /// as "focused" while this tab is selected, the way every other tab in this app does - a real,
    /// documented gap rather than a silently wrong equality, and the one piece of this task the plan's own
    /// wording ("decide tab identity... so the registry and panel A don't show duplicates") leaves
    /// genuinely unresolved without Claude Code offering a way to assign the forked session's id.
    /// </summary>
    private void ResumeSessionAsFork_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is RootsPanelNodeViewModel node)
        {
            ResumeSessionCore(node, fork: true);
        }
    }

    /// <summary>
    /// Also the target of the sleeping-session double-click gesture (<see cref="SessionRow_MouseDoubleClick"/>),
    /// which calls this with <c>fork: false</c> - "attach" for a session with no running process just is
    /// a plain resume, reusing the exact same "already open? select instead" guard below.
    /// </summary>
    private void ResumeSessionCore(RootsPanelNodeViewModel node, bool fork)
    {
        if (node.Kind != RootsPanelNodeKind.Session)
        {
            return;
        }

        string sessionId = node.Key;

        // Already open: select the existing tab rather than launching a second `claude --resume` against
        // the same session id (which claude itself would likely refuse, but there is no reason to even
        // attempt it) - AddTab's own idempotency (TabsViewModel.AddTab) would do the same for a plain
        // resume, but a fork always wants a fresh tab, so this guard is the one place that actually needs
        // to distinguish the two rather than relying on that idempotency.
        if (!fork && _sessionRegistry is not null && _sessionRegistry.TryGet(sessionId, out var existing) && existing is not null)
        {
            Tabs?.SelectTab(sessionId);
            return;
        }

        // Must go through ResolveWorkingDirectoryFor, never RootPathFor directly: this value becomes
        // CreateProcessW's lpCurrentDirectory (PtySession.CreateClaudeSpec -> ConPty.LaunchChild),
        // which hard-fails with Win32 267 (ERROR_DIRECTORY) on anything that isn't an existing
        // directory - and the owning root's key is the literal string "(unattributed)" for a session
        // under the synthetic root, or a stale path for a root folder that has since been deleted.
        // See that method's remarks for the full precedence chain and why the session's own recorded
        // cwd is preferred over its owning root.
        string? workingDirectory = RootsPanel?.ResolveWorkingDirectoryFor(node);

        var baseArguments = fork
            ? new[] { "--resume", sessionId, "--fork-session" }
            : new[] { "--resume", sessionId };

        // Extra args saved via "Edit launch args..." (App/Services/SessionResumeArgsStore.cs) - the
        // one way this app lets a user change a session's launch flags after the fact, since a
        // running `claude` process's own argv cannot be changed (see PtySession's class doc).
        var extraArguments = _resumeArgsStore.Get(sessionId);
        string[] arguments;
        if (extraArguments.Length > 0)
        {
            arguments = new string[baseArguments.Length + extraArguments.Length];
            baseArguments.CopyTo(arguments, 0);
            extraArguments.CopyTo(arguments, baseArguments.Length);
        }
        else
        {
            arguments = baseArguments;
        }

        PtySession session;
        try
        {
            var spec = PtySession.CreateClaudeSpec(arguments, workingDirectory);
            session = PtySession.Start(spec);
        }
        catch (Exception ex)
        {
            AccelMessageDialog.ShowMessage(this, $"Could not resume this session: {ex.Message}", "Resume session",
                AccelDialogIcon.Error);
            return;
        }

        string tabId = fork ? Guid.NewGuid().ToString() : sessionId;

        try
        {
            _sessionRegistry?.Register(tabId, session);
        }
        catch (Exception)
        {
            // Same rationale as CreateSession_Click: Register only throws for a duplicate tabId (not
            // possible for the ids constructed above within one run) or a disposed registry (app is
            // shutting down) - nothing useful to add to the UI, and the session must not be disposed here.
            return;
        }

        _ptyRouteRegistry?.RegisterSession(tabId, session);
        Tabs?.AddTab(tabId, string.IsNullOrWhiteSpace(node.Text) ? null : node.Text);
    }

    /// <summary>
    /// P4-T5: the tab strip's double-click-to-stop gesture (MainWindow.xaml's <c>EventSetter</c> on
    /// <c>TabsList</c>'s <c>ListBoxItem</c> style). Confirms, then delegates the actual teardown to
    /// <see cref="TabsViewModel.StopTabCommand"/> - this handler's only job is the gesture and the
    /// confirmation; see that command's remarks for why "stop" keeps the tab (frozen scrollback + exit
    /// banner) instead of removing it like the ✕ close button does.
    /// </summary>
    private void TabItem_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as ListBoxItem)?.DataContext is not TabViewModel tab || tab.HasEnded || !tab.HasPtySession)
        {
            return;
        }

        bool confirmed = AccelMessageDialog.ShowConfirm(
            this,
            $"Stop \"{tab.Title}\"? The session's process will be terminated. The tab stays open so you can still see its output.",
            "Stop session",
            AccelDialogIcon.Warning);

        if (!confirmed)
        {
            return;
        }

        Tabs?.StopTabCommand.Execute(tab);
    }

    /// <summary>
    /// Panel B's FILES tree double-click gesture: opens (or selects, if already open -
    /// <see cref="TabsViewModel.AddFileTab"/> is idempotent per path) a tab for the double-clicked
    /// file - editable in panel D's editor when the file reads as text (see
    /// <see cref="ShowFileTabAsync"/>). A no-op for a directory row (its own double-click is the TreeView's
    /// built-in expand/collapse) or the lazy-load <see cref="FilesPanelNodeViewModel"/> placeholder
    /// (empty <see cref="FilesPanelNodeViewModel.Key"/>).
    /// </summary>
    private void FilesTreeViewItem_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as TreeViewItem)?.DataContext is not FilesPanelNodeViewModel node
            || node.IsDirectory
            || string.IsNullOrEmpty(node.Key))
        {
            return;
        }

        Tabs?.AddFileTab(node.Key);
    }

    /// <summary>
    /// Panel B's FILES tree explorer commands (<see cref="FilesPanelViewModel.EntryRemovedOrMoved"/>)
    /// never rebind or reload an open tab whose backing file/folder just moved or was removed - per
    /// this feature's confirmed scope, the tab is simply closed. Handles both a directly-affected file
    /// (exact <see cref="TabViewModel.TabId"/> match) and every tab whose path is nested under a
    /// removed/moved-away directory (prefix match, same containment idiom
    /// <see cref="Accel.Orchestration.FileSystemEntryPlanner"/> uses). Git-change/git-diff tabs are
    /// keyed on the same file-path <c>TabId</c> space (<see cref="TabsViewModel.AddGitChangeTab"/>/
    /// <see cref="TabsViewModel.AddGitDiffTab"/>), so this closes those too with no extra handling.
    ///
    /// <para><see cref="TabsViewModel.CloseTabAsync(string)"/> still runs its existing dirty-tab save/
    /// discard/cancel prompt for an unsaved editable tab - deliberate: "just close it" is about not
    /// trying to re-point the buffer at a new path, not about silently discarding unsaved edits out
    /// from under the user.</para>
    /// </summary>
    private void OnFilesPanelEntryRemovedOrMoved(string oldPath, bool wasDirectory)
    {
        if (Tabs is null)
        {
            return;
        }

        bool Matches(TabViewModel tab) => wasDirectory
            ? tab.TabId.StartsWith(oldPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
              || tab.TabId.StartsWith(oldPath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            : string.Equals(tab.TabId, oldPath, StringComparison.OrdinalIgnoreCase);

        foreach (var tab in Tabs.Tabs.Where(Matches).ToArray())
        {
            _ = Tabs.CloseTabAsync(tab.TabId);
        }
    }

    /// <summary>
    /// Panel B's GIT section double-click gesture: opens (or selects, if already open) a tab for the
    /// double-clicked row - a single-pane view for Added/Untracked/Deleted (editable when a
    /// working-tree copy reads as text; Deleted stays read-only), a
    /// side-by-side diff for Modified (see <see cref="GitPanelEntryViewModel.IsOpenable"/> for exactly
    /// which statuses qualify at all). <see cref="GitChangeRowTemplate"/>'s root <c>Grid</c> is not a
    /// <c>Control</c>, so it has no <c>MouseDoubleClick</c> routed event to hook (unlike panel A/C's
    /// <c>TreeViewItem</c>/<c>ListBoxItem</c> rows) - double-click is detected here via
    /// <see cref="System.Windows.Input.MouseButtonEventArgs.ClickCount"/> instead.
    ///
    /// <para><b>Which side is which, for a Modified row's diff.</b> A staged modification
    /// (<see cref="GitPanelEntryViewModel.IsStaged"/>) compares the index against HEAD - this row
    /// represents that staged change, so "before" is HEAD and "after" is the index blob (not
    /// necessarily the current disk file, which may have drifted further since staging). An unstaged
    /// modification compares the working tree against the index - "before" is the index blob and
    /// "after" is the current disk file.</para>
    /// </summary>
    private void GitChangeRow_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2
            || (sender as FrameworkElement)?.DataContext is not GitPanelEntryViewModel entry
            || !entry.IsOpenable)
        {
            return;
        }

        string title = Path.GetFileName(entry.Path);

        if (entry.IsModified)
        {
            var (oldSide, newSide) = entry.IsStaged
                ? (GitDiffSide.Head, GitDiffSide.Index)
                : (GitDiffSide.Index, GitDiffSide.WorkingTree);

            Tabs?.AddGitDiffTab(entry.FullPath, title, entry.RepoRootPath, entry.Path, oldSide, newSide);
            return;
        }

        Tabs?.AddGitChangeTab(entry.FullPath, title, entry.RepoRootPath, entry.Path);
    }

    /// <summary>Colours <see cref="FileEditor"/> from <see cref="SyntaxHighlighter.Tokenize"/>; wired
    /// into the editor's <c>LineTransformers</c> in the constructor and re-pointed at the current
    /// document by <see cref="ShowFileTabAsync"/>.</summary>
    private readonly SyntaxColorizer _fileSyntaxColorizer;

    /// <summary>Same role as <see cref="_fileSyntaxColorizer"/>, for <see cref="DiffNewEditor"/> - see
    /// <see cref="ShowGitDiffTabAsync"/>'s remarks for why the diff view's editable "After" pane needs
    /// its own instance rather than sharing the file editor's.</summary>
    private readonly SyntaxColorizer _diffSyntaxColorizer;

    /// <summary>Paints <see cref="DiffNewEditor"/>'s added-line background - see
    /// <see cref="DiffLineHighlighter"/>. Refreshed by <see cref="ShowGitDiffTabAsync"/>.</summary>
    private readonly DiffLineHighlighter _diffAddedLineHighlighter;

    /// <summary>
    /// One <see cref="FileEditBuffer"/> per open <b>editable</b> file/git-change tab, keyed by
    /// <see cref="TabViewModel.TabId"/> - which for those kinds is the file's own full path (see
    /// <see cref="TabViewModel.ForFile"/>), hence the case-insensitive comparer that
    /// <see cref="TabsViewModel"/>'s own tab lookup already uses for the same key space.
    ///
    /// <para><b>Why the window owns these.</b> <see cref="FileEditor"/> is a single shared control
    /// re-pointed on every selection, so it cannot be where unsaved text or undo history lives - see
    /// <see cref="FileEditBuffer"/>'s remarks. Entries are created lazily by
    /// <see cref="ShowFileTabAsync"/> on a tab's first activation and removed by
    /// <see cref="EvictFileEditBuffer"/> when the tab closes, so an open tab's buffer survives any
    /// number of tab switches while a closed one is re-read from disk if it is opened again.</para>
    ///
    /// <para>Read-only content is deliberately <b>not</b> cached here: a Deleted GIT entry's
    /// <c>git show</c> output, a non-text file and a failed read all have no file to save back to, so
    /// a buffer for them would be a save target that cannot exist. They get a throwaway document
    /// instead, exactly as the pre-edit viewer did.</para>
    /// </summary>
    private readonly Dictionary<string, FileEditBuffer> _fileEditBuffers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The buffer <see cref="FileEditor"/> is currently pointed at, or <see langword="null"/>
    /// when it is showing read-only content. Exists so caret/scroll can be captured back into the
    /// right buffer when the tab is left, without having to know which tab is being left - see
    /// <see cref="CaptureFileEditorViewState"/>.</summary>
    private FileEditBuffer? _activeFileEditBuffer;

    /// <summary>Same role as <see cref="_activeFileEditBuffer"/>, for <see cref="DiffNewEditor"/> -
    /// the buffer it is currently pointed at when a diff tab's "After" pane is editable
    /// (<see cref="GitDiffSide.WorkingTree"/>), or <see langword="null"/> otherwise. The two fields are
    /// never both non-null for the same buffer at once: <see cref="FileEditor"/> and
    /// <see cref="DiffNewEditor"/> render mutually-exclusive panes.</summary>
    private FileEditBuffer? _activeDiffEditBuffer;

    /// <summary>
    /// Renders <paramref name="tab"/> in panel D's editor, coloured per
    /// <see cref="SyntaxHighlighter.Tokenize"/> (language resolved from the file's extension - see
    /// <see cref="SourceLanguageResolver"/>) - wired as <see cref="TabsViewModel.ShowFileAsync"/>.
    /// Shared by both <see cref="TabKind.File"/> (panel B's FILES tree) and
    /// <see cref="TabKind.GitChange"/> (panel B's GIT section) tabs.
    ///
    /// <para><b>Two outcomes.</b> If the tab has a working-tree file that reads as text, it gets (or
    /// re-uses) a <see cref="FileEditBuffer"/> and the editor becomes writable: re-selecting the tab
    /// restores its unsaved text, undo history, dirty state and caret/scroll, because all of those
    /// live on the buffer's document rather than on the shared control. Otherwise the content is
    /// shown read-only in a throwaway document - see <see cref="_fileEditBuffers"/> for which cases
    /// those are and why they must not be editable.</para>
    ///
    /// <para>A failed read (permission denied, the path vanished between the click and here,
    /// `git show` failing) shows the error message in place of content rather than throwing:
    /// <see cref="TabsViewModel"/>'s own safe-call wrapper would swallow an exception here silently,
    /// leaving the previous tab's content on screen with no explanation.</para>
    /// </summary>
    private async Task ShowFileTabAsync(TabViewModel tab)
    {
        if (tab.IsGitDiffTab)
        {
            await ShowGitDiffTabAsync(tab).ConfigureAwait(true);
            return;
        }

        if (tab.IsMarkdown && tab.IsPreviewMode)
        {
            await ShowMarkdownPreviewAsync(tab).ConfigureAwait(true);
            return;
        }

        // Before anything re-points the editor: remember where the user was in the tab being left.
        CaptureFileEditorViewState();

        if (_fileEditBuffers.TryGetValue(tab.TabId, out var cached))
        {
            // Re-activation only: on first load the buffer *is* the current disk state, so there is
            // nothing to compare against yet. Any outcome here (reloaded, kept, cancelled) still ends
            // with the same tab being shown - the check changes what the buffer contains, never which
            // tab wins the selection.
            var outcome = await ResolveExternalFileChangeAsync(tab, cached, ExternalChangeTrigger.Activation)
                .ConfigureAwait(true);

            // A conflict prompt is modal, which means WPF pumped messages while it was up and the
            // selection may have moved on. Re-pointing the editor at this tab's buffer now would show
            // it under a different tab's header.
            if (outcome != ExternalChangeOutcome.Unchanged
                && Tabs?.SelectedTab is { } selected && !ReferenceEquals(selected, tab))
            {
                return;
            }

            ActivateFileEditBuffer(tab, cached);
            return;
        }

        FileEditBuffer? buffer = null;
        string? readOnlyContent = null;
        SourceLanguage language = SourceLanguage.PlainText;
        try
        {
            language = SourceLanguageResolver.Resolve(tab.TabId);
            buffer = await TryCreateFileEditBufferAsync(tab, language).ConfigureAwait(true);

            if (buffer is null)
            {
                readOnlyContent = NormalizeLineEndings(await ReadTabContentAsync(tab).ConfigureAwait(true));
            }
        }
        catch (Exception ex)
        {
            buffer = null;
            readOnlyContent = $"Could not read file:\n{tab.TabId}\n\n{ex.Message}";
            language = SourceLanguage.PlainText;
        }

        if (buffer is not null)
        {
            _fileEditBuffers[tab.TabId] = buffer;
            ActivateFileEditBuffer(tab, buffer);
            return;
        }

        tab.IsEditable = false;
        tab.IsDirty = false;
        ShowFileEditorDocument(new TextDocument(readOnlyContent ?? string.Empty), language, isReadOnly: true);
        _activeFileEditBuffer = null;

        // Snap back to the top: read-only content is re-read on every activation, so there is no
        // stored view state to return to, and the previous tab's offsets must not carry over.
        FileEditor.CaretOffset = 0;
        FileEditor.ScrollToHome();
        ShowFileViewerPane();
    }

    /// <summary>
    /// Builds the edit buffer for <paramref name="tab"/>, or <see langword="null"/> when the tab must
    /// stay read-only. Four independent reasons for null, all of them "there is nothing a save could
    /// write to": the tab is not a single-pane file/git-change tab, and not a diff tab whose "After"
    /// side is the working tree either (see below); its working-tree copy does not exist (a Deleted
    /// GIT entry, whose content <see cref="ReadTabContentAsync"/> then pulls out of <c>HEAD</c>); or
    /// the bytes are not safely editable as text (<see cref="FileTextSnapshot.IsTextEditable"/> -
    /// saving decoded text back over those would persist U+FFFD in place of the user's data).
    ///
    /// <para>A diff tab (<see cref="TabViewModel.IsGitDiffTab"/>) only qualifies when
    /// <see cref="TabViewModel.GitDiffNewSide"/> is <see cref="GitDiffSide.WorkingTree"/> - an unstaged
    /// Modified entry's "After" pane (see <see cref="ShowGitDiffTabAsync"/>'s remarks). A staged
    /// entry's "After" side is the index blob, which has no direct disk file to write edits back to,
    /// so it stays read-only.</para>
    /// </summary>
    /// <remarks>
    /// The read goes through <see cref="FileTextCodec"/>, not <see cref="File.ReadAllTextAsync(string)"/>
    /// plus <see cref="NormalizeLineEndings"/>: the display text is LF-normalised either way, but the
    /// codec is the only reader that also records the encoding/BOM/EOL shape a save has to reproduce.
    /// It is synchronous, so it runs on the thread pool - a large file must not stall the UI thread
    /// just because the old viewer's read happened to be async.
    /// </remarks>
    private static async Task<FileEditBuffer?> TryCreateFileEditBufferAsync(TabViewModel tab, SourceLanguage language)
    {
        bool canEdit = tab.IsFileTab
            || (tab.IsGitChangeTab && !tab.IsGitDiffTab)
            || (tab.IsGitDiffTab && tab.GitDiffNewSide == GitDiffSide.WorkingTree);

        if (!canEdit || !File.Exists(tab.TabId))
        {
            return null;
        }

        var snapshot = await Task.Run(() => FileTextCodec.Read(tab.TabId)).ConfigureAwait(true);
        if (!snapshot.IsTextEditable)
        {
            return null;
        }

        var document = new TextDocument(snapshot.Text);

        // ClearAll before MarkAsOriginalFile: whatever the constructor's own text assignment did to
        // the undo stack must not be undoable (Ctrl+Z on a freshly opened file would otherwise empty
        // it), and "original file" has to mean "as loaded", measured from an empty stack.
        document.UndoStack.ClearAll();
        document.UndoStack.MarkAsOriginalFile();

        return new FileEditBuffer(document, snapshot, language);
    }

    /// <summary>
    /// Points <see cref="FileEditor"/> at <paramref name="buffer"/> and shows panel D's editor pane.
    /// The document assignment is the whole tab switch: text, undo/redo history and dirty state come
    /// back with it (see <see cref="FileEditBuffer"/>), so nothing here re-reads the file or replays
    /// edits.
    /// </summary>
    private void ActivateFileEditBuffer(TabViewModel tab, FileEditBuffer buffer)
    {
        tab.IsEditable = true;
        tab.IsDirty = !buffer.Document.UndoStack.IsOriginalFile;
        EnsureDirtyListener(tab, buffer);

        ShowFileEditorDocument(buffer.Document, buffer.Language, isReadOnly: false);
        _activeFileEditBuffer = buffer;
        ShowFileViewerPane();

        // Caret first (it is an offset into the document that is already in place), viewport after
        // the pane has actually been laid out: a scroll offset applied while FileViewerHost was still
        // Collapsed - or before the new document has been measured - is silently clamped to 0.
        FileEditor.CaretOffset = Math.Min(buffer.CaretOffset, buffer.Document.TextLength);
        double vertical = buffer.VerticalOffset;
        double horizontal = buffer.HorizontalOffset;
        Dispatcher.BeginInvoke(
            () =>
            {
                if (!ReferenceEquals(_activeFileEditBuffer, buffer))
                {
                    // Selection moved on while this was queued - restoring now would scroll whatever
                    // tab is showing instead.
                    return;
                }

                FileEditor.ScrollToVerticalOffset(vertical);
                FileEditor.ScrollToHorizontalOffset(horizontal);
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>Subscribes <paramref name="buffer"/>'s undo stack to push <see cref="TabViewModel.IsDirty"/>
    /// onto <paramref name="tab"/> - shared by <see cref="ActivateFileEditBuffer"/> and
    /// <see cref="ActivateDiffEditBuffer"/>, since both surfaces track dirty state the same way (see
    /// <see cref="ActivateFileEditBuffer"/>'s remarks). Subscribed once per buffer and dropped in
    /// <see cref="EvictFileEditBuffer"/>.</summary>
    private static void EnsureDirtyListener(TabViewModel tab, FileEditBuffer buffer)
    {
        if (buffer.DirtyListener is not null)
        {
            return;
        }

        var undoStack = buffer.Document.UndoStack;
        buffer.DirtyListener = (_, e) =>
        {
            if (e.PropertyName == nameof(UndoStack.IsOriginalFile))
            {
                tab.IsDirty = !undoStack.IsOriginalFile;
            }
        };
        undoStack.PropertyChanged += buffer.DirtyListener;
    }

    /// <summary>
    /// Points <see cref="DiffNewEditor"/> at <paramref name="buffer"/> - the diff view's editable
    /// "After" pane equivalent of <see cref="ActivateFileEditBuffer"/>. Does not itself show/hide any
    /// pane or control: <see cref="ShowGitDiffTabAsync"/> already toggles <see cref="DiffNewEditor"/>
    /// vs. <see cref="DiffNewText"/>/<see cref="DiffNewLineNumbers"/> visibility once, after both diff
    /// sides are resolved.
    /// </summary>
    private void ActivateDiffEditBuffer(TabViewModel tab, FileEditBuffer buffer)
    {
        tab.IsEditable = true;
        tab.IsDirty = !buffer.Document.UndoStack.IsOriginalFile;
        EnsureDirtyListener(tab, buffer);

        DiffNewEditor.Document = buffer.Document;
        _diffSyntaxColorizer.SetDocument(buffer.Document, buffer.Language);
        _activeDiffEditBuffer = buffer;

        DiffNewEditor.CaretOffset = Math.Min(buffer.CaretOffset, buffer.Document.TextLength);
        double vertical = buffer.VerticalOffset;
        double horizontal = buffer.HorizontalOffset;
        Dispatcher.BeginInvoke(
            () =>
            {
                if (!ReferenceEquals(_activeDiffEditBuffer, buffer))
                {
                    // Selection moved on while this was queued - restoring now would scroll whatever
                    // tab is showing instead.
                    return;
                }

                DiffNewEditor.ScrollToVerticalOffset(vertical);
                DiffNewEditor.ScrollToHorizontalOffset(horizontal);
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The one place <see cref="FileEditor"/>'s document, read-only state and colouring are set
    /// together - they have to move as a unit: a document swapped without re-pointing the colouriser
    /// would render the previous tab's cached colours, and one swapped without the read-only flag
    /// would let a Deleted GIT entry's <c>git show</c> text be typed into.
    /// </summary>
    /// <remarks>
    /// The colouriser is re-pointed AFTER the document is in place because it tokenizes the whole
    /// document on that call. Its own <c>SetDocument</c> also drops any pending debounced rebuild in
    /// favour of an immediate one, so the first rendered frame is already coloured.
    /// </remarks>
    private void ShowFileEditorDocument(TextDocument document, SourceLanguage language, bool isReadOnly)
    {
        FileEditor.IsReadOnly = isReadOnly;
        FileEditor.Document = document;
        _fileSyntaxColorizer.SetDocument(document, language);

        // Not colour-only, not glyph-only: the pane's own accessible name says which mode it is in
        // (CLAUDE_DESIGN.md's accessibility rule - the same reason TabViewModel carries an
        // EditStateSuffix).
        System.Windows.Automation.AutomationProperties.SetName(
            FileEditor, isReadOnly ? "File content (read-only)" : "File content (editable)");
    }

    /// <summary>
    /// Copies the caret and scroll offsets out of the shared control into the buffer they belong to,
    /// so returning to that tab puts the user back where they were. Called whenever panel D is about
    /// to stop showing the current buffer - a tab switch, a flip to the terminal/diff/markdown-preview
    /// pane - rather than on every caret move, since only the last position before leaving matters.
    /// A no-op when the editor is showing read-only content, or when something has already re-pointed
    /// it at another document (whose offsets are not this buffer's to record).
    /// </summary>
    private void CaptureFileEditorViewState()
    {
        if (_activeFileEditBuffer is { } buffer && ReferenceEquals(FileEditor.Document, buffer.Document))
        {
            buffer.CaretOffset = FileEditor.CaretOffset;
            buffer.VerticalOffset = FileEditor.VerticalOffset;
            buffer.HorizontalOffset = FileEditor.HorizontalOffset;
        }

        // Same capture, for whichever buffer the diff view's editable "After" pane
        // (DiffNewEditor) is currently showing - see _activeDiffEditBuffer's remarks.
        if (_activeDiffEditBuffer is { } diffBuffer && ReferenceEquals(DiffNewEditor.Document, diffBuffer.Document))
        {
            diffBuffer.CaretOffset = DiffNewEditor.CaretOffset;
            diffBuffer.VerticalOffset = DiffNewEditor.VerticalOffset;
            diffBuffer.HorizontalOffset = DiffNewEditor.HorizontalOffset;
        }
    }

    /// <summary>
    /// Drops a closed tab's edit buffer - wired as <see cref="TabsViewModel.ReleaseFileTab"/>. Keeping
    /// it would pin the document (and its whole undo history) for a tab that no longer exists, and
    /// would make re-opening the same path resurrect a stale buffer instead of reading the file as it
    /// now is. A no-op for the tab kinds that never had one.
    /// </summary>
    private void EvictFileEditBuffer(TabViewModel tab)
    {
        if (!_fileEditBuffers.Remove(tab.TabId, out var buffer))
        {
            return;
        }

        buffer.Detach();

        if (ReferenceEquals(_activeFileEditBuffer, buffer))
        {
            // The editor may still be pointed at this document until the next selection renders;
            // forgetting it here is what stops CaptureFileEditorViewState writing into a dead buffer.
            _activeFileEditBuffer = null;
        }

        if (ReferenceEquals(_activeDiffEditBuffer, buffer))
        {
            _activeDiffEditBuffer = null;
        }
    }

    /// <summary>
    /// T6: writes <paramref name="tab"/>'s edit buffer back to disk - wired as
    /// <see cref="TabsViewModel.SaveFileAsync"/>, called (through <see cref="TabsViewModel.SaveTabCommand"/>)
    /// from Ctrl+S (<c>MainWindow.xaml</c>'s <c>Window.InputBindings</c>) and, later, the tab header's
    /// Save button (T7). <see cref="TabsViewModel.SaveTabAsync"/> has already checked
    /// <see cref="TabViewModel.IsEditable"/>/<see cref="TabViewModel.IsDirty"/>, so an editable buffer
    /// for this tab is expected to exist; a missing one (should not happen) is treated as nothing to
    /// save rather than crashing.
    ///
    /// <para><b>On success</b>: the buffer's <see cref="FileEditBuffer.Snapshot"/> is replaced with a
    /// fresh <see cref="FileTextCodec.Read"/> of the file just written, so its tracked
    /// <see cref="FileTextSnapshot.LastWriteUtc"/>/<see cref="FileTextSnapshot.Length"/> matches reality
    /// (a later external-change check must not immediately think the save itself was an external
    /// change), the undo stack is marked back to "original file" (flips
    /// <see cref="TabViewModel.IsDirty"/> to false through <see cref="ActivateFileEditBuffer"/>'s
    /// <c>DirtyListener</c>), and panel A is refreshed through the exact same mechanism
    /// <see cref="RemoveSession_Click"/> already uses after a disk-changing operation
    /// (<see cref="RootsPanelViewModel.RefreshCommand"/> -&gt; <c>ITelemetryFeed.RequestRefresh</c>) -
    /// no second refresh mechanism is invented here.</para>
    ///
    /// <para><b>Panel B's GIT section is deliberately not refreshed from here.</b> It used to be
    /// assumed to ride along on the <c>RequestRefresh</c> above, but that lands in
    /// <see cref="GitPanelViewModel.Rebuild"/>, whose same-root fast path is a no-op - so it never
    /// actually did. The write this method just performed is now picked up by that panel's own
    /// <see cref="Accel.App.Services.IDirectoryWatcher"/>, which covers a save made from anywhere
    /// (here, an agent, another editor) rather than only from this one call site.</para>
    ///
    /// <para><b>On failure</b> (read-only file, permission denied, the path removed underneath the
    /// editor): a message dialog reports it and the buffer/tab are left exactly as they were - still
    /// dirty, so the failure is never silently swallowed and the user can retry or copy their text out.</para>
    /// </summary>
    private async Task<bool> SaveFileTabAsync(TabViewModel tab)
    {
        if (!_fileEditBuffers.TryGetValue(tab.TabId, out var buffer))
        {
            return false;
        }

        // T9's guard, deliberately the last thing before the write: if something else (typically a
        // Claude Code session) rewrote the file since this buffer read it, the user decides whether
        // their text still gets to win. False means the save must not happen - either they cancelled,
        // or they took the disk version and there is nothing left to save. Note that the text is read
        // out of the document *after* this, since a "reload from disk" outcome replaces it.
        if (!await ConfirmSaveOverExternalChangeAsync(tab, buffer).ConfigureAwait(true))
        {
            return false;
        }

        string text = buffer.Document.Text;

        try
        {
            await FileTextCodec.WriteAsync(tab.TabId, text, buffer.Snapshot).ConfigureAwait(true);

            // Re-read the just-written file rather than hand-rolling a new snapshot: this is the one
            // way to be sure the tracked LastWriteUtc/Length match exactly what landed on disk (down to
            // whatever FileInfo rounding/granularity the filesystem applies), not merely what this
            // process believes it wrote.
            buffer.Snapshot = await Task.Run(() => FileTextCodec.Read(tab.TabId)).ConfigureAwait(true);

            // The conflict this buffer may have been carrying is settled - the disk now holds the
            // version the user chose. Keeping the acknowledgement would have it compared against a
            // state that can no longer recur.
            buffer.AcknowledgedDiskState = null;
        }
        catch (Exception ex)
        {
            AccelMessageDialog.ShowMessage(
                this,
                $"Could not save this file:\n{tab.TabId}\n\n{ex.Message}",
                "Save file",
                AccelDialogIcon.Warning);
            return false;
        }

        buffer.Document.UndoStack.MarkAsOriginalFile();

        // Same refresh call RemoveSession_Click already uses after a disk-changing operation - this
        // save just changed a tracked file's content, which panel B's GIT section must pick up too.
        RootsPanel?.RefreshCommand.Execute(null);

        return true;
    }

    /// <summary>
    /// T6: reverts <paramref name="tab"/>'s edit buffer to what is currently on disk - wired as
    /// <see cref="TabsViewModel.DiscardFileAsync"/>, called from
    /// <see cref="TabsViewModel.DiscardTabChangesCommand"/> (the tab header's Discard button, T7).
    /// Same "nothing to do without a buffer" posture as <see cref="SaveFileTabAsync"/>.
    ///
    /// <para>The re-read text replaces the document's contents inside a single AvalonEdit undo group
    /// (<see cref="UndoStack.StartUndoGroup()"/>/<see cref="UndoStack.EndUndoGroup()"/>) rather than
    /// clearing the undo stack - so Ctrl+Z immediately after a discard undoes the discard itself and
    /// restores the pre-discard text, exactly like any other single edit would.</para>
    ///
    /// <para>A failed re-read (path deleted, permission denied) reports the error the same way
    /// <see cref="SaveFileTabAsync"/> does and leaves the buffer/tab untouched - still dirty, so the
    /// user is never left thinking a discard silently happened when it did not.</para>
    /// </summary>
    private async Task DiscardFileTabChangesAsync(TabViewModel tab)
    {
        if (!_fileEditBuffers.TryGetValue(tab.TabId, out var buffer))
        {
            return;
        }

        FileTextSnapshot snapshot;
        try
        {
            snapshot = await Task.Run(() => FileTextCodec.Read(tab.TabId)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AccelMessageDialog.ShowMessage(
                this,
                $"Could not discard changes to this file:\n{tab.TabId}\n\n{ex.Message}",
                "Discard changes",
                AccelDialogIcon.Warning);
            return;
        }

        if (!snapshot.IsTextEditable)
        {
            AccelMessageDialog.ShowMessage(
                this,
                $"This file can no longer be read as text, so its changes could not be discarded:\n{tab.TabId}",
                "Discard changes",
                AccelDialogIcon.Warning);
            return;
        }

        var document = buffer.Document;
        var undoStack = document.UndoStack;
        undoStack.StartUndoGroup();
        try
        {
            document.Replace(0, document.TextLength, snapshot.Text);
        }
        finally
        {
            undoStack.EndUndoGroup();
        }

        undoStack.MarkAsOriginalFile();
        buffer.Snapshot = snapshot;

        // A discard re-reads the file, so any external change the user had chosen to overwrite is now
        // simply the buffer's content - there is no conflict left to remember (T9).
        buffer.AcknowledgedDiskState = null;

        if (ReferenceEquals(_activeFileEditBuffer, buffer))
        {
            // The replace above may have shortened the text out from under the editor's current caret/
            // scroll - clamp rather than let AvalonEdit fault on a now out-of-range offset.
            FileEditor.CaretOffset = Math.Min(FileEditor.CaretOffset, document.TextLength);
        }
    }

    /// <summary>
    /// T8's per-tab close guard - wired as <see cref="TabsViewModel.ConfirmCloseDirtyTabAsync"/>.
    /// Asks Save/Discard/Cancel for one dirty, editable tab, reusing the exact same three-way shell
    /// (<see cref="AccelMessageDialog.ShowChoice"/>) T9's external-change conflict prompt already
    /// established, rather than inventing a fourth button shape for what is fundamentally the same
    /// "two real actions plus a safe do-nothing default" prompt.
    /// </summary>
    private Task<AccelDialogChoice> ConfirmCloseDirtyTabAsync(TabViewModel tab)
    {
        string name = Path.GetFileName(tab.TabId);
        var choice = AccelMessageDialog.ShowChoice(
            this,
            $"{name} has unsaved changes.\n\nSave writes them to disk before closing. Discard closes the tab " +
            "and throws the changes away. Cancel leaves the tab open.",
            "Unsaved changes",
            primaryText: "Save",
            secondaryText: "Discard",
            cancelText: "Cancel",
            icon: AccelDialogIcon.Warning);

        return Task.FromResult(choice);
    }

    /// <summary>
    /// T8's shutdown guard: <c>Window.Closing</c> is the one place every quit path (the title bar's
    /// close box, Alt+F4, <c>Application.Current.Shutdown()</c>, and <c>Program.cs</c>'s Ctrl+C ->
    /// <c>window.Close()</c>) converges on - unlike <see cref="Window.Closed"/> (already used by
    /// <c>Program.cs</c> for its own teardown), <c>Closing</c> is cancelable, which is what lets this
    /// actually stop a quit rather than merely react to one.
    ///
    /// <para><b>Why a single summary dialog instead of one prompt per dirty tab.</b> A user who is
    /// quitting with several dirty tabs open almost always wants the same answer for all of them, and a
    /// user in a hurry to quit is exactly the user N sequential modal prompts would train to blindly
    /// mash through - the opposite of what a "did you mean to lose this" guard is for. One dialog that
    /// names every affected file and offers Save All / Discard All / Cancel says the same thing with one
    /// click instead of N, and Cancel still aborts the whole quit atomically (no tab gets saved while
    /// another is left hanging mid-decision).</para>
    ///
    /// <para>Deliberately synchronous-looking but is not: <see cref="CancelEventArgs.Cancel"/> is set
    /// <see langword="true"/> immediately, before anything async runs (a synchronous <c>Closing</c>
    /// handler cannot await and still control whether the close proceeds), and then this method
    /// re-invokes <see cref="Window.Close"/> from a dispatcher callback if the user's answer says the
    /// quit should actually happen - <see cref="_closeConfirmed"/> is what stops that second call from
    /// re-entering this same guard.</para>
    /// </summary>
    private bool _closeConfirmed;

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed)
        {
            // The re-entrant Close() below, already confirmed - let it proceed without asking again.
            return;
        }

        var dirtyTabs = (Tabs?.Tabs ?? Enumerable.Empty<TabViewModel>())
            .Where(t => t.IsEditable && t.IsDirty)
            .ToList();

        if (dirtyTabs.Count == 0)
        {
            return;
        }

        // Stop the quit here and now; a later Close() (once the user has actually answered) is what
        // lets it proceed. Set before anything async runs, per this method's own remarks.
        e.Cancel = true;

        string list = string.Join('\n', dirtyTabs.Select(t => " - " + Path.GetFileName(t.TabId)));
        var choice = AccelMessageDialog.ShowChoice(
            this,
            $"{dirtyTabs.Count} tab(s) have unsaved changes:\n\n{list}\n\n" +
            "Save All writes every one of them to disk before quitting. Discard All quits and throws " +
            "all of these changes away. Cancel keeps Accel open.",
            "Unsaved changes",
            primaryText: "Save All",
            secondaryText: "Discard All",
            cancelText: "Cancel",
            icon: AccelDialogIcon.Warning);

        if (choice == AccelDialogChoice.Cancel)
        {
            return;
        }

        if (choice == AccelDialogChoice.Primary)
        {
            // Save All: the same write path a single Ctrl+S uses, one tab at a time. A failure (reported
            // by SaveFileTabAsync itself, same as T6's own posture) leaves that tab dirty and aborts the
            // quit - the same never-silently-swallow rule as the per-tab close guard, applied across the
            // whole set: quitting must not go on to discard tabs the user just failed to save.
            foreach (var tab in dirtyTabs)
            {
                if (!await SaveFileTabAsync(tab).ConfigureAwait(true))
                {
                    return;
                }
            }
        }

        // Discard All (or every save above succeeded): nothing left to lose, so the quit can proceed.
        // Re-invoking Close() re-raises Closing - _closeConfirmed is what keeps that second pass from
        // asking the same question again.
        _closeConfirmed = true;
        await Dispatcher.BeginInvoke(new Action(Close)).Task.ConfigureAwait(true);
    }

    /// <summary>What prompted an external-change check. Only affects wording and what "cancel" means
    /// to the caller: the detection and the three outcomes are identical either way.</summary>
    private enum ExternalChangeTrigger
    {
        /// <summary>A cached buffer is being re-shown by <see cref="ShowFileTabAsync"/>.</summary>
        Activation,

        /// <summary>A save is about to write over the file.</summary>
        BeforeSave,
    }

    /// <summary>How an external-change check resolved. Four cases, none of them collapsible: a caller
    /// about to write to disk has to distinguish "nothing happened" from "the user chose to overwrite"
    /// from "the buffer no longer holds the edits you were going to save".</summary>
    private enum ExternalChangeOutcome
    {
        /// <summary>The file on disk still matches what the buffer read (or the conflict had already
        /// been acknowledged). Nothing was shown and nothing changed.</summary>
        Unchanged,

        /// <summary>The buffer was re-read from disk and is now clean - silently when it had no
        /// unsaved edits to lose, or because the user chose to discard them.</summary>
        Reloaded,

        /// <summary>The user chose to keep their unsaved version and overwrite what is on disk.</summary>
        KeepMine,

        /// <summary>The user dismissed the conflict prompt. The buffer is untouched and still dirty;
        /// whatever triggered the check must not proceed.</summary>
        Cancelled,
    }

    /// <summary>How long <see cref="ShowFileEditorNotice"/> leaves its message up.</summary>
    private static readonly TimeSpan FileEditorNoticeDuration = TimeSpan.FromSeconds(6);

    /// <summary>Hides <see cref="FileEditorNotice"/> again; created on first use so the designer
    /// constructor does not spin up a timer nobody will stop.</summary>
    private DispatcherTimer? _fileEditorNoticeTimer;

    /// <summary>
    /// Detects - and, when it must, asks the user how to resolve - the case this whole editor is most
    /// exposed to: <b>the file changed on disk while a tab was holding it open</b>.
    ///
    /// <para><b>Why this matters more here than in a normal editor.</b> Accel exists to watch Claude
    /// Code sessions, and those sessions rewrite exactly the files a user is most likely to have open
    /// in panel D, while they are open. Writing a stale buffer back over an agent's edit would delete
    /// work the user never saw and never authorised. So: a clean buffer quietly follows the file, and
    /// a dirty one never resolves itself - the user picks, or nothing happens.</para>
    ///
    /// <para><b>Why polling (activation + save) rather than a <see cref="System.IO.FileSystemWatcher"/>.</b>
    /// A watcher buys live detection, and pays for it with a per-open-file OS handle whose lifetime
    /// has to be tied to tab open/close/rename and to the window's own teardown - the classic place
    /// this app would leak handles - plus a debounce for the burst of events a single agent write
    /// emits, plus deciding what a mid-typing notification is even allowed to do to the document. The
    /// only two moments where staleness can actually cause harm are the two where a stale buffer gets
    /// used: when it is put back on screen, and when it is written to disk. Checking exactly there is
    /// two <c>FileInfo</c> stats, has no lifetime at all, and cannot leak. The gap it leaves - a file
    /// rewritten while its tab sits visible and untouched shows old text until the next activation or
    /// save - is cosmetic, never data-losing, because the save-time check runs before any write.
    /// A watcher stays a strictly additive upgrade if live refresh is ever wanted: it would feed this
    /// same method rather than replace it.</para>
    /// </summary>
    private async Task<ExternalChangeOutcome> ResolveExternalFileChangeAsync(
        TabViewModel tab, FileEditBuffer buffer, ExternalChangeTrigger trigger)
    {
        var current = await Task.Run(() => ExternalFileChangeDetector.Probe(tab.TabId)).ConfigureAwait(true);

        if (!ExternalFileChangeDetector.HasChangedOnDisk(buffer.Snapshot, current))
        {
            return ExternalChangeOutcome.Unchanged;
        }

        if (!tab.IsDirty)
        {
            // Nothing of the user's to lose, so following the file is strictly better than showing
            // text that is already wrong - but it still has to be *said*, or the tab would silently
            // disagree with what the user last typed into it from somewhere else.
            if (!await TryReloadFileEditBufferAsync(tab, buffer).ConfigureAwait(true))
            {
                return ExternalChangeOutcome.Unchanged;
            }

            ShowFileEditorNotice($"Reloaded {Path.GetFileName(tab.TabId)} - it changed on disk.");
            return ExternalChangeOutcome.Reloaded;
        }

        if (buffer.AcknowledgedDiskState == current)
        {
            // Already answered for this exact on-disk state - see FileEditBuffer.AcknowledgedDiskState.
            return ExternalChangeOutcome.KeepMine;
        }

        string name = Path.GetFileName(tab.TabId);
        string message =
            $"{name} was changed on disk after Accel read it, and you have unsaved changes here.\n\n" +
            "This usually means a Claude Code session (or another editor) rewrote the file. " +
            "Whichever version you pick, the other one is lost.\n\n" +
            (trigger == ExternalChangeTrigger.BeforeSave
                ? "Keep my version saves your text over the file on disk. Reload from disk throws your unsaved changes away and does not save. Cancel leaves everything as it is."
                : "Keep my version leaves your unsaved text in the tab. Reload from disk throws your unsaved changes away. Cancel leaves everything as it is.");

        var choice = AccelMessageDialog.ShowChoice(
            this,
            message,
            "File changed on disk",
            primaryText: "Keep my version",
            secondaryText: "Reload from disk",
            cancelText: "Cancel",
            icon: AccelDialogIcon.Warning);

        switch (choice)
        {
            case AccelDialogChoice.Primary:
                buffer.AcknowledgedDiskState = current;
                return ExternalChangeOutcome.KeepMine;

            case AccelDialogChoice.Secondary:
                if (!await TryReloadFileEditBufferAsync(tab, buffer).ConfigureAwait(true))
                {
                    // The re-read failed after the user agreed to drop their edits; keeping those
                    // edits is the only outcome left that does not destroy both versions - and it has
                    // to be said, or the tab would look like the discard succeeded.
                    AccelMessageDialog.ShowMessage(
                        this,
                        $"This file could not be re-read, so your unsaved changes were kept instead of being discarded:\n{tab.TabId}",
                        "File changed on disk",
                        AccelDialogIcon.Warning);
                    return ExternalChangeOutcome.Cancelled;
                }

                // Not silent, unlike the clean-buffer reload: the user gave up work here, so the tab
                // says so rather than just quietly changing content under them.
                ShowFileEditorNotice($"Discarded your unsaved changes and reloaded {name} from disk.");
                return ExternalChangeOutcome.Reloaded;

            default:
                return ExternalChangeOutcome.Cancelled;
        }
    }

    /// <summary>
    /// The guard a save has to pass: re-checks the file immediately before the write and returns
    /// whether writing is still the right thing to do. <see langword="false"/> means the save must be
    /// abandoned - either the user cancelled (the tab stays dirty, exactly as it was) or they chose to
    /// take the version on disk instead (the buffer has already been re-read and is now clean, so
    /// there is nothing left to save).
    /// </summary>
    /// <remarks>
    /// Runs immediately before the write rather than at the start of the save command: everything in
    /// between - a dialog, an await - is time for another writer to land, and the whole value of this
    /// check is that it is the last thing to happen before the bytes go out.
    /// </remarks>
    private async Task<bool> ConfirmSaveOverExternalChangeAsync(TabViewModel tab, FileEditBuffer buffer)
    {
        var outcome = await ResolveExternalFileChangeAsync(tab, buffer, ExternalChangeTrigger.BeforeSave)
            .ConfigureAwait(true);

        return outcome is ExternalChangeOutcome.Unchanged or ExternalChangeOutcome.KeepMine;
    }

    /// <summary>
    /// Replaces <paramref name="buffer"/>'s text with what is on disk now, resetting it to clean, and
    /// returns whether that succeeded. Best-effort caret/scroll preservation: the offsets are clamped
    /// to the new length rather than mapped through the change, because there is no meaningful mapping
    /// from an offset in one version of a file to an offset in a version somebody else wrote.
    /// </summary>
    /// <remarks>
    /// The undo stack is cleared rather than made undoable. A reload is not the user's edit to take
    /// back, and the entries below it describe a document that no longer exists - replaying them onto
    /// the new text would splice two unrelated versions together. <see cref="UndoStack.ClearAll"/>
    /// before <see cref="UndoStack.MarkAsOriginalFile"/>, for the same reason the initial load does it
    /// in that order: "not dirty" has to be measured from an empty stack.
    /// </remarks>
    private async Task<bool> TryReloadFileEditBufferAsync(TabViewModel tab, FileEditBuffer buffer)
    {
        FileTextSnapshot snapshot;
        try
        {
            snapshot = await Task.Run(() => FileTextCodec.Read(tab.TabId)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Locked or unreadable right now. Leave the buffer alone and say nothing: the check will
            // run again on the next activation or save, and a failed refresh is not a reason to
            // disturb - let alone empty - a tab the user can still read.
            return false;
        }

        if (!snapshot.IsTextEditable)
        {
            // Whatever is there now is not text. Replacing the buffer with a decode full of U+FFFD
            // would make a save write those replacement characters back over it.
            return false;
        }

        // Capture before the replace moves the caret; a no-op unless this buffer is the one on screen.
        CaptureFileEditorViewState();

        var document = buffer.Document;
        document.Replace(0, document.TextLength, snapshot.Text);
        document.UndoStack.ClearAll();
        document.UndoStack.MarkAsOriginalFile();

        buffer.Snapshot = snapshot;
        buffer.AcknowledgedDiskState = null;
        buffer.CaretOffset = Math.Min(buffer.CaretOffset, document.TextLength);
        tab.IsDirty = false;

        // On the activation path ActivateFileEditBuffer restores the view right after this returns;
        // on the save path nothing else is going to, and the replace has already scrolled the editor.
        if (ReferenceEquals(_activeFileEditBuffer, buffer) && ReferenceEquals(FileEditor.Document, document))
        {
            FileEditor.CaretOffset = buffer.CaretOffset;
            FileEditor.ScrollToVerticalOffset(buffer.VerticalOffset);
            FileEditor.ScrollToHorizontalOffset(buffer.HorizontalOffset);
        }

        return true;
    }

    /// <summary>
    /// Puts a short, self-dismissing message over the top-right of the editor. The deliberately
    /// non-modal half of the external-change story: a silent reload had nothing at risk, so it owes
    /// the user an explanation but not an interruption - a dialog there would train them to dismiss
    /// dialogs, which is precisely the reflex the conflict prompt cannot afford.
    /// </summary>
    private void ShowFileEditorNotice(string message)
    {
        FileEditorNoticeText.Text = message;
        System.Windows.Automation.AutomationProperties.SetName(FileEditorNotice, message);
        FileEditorNotice.Visibility = Visibility.Visible;

        _fileEditorNoticeTimer ??= new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _fileEditorNoticeTimer.Stop();
        _fileEditorNoticeTimer.Interval = FileEditorNoticeDuration;
        _fileEditorNoticeTimer.Tick -= HideFileEditorNotice;
        _fileEditorNoticeTimer.Tick += HideFileEditorNotice;
        _fileEditorNoticeTimer.Start();
    }

    private void HideFileEditorNotice(object? sender, EventArgs e)
    {
        _fileEditorNoticeTimer?.Stop();
        FileEditorNotice.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Renders a markdown <see cref="TabKind.File"/>/<see cref="TabKind.GitChange"/> tab (never a
    /// diff - <see cref="TabViewModel.IsMarkdown"/> is already false for one) as rendered HTML in
    /// <see cref="MarkdownPreviewHost"/>, instead of <see cref="ShowFileTabAsync"/>'s usual
    /// highlighted-text path - branched to from there when
    /// <see cref="TabViewModel.IsPreviewMode"/> is set. If an editable <see cref="FileEditBuffer"/>
    /// already exists for this tab (the same <see cref="_fileEditBuffers"/> lookup
    /// <see cref="ShowFileTabAsync"/> uses), renders that buffer's current (possibly unsaved) text so
    /// preview never shows stale disk content for a dirty tab. Otherwise falls back to
    /// <see cref="ReadTabContentAsync"/> (same disk-read/git-show-fallback rules, same content both
    /// views would show) and reports a failed read as rendered text in place of content, same posture
    /// as <see cref="ShowFileTabAsync"/>'s own try/catch. Neither path touches
    /// <see cref="_fileEditBuffers"/> or <see cref="FileEditor"/>'s document - this method only
    /// decides where the text handed to the renderer comes from.
    /// </summary>
    private async Task ShowMarkdownPreviewAsync(TabViewModel tab)
    {
        // Must run BEFORE MarkdownPreview.RenderAsync's first-ever call (which awaits
        // MarkdownPreviewView.Initialization) - see that property's remarks: WebView2 needs this
        // pane to already be un-Collapsed/laid-out for EnsureCoreWebView2Async to complete at all.
        // No extra flicker from reordering this ahead of content being ready: the pane was always
        // going to end up shown here regardless.
        ShowMarkdownPreviewPane();

        string content;
        if (_fileEditBuffers.TryGetValue(tab.TabId, out var buffer))
        {
            content = buffer.Document.Text;
        }
        else
        {
            try
            {
                content = await ReadTabContentAsync(tab).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                content = $"Could not read file:\n{tab.TabId}\n\n{ex.Message}";
            }
        }

        string bodyHtml = Markdig.Markdown.ToHtml(content);
        await MarkdownPreview.RenderAsync(bodyHtml).ConfigureAwait(true);
    }

    /// <summary>
    /// Renders a Modified git-change tab's two sides side-by-side (see
    /// <see cref="TabViewModel.GitDiffOldSide"/>/<see cref="TabViewModel.GitDiffNewSide"/> and
    /// <c>MainWindow.GitChangeRow_MouseLeftButtonDown</c>'s remarks for which side is which). Each
    /// side is read and rendered independently - a failure reading one side (e.g. the file was never
    /// committed, so <see cref="GitDiffSide.Head"/> has nothing to show) does not blank out the other.
    ///
    /// <para><b>The "After" pane is editable when it is the working tree.</b> An unstaged Modified
    /// entry's <see cref="TabViewModel.GitDiffNewSide"/> is <see cref="GitDiffSide.WorkingTree"/> - a
    /// real disk file a save can write to - so it gets (or re-uses) a <see cref="FileEditBuffer"/> via
    /// <see cref="DiffNewEditor"/>, exactly the same buffer <see cref="ShowFileTabAsync"/> would use
    /// for a single-pane view of the same path (they share <see cref="_fileEditBuffers"/>, keyed by
    /// <see cref="TabViewModel.TabId"/> - a diff tab and a single-pane tab for the same file are the
    /// same <see cref="TabViewModel"/> to begin with, see <see cref="TabsViewModel.AddGitDiffTab"/>).
    /// A staged entry's "After" side is the index blob (<see cref="GitDiffSide.Index"/>), which has no
    /// disk file behind it, so it stays read-only in <see cref="DiffNewText"/> like before.</para>
    /// </summary>
    private async Task ShowGitDiffTabAsync(TabViewModel tab)
    {
        // Before anything re-points either editor: remember where the user was in whichever tab (a
        // single-pane file view or another diff tab) is being left - see ShowFileTabAsync's own
        // opening remark for why this must happen first.
        CaptureFileEditorViewState();

        SourceLanguage language = SourceLanguageResolver.Resolve(tab.TabId);

        string oldContent;
        try
        {
            oldContent = await ReadGitDiffSideAsync(tab, tab.GitDiffOldSide!.Value).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            oldContent = $"Could not read \"before\" content:\n{ex.Message}";
        }

        FileEditBuffer? buffer = null;
        string newContent;
        if (tab.GitDiffNewSide == GitDiffSide.WorkingTree)
        {
            if (_fileEditBuffers.TryGetValue(tab.TabId, out var cached))
            {
                // Re-activation: show the live (possibly unsaved) buffer text, not a fresh disk read -
                // same "the buffer is the source of truth once one exists" rule ShowMarkdownPreviewAsync
                // already follows.
                buffer = cached;
                newContent = buffer.Document.Text;
            }
            else
            {
                try
                {
                    buffer = await TryCreateFileEditBufferAsync(tab, language).ConfigureAwait(true);
                    newContent = buffer is not null
                        ? buffer.Document.Text
                        : await ReadGitDiffSideAsync(tab, tab.GitDiffNewSide!.Value).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    newContent = $"Could not read \"after\" content:\n{ex.Message}";
                }
            }
        }
        else
        {
            try
            {
                newContent = await ReadGitDiffSideAsync(tab, tab.GitDiffNewSide!.Value).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                newContent = $"Could not read \"after\" content:\n{ex.Message}";
            }
        }

        oldContent = NormalizeLineEndings(oldContent);
        newContent = NormalizeLineEndings(newContent);

        string[] oldLines = oldContent.Split('\n');
        string[] newLines = newContent.Split('\n');
        _diffMarks = ComputeDiffMarks(oldLines, newLines, out var removedOldLines, out var addedNewLines);

        var removedLineBrush = (Brush)FindResource("DiffRemovedLineBrush");
        var addedLineBrush = (Brush)FindResource("DiffAddedLineBrush");
        DiffOldText.Document = BuildHighlightedDocument(oldContent, language, i => removedOldLines.Contains(i) ? removedLineBrush : null);

        SetLineNumbers(DiffOldLineNumbers, oldLines.Length);
        ResetScroll(DiffOldText, DiffOldLineNumbersTransform);

        if (buffer is not null)
        {
            _fileEditBuffers[tab.TabId] = buffer;
            _diffAddedLineHighlighter.SetHighlightedLines(addedNewLines, addedLineBrush);
            ActivateDiffEditBuffer(tab, buffer);
            DiffNewEditor.TextArea.TextView.Redraw();

            DiffNewText.Visibility = Visibility.Collapsed;
            DiffNewLineNumbers.Visibility = Visibility.Collapsed;
            DiffNewEditor.Visibility = Visibility.Visible;
        }
        else
        {
            tab.IsEditable = false;
            tab.IsDirty = false;

            DiffNewText.Document = BuildHighlightedDocument(newContent, language, i => addedNewLines.Contains(i) ? addedLineBrush : null);
            SetLineNumbers(DiffNewLineNumbers, newLines.Length);
            ResetScroll(DiffNewText, DiffNewLineNumbersTransform);

            DiffNewEditor.Visibility = Visibility.Collapsed;
            DiffNewText.Visibility = Visibility.Visible;
            DiffNewLineNumbers.Visibility = Visibility.Visible;
            _activeDiffEditBuffer = null;
        }

        _diffMarkTotalLines = newLines.Length;
        RenderDiffMarkStrip();

        ShowDiffViewerPane();
    }

    /// <summary>
    /// Panel D has exactly four mutually-exclusive "panes": the terminal, the single-pane file
    /// viewer, the side-by-side diff viewer, and the rendered-HTML markdown preview - these four
    /// helpers are the only place any of them is shown/hidden, so exactly one is ever visible at a
    /// time.
    ///
    /// <para><b>Why <see cref="Terminal"/> and <see cref="MarkdownPreview"/> must be collapsed, not
    /// just covered.</b> <c>WebView2</c> (like any HWND-backed/"airspace" control hosted in WPF) is
    /// composited by the OS above the WPF render surface, not through WPF's own visual z-order - a
    /// WPF sibling declared after it in XAML (<see cref="FileViewerHost"/>/<see cref="DiffViewerHost"/>)
    /// does not actually paint over it just because it comes later in the tree or has
    /// <c>Visibility="Visible"</c> while the WebView2 stays visible too. Confirmed as the root cause
    /// of a reported bug: opening a FILES/GIT read-only tab created the tab correctly, but panel D
    /// kept showing whatever the terminal was last displaying (or a blank one) - collapsing
    /// <see cref="Terminal"/> itself (not merely overlaying it) is the only thing that actually hides
    /// a WebView2's native window. <see cref="MarkdownPreviewHost"/>'s own <see cref="MarkdownPreview"/>
    /// is a second, independent WebView2 control, so the exact same rule applies to it.</para>
    /// </summary>
    private void ShowTerminalPane()
    {
        // Leaving the editor pane: remember the caret/scroll of whatever buffer it was showing (see
        // CaptureFileEditorViewState). The three "leave" helpers each do this; ShowFileViewerPane
        // deliberately does not - it is only ever reached after the incoming buffer is already active.
        CaptureFileEditorViewState();
        FileViewerHost.Visibility = Visibility.Collapsed;
        DiffViewerHost.Visibility = Visibility.Collapsed;
        MarkdownPreviewHost.Visibility = Visibility.Collapsed;
        Terminal.Visibility = Visibility.Visible;
    }

    /// <summary>See <see cref="ShowTerminalPane"/>'s remarks.</summary>
    private void ShowFileViewerPane()
    {
        Terminal.Visibility = Visibility.Collapsed;
        DiffViewerHost.Visibility = Visibility.Collapsed;
        MarkdownPreviewHost.Visibility = Visibility.Collapsed;
        FileViewerHost.Visibility = Visibility.Visible;
    }

    /// <summary>See <see cref="ShowTerminalPane"/>'s remarks.</summary>
    private void ShowDiffViewerPane()
    {
        // See ShowTerminalPane.
        CaptureFileEditorViewState();
        Terminal.Visibility = Visibility.Collapsed;
        FileViewerHost.Visibility = Visibility.Collapsed;
        MarkdownPreviewHost.Visibility = Visibility.Collapsed;
        DiffViewerHost.Visibility = Visibility.Visible;
    }

    /// <summary>See <see cref="ShowTerminalPane"/>'s remarks.</summary>
    private void ShowMarkdownPreviewPane()
    {
        // See ShowTerminalPane - toggling to preview must not lose the caret/scroll the user had in
        // the very same file's text view (nor, T10 aside, its buffer: that stays in _fileEditBuffers).
        CaptureFileEditorViewState();
        Terminal.Visibility = Visibility.Collapsed;
        FileViewerHost.Visibility = Visibility.Collapsed;
        DiffViewerHost.Visibility = Visibility.Collapsed;
        MarkdownPreviewHost.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// A plain disk read for a <see cref="TabKind.File"/> tab, or for a <see cref="TabKind.GitChange"/>
    /// tab whose working-tree copy is still there (Added/Untracked). Falls back to
    /// <see cref="GitStatusBuilder.ReadCommittedContent"/> (off the UI thread - it shells out to
    /// `git`) only when the tab carries git coordinates (<see cref="TabViewModel.GitRepoRootPath"/>/
    /// <see cref="TabViewModel.GitRelativePath"/>) <b>and</b> the disk path is actually missing - a
    /// Deleted entry's whole reason for needing this fallback at all.
    /// </summary>
    private static Task<string> ReadTabContentAsync(TabViewModel tab)
    {
        if (!File.Exists(tab.TabId) && tab.GitRepoRootPath is { } repoRootPath && tab.GitRelativePath is { } relativePath)
        {
            return ReadGitObjectAsync(repoRootPath, $"HEAD:{relativePath}");
        }

        return File.ReadAllTextAsync(tab.TabId);
    }

    /// <summary>Reads one side of a <see cref="TabKind.GitChange"/> diff tab's comparison - see
    /// <see cref="GitDiffSide"/> for what each value means.</summary>
    private static Task<string> ReadGitDiffSideAsync(TabViewModel tab, GitDiffSide side)
    {
        if (side == GitDiffSide.WorkingTree)
        {
            return File.ReadAllTextAsync(tab.TabId);
        }

        string repoRootPath = tab.GitRepoRootPath ?? throw new InvalidOperationException("A git diff tab must carry a repo root.");
        string relativePath = tab.GitRelativePath ?? throw new InvalidOperationException("A git diff tab must carry a relative path.");
        string gitObjectSpec = side == GitDiffSide.Index ? $":{relativePath}" : $"HEAD:{relativePath}";

        return ReadGitObjectAsync(repoRootPath, gitObjectSpec);
    }

    /// <summary>Runs <see cref="GitStatusBuilder.ReadGitObject"/> off the UI thread (it shells out to
    /// `git`), throwing with a descriptive message on failure rather than returning
    /// <see langword="null"/> - both <see cref="ReadTabContentAsync"/> and
    /// <see cref="ReadGitDiffSideAsync"/> already run inside a try/catch that turns any exception into
    /// on-screen text instead of crashing the UI thread.</summary>
    private static Task<string> ReadGitObjectAsync(string repoRootPath, string gitObjectSpec) => Task.Run(() =>
    {
        string? content = GitStatusBuilder.ReadGitObject(repoRootPath, gitObjectSpec);
        if (content is null)
        {
            throw new InvalidOperationException($"git show {gitObjectSpec} failed - the object may not exist at that revision.");
        }

        return content;
    });

    /// <summary>Normalizes line endings to <c>'\n'</c> - shared by every reader of a file/diff tab's
    /// content, so the line counts fed to <see cref="SetLineNumbers"/> and the arrays fed to
    /// <see cref="ComputeDiffMarks"/> agree exactly with what <see cref="BuildHighlightedDocument"/>
    /// renders (its own normalization below is therefore a no-op on already-normalized input).</summary>
    private static string NormalizeLineEndings(string content) => content.Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>Fills a line-number gutter <see cref="TextBlock"/> with <c>1..lineCount</c>, one number
    /// per line - see <see cref="DiffOldLineNumbers"/>/<see cref="DiffNewLineNumbers"/>'s XAML remarks
    /// for how it then stays visually aligned with its paired <see cref="RichTextBox"/> as that box
    /// scrolls. Only the diff viewer still needs this; the single-pane file view's gutter is
    /// AvalonEdit's own (<c>FileEditor.ShowLineNumbers</c>).</summary>
    private static void SetLineNumbers(TextBlock gutter, int lineCount)
    {
        gutter.Text = string.Join('\n', Enumerable.Range(1, lineCount));
    }

    /// <summary>Snaps a just-repopulated pane and its paired line-number gutter back to the top - a
    /// leftover scroll offset from whatever tab was open before would otherwise leave the newly loaded
    /// content (and, since the gutter's offset is only ever pushed by <c>ScrollChanged</c>, the gutter
    /// too) scrolled past its own start.</summary>
    private static void ResetScroll(RichTextBox pane, TranslateTransform gutterTransform)
    {
        pane.ScrollToVerticalOffset(0);
        gutterTransform.Y = 0;
    }

    /// <summary>Keeps the "Before" pane's gutter in lock-step with <see cref="DiffOldText"/>'s scroll via
    /// a <see cref="TranslateTransform"/> rather than a second <see cref="ScrollViewer"/>: the gutter is
    /// plain text with no scrolling concerns of its own, so mirroring the RichTextBox's own vertical
    /// offset pixel-for-pixel keeps it aligned without a second scroll position to ever drift out of
    /// sync. Also drives <see cref="DiffNewText"/> to the same offset (the TODO's "synchronize the
    /// scroll between the two panes") - guarded by <see cref="_isSyncingDiffScroll"/> so that mirrored
    /// scroll doesn't bounce back and re-drive this side.</summary>
    private void DiffOldText_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        DiffOldLineNumbersTransform.Y = -e.VerticalOffset;
        SyncDiffNewScroll(e.VerticalOffset);
    }

    /// <summary>The "old to new" half of <see cref="DiffOldText_ScrollChanged"/>'s sync, generalized
    /// over which control is currently showing the "After" side - the read-only
    /// <see cref="DiffNewText"/> <see cref="RichTextBox"/>, or the editable <see cref="DiffNewEditor"/>
    /// AvalonEdit control (see <see cref="ShowGitDiffTabAsync"/>'s remarks for when each is shown).
    /// Exactly one of the two is ever visible for a given diff tab.</summary>
    private void SyncDiffNewScroll(double verticalOffset)
    {
        if (_isSyncingDiffScroll)
        {
            return;
        }

        _isSyncingDiffScroll = true;
        try
        {
            if (DiffNewEditor.Visibility == Visibility.Visible)
            {
                DiffNewEditor.ScrollToVerticalOffset(verticalOffset);
            }
            else
            {
                DiffNewText.ScrollToVerticalOffset(verticalOffset);
            }
        }
        finally
        {
            _isSyncingDiffScroll = false;
        }
    }

    /// <summary>See <see cref="DiffOldText_ScrollChanged"/>'s remarks - the "After" pane's half of the
    /// same two-way sync.</summary>
    private void DiffNewText_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        DiffNewLineNumbersTransform.Y = -e.VerticalOffset;
        SyncDiffScroll(DiffOldText, e.VerticalOffset);
    }

    /// <summary>
    /// <see langword="false"/> outside of a sync in progress; briefly <see langword="true"/> while one
    /// pane's <c>ScrollChanged</c> handler is driving the other pane's offset, so that pane's own
    /// resulting <c>ScrollChanged</c> (if it fires at all - WPF only raises it when the offset actually
    /// changes) does not immediately try to drive the first pane again.
    /// </summary>
    private bool _isSyncingDiffScroll;

    /// <summary>See <see cref="_isSyncingDiffScroll"/>'s remarks.</summary>
    private void SyncDiffScroll(RichTextBox target, double verticalOffset)
    {
        if (_isSyncingDiffScroll)
        {
            return;
        }

        _isSyncingDiffScroll = true;
        try
        {
            target.ScrollToVerticalOffset(verticalOffset);
        }
        finally
        {
            _isSyncingDiffScroll = false;
        }
    }

    /// <summary>One line-level change for <see cref="ComputeDiffMarks"/>'s overview-ruler marks: an
    /// added line (present only in the "After" side) is green, a point where one or more lines were
    /// removed (present only in the "Before" side) is red, anchored at the "After"-side row it would
    /// have preceded.</summary>
    private readonly record struct DiffMark(int NewLineIndex, bool IsAdded);

    /// <summary>Diff marks for the currently-shown git-diff tab's "After" pane overview ruler (see
    /// <see cref="DiffMarkStrip"/>'s XAML remarks) - repopulated by <see cref="ShowGitDiffTabAsync"/>,
    /// re-rendered by <see cref="RenderDiffMarkStrip"/> whenever the strip itself resizes.</summary>
    private List<DiffMark> _diffMarks = new();

    /// <summary>Total "After"-side line count backing <see cref="_diffMarks"/>'s row-to-pixel scaling in
    /// <see cref="RenderDiffMarkStrip"/>.</summary>
    private int _diffMarkTotalLines;

    /// <summary>
    /// Line-level diff between <paramref name="oldLines"/> and <paramref name="newLines"/> (already
    /// normalized/split by <see cref="ShowGitDiffTabAsync"/>), classified into <see cref="DiffMark"/>s
    /// anchored to "After"-side row indices - via the classic dynamic-programming LCS backtrack, since
    /// no diff library is referenced anywhere in this codebase. O(N*M) time/space, so it is skipped
    /// outright above a size cap rather than risking a multi-second UI-thread stall on a huge file - a
    /// git-diffable text file is overwhelmingly source-sized, not data-dump-sized, so the cap should
    /// essentially never bite in practice.
    /// </summary>
    private static List<DiffMark> ComputeDiffMarks(string[] oldLines, string[] newLines, out HashSet<int> removedOldLines, out HashSet<int> addedNewLines)
    {
        removedOldLines = new HashSet<int>();
        addedNewLines = new HashSet<int>();

        int n = oldLines.Length;
        int m = newLines.Length;
        if ((long)n * m > 4_000_000)
        {
            return new List<DiffMark>();
        }

        var lengths = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lengths[i, j] = oldLines[i] == newLines[j]
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var marks = new List<DiffMark>();
        int a = 0, b = 0, lastDeletedAtRow = -1;
        while (a < n && b < m)
        {
            if (oldLines[a] == newLines[b])
            {
                a++;
                b++;
            }
            else if (lengths[a + 1, b] >= lengths[a, b + 1])
            {
                if (lastDeletedAtRow != b)
                {
                    marks.Add(new DiffMark(b, IsAdded: false));
                    lastDeletedAtRow = b;
                }

                removedOldLines.Add(a);
                a++;
            }
            else
            {
                marks.Add(new DiffMark(b, IsAdded: true));
                addedNewLines.Add(b);
                b++;
            }
        }

        if (a < n && lastDeletedAtRow != b)
        {
            marks.Add(new DiffMark(b, IsAdded: false));
        }

        while (a < n)
        {
            removedOldLines.Add(a);
            a++;
        }

        while (b < m)
        {
            marks.Add(new DiffMark(b, IsAdded: true));
            addedNewLines.Add(b);
            b++;
        }

        return marks;
    }

    /// <summary>Draws <see cref="_diffMarks"/> onto <see cref="DiffMarkStrip"/>, scaling each mark's
    /// "After"-row index to the strip's current pixel height - re-run from <see cref="ShowGitDiffTabAsync"/>
    /// and from <see cref="DiffMarkStrip_SizeChanged"/>, since panel D can be resized (or the app window
    /// itself) after a diff tab is already showing.</summary>
    private void RenderDiffMarkStrip()
    {
        DiffMarkStrip.Children.Clear();

        double height = DiffMarkStrip.ActualHeight;
        double width = DiffMarkStrip.ActualWidth;
        if (height <= 0 || width <= 0 || _diffMarkTotalLines <= 0 || _diffMarks.Count == 0)
        {
            return;
        }

        const double markThickness = 2.0;
        foreach (var mark in _diffMarks)
        {
            var rect = new Rectangle
            {
                Width = width,
                Height = markThickness,
                Fill = (Brush)FindResource(mark.IsAdded ? "SuccessBrush" : "DangerBrush"),
            };

            double top = height * mark.NewLineIndex / _diffMarkTotalLines;
            Canvas.SetTop(rect, Math.Min(top, Math.Max(0, height - markThickness)));
            Canvas.SetLeft(rect, 0);
            DiffMarkStrip.Children.Add(rect);
        }
    }

    /// <summary>See <see cref="RenderDiffMarkStrip"/>'s remarks.</summary>
    private void DiffMarkStrip_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderDiffMarkStrip();
    }

    /// <summary>
    /// Tokenizes and colours <paramref name="content"/> (see <see cref="SyntaxHighlighter.Tokenize"/>)
    /// into a <see cref="FlowDocument"/> ready to assign to a <see cref="RichTextBox.Document"/> -
    /// shared by the single-pane file viewer and both panes of the side-by-side diff viewer.
    /// Line endings are normalized to <c>'\n'</c> first: <see cref="SyntaxHighlighter"/>'s multiline
    /// patterns anchor on it alone (<c>(?m:^...$)</c>), and <see cref="AppendToken"/> splits tokens on
    /// it too - a stray <c>'\r'</c> left in either path would either break those anchors or paint as a
    /// visible stray glyph.
    /// </summary>
    private FlowDocument BuildHighlightedDocument(string content, SourceLanguage language, Func<int, Brush?>? lineBackground = null)
    {
        content = content.Replace("\r\n", "\n").Replace("\r", "\n");

        var paragraph = new Paragraph { Margin = new Thickness(0) };
        int lineIndex = 0;
        foreach (var token in SyntaxHighlighter.Tokenize(content, language))
        {
            lineIndex = AppendToken(paragraph, token, lineIndex, lineBackground);
        }

        // PageWidth genuinely caps where a line wraps within the document (unlike the RichTextBox's
        // own ActualWidth, which only governs the visible viewport/horizontal scrollbar) - 4000 was
        // wide enough for ordinary source lines but not for a long, never-hard-wrapped markdown
        // paragraph, which would silently wrap inside the FlowDocument while the caller's own line
        // count (SetLineNumbers) still counted it as one line, permanently desyncing every line number
        // below it. A line this
        // long is essentially unreachable in practice, so there is no real downside to sizing for it.
        return new FlowDocument(paragraph) { PageWidth = 1_000_000 };
    }

    /// <summary>
    /// Appends one <see cref="SyntaxToken"/> to <paramref name="paragraph"/> as a coloured
    /// <see cref="Run"/> (or the viewer's default foreground, for a <see langword="null"/>
    /// <see cref="SyntaxToken.ColorHex"/>) - split on <c>'\n'</c> into an explicit
    /// <see cref="LineBreak"/> per line, since WPF's text layout does not itself treat an embedded
    /// <c>'\n'</c> inside a <see cref="Run"/>'s text as a line break.
    /// </summary>
    private int AppendToken(Paragraph paragraph, SyntaxToken token, int lineIndex, Func<int, Brush?>? lineBackground)
    {
        var brush = token.ColorHex is null ? null : GetSyntaxBrush(token.ColorHex);
        string[] lines = token.Text.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > 0)
            {
                var run = new Run(lines[i]);
                if (brush is not null)
                {
                    run.Foreground = brush;
                }

                var background = lineBackground?.Invoke(lineIndex);
                if (background is not null)
                {
                    run.Background = background;
                }

                paragraph.Inlines.Add(run);
            }

            if (i < lines.Length - 1)
            {
                paragraph.Inlines.Add(new LineBreak());
                lineIndex++;
            }
        }

        return lineIndex;
    }

    /// <summary>The hex-to-frozen-brush cache lives in <see cref="SyntaxBrushCache"/> so this path and
    /// <see cref="SyntaxColorizer"/> share one set of brushes for one palette - see that class's
    /// remarks.</summary>
    private static SolidColorBrush GetSyntaxBrush(string colorHex) => SyntaxBrushCache.Get(colorHex);

    /// <summary>
    /// Panel A's session-row double-click gesture: a sleeping (not <see cref="RootsPanelNodeViewModel.IsRunning"/>)
    /// session attaches - i.e. resumes in place, exactly <see cref="ResumeSession_Click"/>'s action, including
    /// its "already open? select instead" guard - while an active session stops, exactly
    /// <see cref="TabItem_MouseDoubleClick"/>'s confirm-then-stop gesture on the strip. Root/Agent/Placeholder
    /// rows fall through untouched so the TreeView's own default double-click-to-expand keeps working there.
    /// </summary>
    private void SessionRow_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as TreeViewItem)?.DataContext is not RootsPanelNodeViewModel node || node.Kind != RootsPanelNodeKind.Session)
        {
            return;
        }

        e.Handled = true;

        if (!node.IsRunning)
        {
            ResumeSessionCore(node, fork: false);
            return;
        }

        var tab = Tabs?.Tabs.FirstOrDefault(t => t.TabId == node.Key);
        if (tab is null || tab.HasEnded)
        {
            return;
        }

        bool confirmedStop = AccelMessageDialog.ShowConfirm(
            this,
            $"Stop \"{tab.Title}\"? The session's process will be terminated. The tab stays open so you can still see its output.",
            "Stop session",
            AccelDialogIcon.Warning);

        if (!confirmedStop)
        {
            return;
        }

        Tabs?.StopTabCommand.Execute(tab);
    }

    /// <summary>
    /// P4-T3/T3b's UI surface: removes a session's on-disk data via <see cref="SessionRemover.Plan"/> +
    /// <see cref="SessionRemoverExecutor.Execute"/>, recycle-bin only - there is no UI path to
    /// <see cref="SessionRemovalMode.PermanentDelete"/> from this menu.
    ///
    /// <para><b>Fails closed while the session is live</b>, checked twice: once here before even
    /// building a plan or showing the confirmation dialog (via <paramref name="sender"/>'s row -
    /// <see cref="RootsPanelNodeViewModel.IsRunning"/>, the disk-derived signal, OR-ed with whether this
    /// Accel instance has an open tab for it), and again by <see cref="SessionRemoverExecutor.Execute"/>'s
    /// own <c>isSessionLive</c> re-checks immediately before every delete. Neither check can see a
    /// `claude` process resuming this session id from a source entirely outside this Accel instance
    /// (a different terminal, a different machine) - the disk-derived <c>IsRunning</c> signal only
    /// updates on <see cref="Accel.App.Services.ITelemetryFeed"/>'s next tick, so there is an inherent,
    /// small window this cannot close; it is not claimed to be airtight, only fail-closed against
    /// everything this app can actually observe.</para>
    /// </summary>
    private async void RemoveSession_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not RootsPanelNodeViewModel node || node.Kind != RootsPanelNodeKind.Session)
        {
            return;
        }

        string sessionId = node.Key;
        Func<bool> isLive = () => node.IsRunning || (_sessionRegistry?.TryGet(sessionId, out _) ?? false);

        if (isLive())
        {
            AccelMessageDialog.ShowMessage(
                this,
                "This session is running. Stop it first, then remove it.",
                "Remove session",
                AccelDialogIcon.Info);
            return;
        }

        var plan = SessionRemover.Plan(sessionId, node.ProjectDir);
        if (!plan.IsSafe)
        {
            AccelMessageDialog.ShowMessage(
                this,
                "Refusing to remove this session - it did not pass validation:\n\n" + string.Join('\n', plan.Warnings),
                "Remove session",
                AccelDialogIcon.Error);
            return;
        }

        int existingCount = plan.ExistingTargets.Count();
        if (existingCount == 0 && !plan.HistoryFileExists)
        {
            AccelMessageDialog.ShowMessage(this, "There is nothing on disk to remove for this session.", "Remove session",
                AccelDialogIcon.Info);
            return;
        }

        double totalMegabytes = plan.TotalBytes / (1024.0 * 1024.0);
        bool confirmed = AccelMessageDialog.ShowConfirm(
            this,
            $"Remove \"{node.Text}\"?\n\n" +
            $"This moves {existingCount} location(s) (~{totalMegabytes:0.0} MB) to the recycle bin and removes " +
            "this session's entry from history.jsonl. The tab strip and panel A are unaffected if the session " +
            "is not currently open.\n\nThis can be undone from the recycle bin, but is otherwise permanent.",
            "Remove session",
            AccelDialogIcon.Warning);

        if (!confirmed)
        {
            return;
        }

        var result = await Task.Run(() => SessionRemoverExecutor.Execute(plan, isLive, SessionRemovalMode.RecycleBin));

        if (result.FullyRemoved)
        {
            RootsPanel?.RefreshCommand.Execute(null);
            ShowTransientWarning($"\"{node.Text}\" was removed.");
            return;
        }

        if (result.AbortedForLiveness)
        {
            AccelMessageDialog.ShowMessage(this, "The session became active again during removal, so nothing further was touched.",
                "Remove session", AccelDialogIcon.Info);
            return;
        }

        var failed = result.Steps.Where(s => s.Outcome == SessionRemovalStepOutcome.Failed).ToArray();
        string detail = failed.Length > 0
            ? string.Join('\n', failed.Select(s => $"- {s.Description}: {s.Detail}"))
            : $"Aborted: {result.AbortReason}";
        AccelMessageDialog.ShowMessage(this, "Removal did not fully complete:\n\n" + detail, "Remove session",
            AccelDialogIcon.Error);
    }

    private DispatcherTimer? _transientWarningTimer;

    /// <summary>
    /// Shows <paramref name="text"/> in the non-modal banner beneath the menu bar, auto-hiding it again
    /// after a few seconds. Never blocks the caller and never stacks timers - a second call while one is
    /// already showing just restarts the clock with the new text.
    /// </summary>
    private void ShowTransientWarning(string text)
    {
        _transientWarningTimer?.Stop();

        TransientWarningText.Text = text;
        TransientWarningBanner.Visibility = Visibility.Visible;

        _transientWarningTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _transientWarningTimer.Tick += (_, _) =>
        {
            _transientWarningTimer?.Stop();
            TransientWarningBanner.Visibility = Visibility.Collapsed;
        };
        _transientWarningTimer.Start();
    }
}

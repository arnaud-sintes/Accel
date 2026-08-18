namespace Accel.App;

using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Accel.App.Services;
using Accel.App.ViewModels;
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
        }

        if (filesPanel is not null)
        {
            // Scoped to panel B's file-tree section only, never Window.DataContext or all of
            // PanelB, per the rule quoted above - GitSectionRoot below gets its own, independent
            // DataContext.
            FilesPanelVm = filesPanel;
            FilesSectionRoot.DataContext = filesPanel;
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

        Closed += (_, _) =>
        {
            // No session teardown here any more: PtyRegistry is the single owner of PtySession.Dispose
            // (P3-T2) and app-exit teardown is P3-T4's job (CloseAllAsync/Dispose around the app loop).
            // This only drops the tab strip's registry subscription plus panel B's own; AgentGraphViewModel
            // is disposed by Program.cs's composition root (mirrors how rootsPanel is never disposed here).
            Tabs?.Dispose();
            _panelBStub?.Dispose();
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

    /// <summary>Synchronously repaints the entire window (RDW_UPDATENOW), including its full frame
    /// (RDW_FRAME) and every child (RDW_ALLCHILDREN) - a hard guarantee against any stale/leftover
    /// bitmap surviving an activation change, regardless of the precise Win32 message-level cause.</summary>
    private void ForceFullWindowRedraw()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        const uint RDW_INVALIDATE = 0x0001;
        const uint RDW_ERASE = 0x0004;
        const uint RDW_FRAME = 0x0400;
        const uint RDW_ALLCHILDREN = 0x0080;
        const uint RDW_UPDATENOW = 0x0100;

        NativeMethods.RedrawWindow(
            _windowHandle,
            IntPtr.Zero,
            IntPtr.Zero,
            RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW);
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

        switch (msg)
        {
            case WM_GETMINMAXINFO:
                handled = ConstrainMaximizedSizeToWorkArea(hwnd, lParam);
                return IntPtr.Zero;
            case WM_NCCALCSIZE:
                return ClampMaximizedClientRectToWorkArea(hwnd, wParam, lParam, ref handled);
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
        initialWorkingDirectory ??= RootsPanel?.SelectedRootPath ?? RootsPanel?.FirstAvailableRootPath;
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

        string? workingDirectory = RootsPanel?.RootPathFor(sessionId) ?? RootsPanel?.FirstAvailableRootPath;

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
        if ((sender as ListBoxItem)?.DataContext is not TabViewModel tab || tab.HasEnded)
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

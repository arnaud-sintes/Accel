// P2-T5b: Accel's own glue script (NOT vendored - see THIRD_PARTY_NOTICES.txt for what is).
// Wires xterm.js to a live PtySession over /pty/{tabId}. Must match Server/PtyRoutes.cs's framing
// convention exactly:
//   - server -> client is ALWAYS a WebSocket TEXT frame carrying decoded UTF-8 text
//     (PtySession.Output already did the one-time, stateful UTF-8 decode - PtyRoutes just
//     re-encodes it losslessly as UTF-8 bytes for the frame).
//   - client -> server BINARY frames are raw input bytes, written verbatim to the pty. This is
//     load-bearing for control bytes: Ctrl+C is the single byte 0x03, which is not valid UTF-8 on
//     its own - if it were routed through a text frame/JS string round trip it would corrupt or
//     get replaced. TextEncoder().encode() still produces the byte 0x03 correctly (UTF-8 is
//     byte-identical to ASCII/C0 control codes below 0x80); what matters is the FRAME TYPE, not
//     the encoder.
//   - client -> server TEXT frames are the one JSON control message, {"resize":[cols,rows]}.
(function () {
  "use strict";

  var term = null;
  var fitAddon = null;
  var socket = null;
  var resizeObserver = null;
  var webglActive = false;

  // Diagnostic accumulator, read back via CoreWebView2.ExecuteScriptAsync by
  // Program.cs's `terminal-e2e-smoke-test` verb to prove real bytes from a real child arrived
  // over the wire. Not used by any production code path.
  window.accelReceivedText = "";

  function createTerminal() {
    term = new Terminal({
      convertEol: true,

      // ConPTY is the backend on every target (locked-in decision 1). This was originally
      // `windowsMode: true`, which forces xterm's LEGACY ConPTY workarounds on unconditionally: a
      // line-feed heuristic that guesses which lines are wrapped, plus reflow disabled outright
      // (xterm's Buffer._isReflowEnabled is `!windowsMode`). Those workarounds exist for the
      // pre-21376 ConPTY that could not report wrapping, and applying them to a modern ConPTY makes
      // a full-screen app's repaint interleave old and new glyphs (see this task's report for the
      // reproduction). `windowsPty` is the option xterm added to replace `windowsMode` precisely so
      // the host can state the backend and build number and let xterm decide - which is what
      // TerminalView's injected accelConPtyBuildNumber supplies. The `windowsMode` fallback below
      // keeps the old behaviour if the host ever fails to inject it, rather than silently dropping
      // the workarounds on a machine that may genuinely need them (Accel supports Windows 10 1809+,
      // i.e. builds well below 21376).
      windowsPty: typeof window.accelConPtyBuildNumber === "number"
        ? { backend: "conpty", buildNumber: window.accelConPtyBuildNumber }
        : {},
      windowsMode: typeof window.accelConPtyBuildNumber !== "number",

      // Integer cell metrics (plan risk register item 4, explicit callout): fontSize is a whole
      // number and lineHeight is the default 1 (a multiplier, not a pixel value). letterSpacing
      // starts at 0 here but is NOT the final value - see snapCellWidthToIntegerPixels() below.
      // Measured on this dev machine (Cascadia Mono, 14px, 125% Windows display scaling): row
      // height already comes out to an exact 16px with these settings, but the *glyph advance
      // width* xterm measures for a monospace font at an arbitrary font size is not guaranteed to
      // land on a whole CSS pixel (measured here: 8.20754716981132px) - that is what produces the
      // smearing/misalignment the plan warns about, and no combination of integer fontSize alone
      // fixes it, because the browser's font-metrics measurement is sub-pixel-accurate regardless
      // of the font size being a whole number. See the task report for the real numbers read back
      // off a live instance via window.accelCellMetrics(), before and after the correction below.
      fontSize: 14,
      lineHeight: 1,
      letterSpacing: 0,

      // Cascadia Mono, confirmed installed on this dev machine (System.Drawing.Text.
      // InstalledFontCollection query - see task report). Consolas is the documented fallback: it
      // ships with Windows itself, so a target machine without Cascadia Mono still gets a
      // monospace font with integer metrics rather than silently falling back to a proportional
      // browser-default font.
      fontFamily: "Cascadia Mono, Consolas, monospace",

      // Reported bug: ordinary (non-bold, non-SGR-1) terminal output rendered visibly bold
      // throughout the whole session. It is NOT an attribute/CSS-plumbing bug, and it is NOT the
      // CLI marking everything bold - both were ruled out by measurement against a live instance:
      //   - xterm 5.5.0's DOM renderer's injected style block was read back off the live page and
      //     was already correct ("span:not(.xterm-bold) { font-weight: 400 }" /
      //     "span.xterm-bold { font-weight: 700 }"), and getComputedStyle on real rendered rows
      //     confirmed ordinary output really did compute to 400 - so pinning fontWeight to 400
      //     (which is exactly what xterm's own "normal" default already resolves to) was a no-op,
      //     which is why it produced no visible change at all;
      //   - a real `claude` child captured through a real ConPTY emitted SGR 1 for only ~6% of its
      //     visible glyphs, so "the CLI bolds everything" was wrong too - the plain, attribute-free
      //     output of even `cmd.exe` looked equally heavy.
      // The actual cause is the pinned FONT, not the weight option: measured on a live page by
      // rasterizing the same string to a canvas per weight and summing alpha coverage ("ink"),
      // Cascadia Mono's Regular (400) instance renders HEAVIER in Chromium/WebView2 than Consolas
      // *Bold* does at the same 14px (ink 196803 vs 194596), and ~21% heavier than Consolas Regular
      // (162136). So weight-400 output is legitimately bold-looking; nothing about the bold
      // attribute path was ever broken. Cascadia Mono is a variable font whose wght axis Chromium
      // does honour here (measured ink rises monotonically 200 -> 700), so the fix is to render
      // ordinary output at its genuine Light instance, 300 (ink 155123 - i.e. the same ink density
      // as Consolas Regular, the conventional Windows console look), and to keep SGR-1 bold clearly
      // heavier at 600 (ink 225080, a 1.45x jump). Cell metrics are unaffected: the measured advance
      // width is identical (237.89px for the same probe string) at every weight on this axis, so
      // snapCellWidthToIntegerPixels() below still lands on the same integer cell.
      fontWeight: "300",
      fontWeightBold: "600",

      // Matches App/Theme.xaml's dark palette exactly - base black #0A0A0A (BackgroundBaseColor),
      // near-white #F2F2F2 (TextPrimaryColor), pastel-orange caret #F0A868 (AccentColor) and a
      // 25%-alpha teal-blue selection #6EC1D6 (TealTintStrongColor) - so the terminal is the same
      // design system as the WPF chrome around it, not a separate one.
      // so there's no visible seam between the WPF chrome and panel D's terminal surface - xterm's
      // own default theme (see xterm.css) would otherwise not match the rest of the window exactly.
      theme: {
        background: "#0a0a0a",
        foreground: "#f2f2f2",
        cursor: "#f0a868",
        cursorAccent: "#0a0a0a",
        selectionBackground: "#6ec1d640",
      },
    });

    fitAddon = new FitAddon.FitAddon();
    term.loadAddon(fitAddon);
    term.open(document.getElementById("term"));

    // Must come after term.open() (the WebGL addon needs the element/renderer to exist) and
    // before the first fit/snap below, so cell metrics are measured against the renderer that
    // will actually paint.
    activateWebglRenderer();

    fitAddon.fit();

    // Runtime correction, done once the renderer has actually measured the real font (it cannot
    // be computed ahead of time - it depends on the font actually installed and resolved on this
    // machine, per the fontFamily fallback chain above): nudge letterSpacing up by exactly the
    // fractional remainder of the measured cell width, rounding the *total* advance width (glyph
    // + letterSpacing) up to the next whole CSS pixel. This is the standard fix for xterm's
    // sub-pixel monospace measurement, not a fontSize/letterSpacing guess - see
    // window.accelCellMetrics() for how this is verified to actually work, with real numbers,
    // rather than assumed.
    snapCellWidthToIntegerPixels();

    // The one onData handler for real user input. xterm.js already produces the correct raw
    // character for every key the user types, including control sequences - Ctrl+C arrives here
    // as the single character U+0003 - so this forwards verbatim with no per-key special
    // casing. window.accelSimulateInput (below) calls this exact function too, so the Ctrl+C
    // smoke-test check exercises the real production path, not a look-alike.
    term.onData(handleTerminalData);

    // xterm.js has no built-in notion of "Shift+Enter = soft newline" - it maps every Enter to a
    // plain CR ("\r") regardless of modifiers, since that distinction is an app/CLI convention, not
    // a terminal-emulator one. The Claude Code CLI (Ink-based) expects the conventional ESC-CR
    // ("\x1b\r") sequence to tell a soft newline apart from a submit, so intercept Shift+Enter here
    // and send that instead, before xterm's own default handling emits a plain "\r" for it too.
    term.attachCustomKeyEventHandler(function (event) {
      // Returning false only tells xterm's OWN keydown handler to skip its default terminal-sequence
      // processing for this key - it does NOT call event.preventDefault(), so without doing that
      // ourselves the browser's native default action (e.g. inserting a real newline / firing its
      // own native paste on xterm's hidden textarea) still runs too, on top of whatever we send
      // below - the terminal ends up processing the key twice. Found empirically: Shift+Enter was
      // sending "\x1b\r" here AND a plain "\r" from the untouched native default, and Ctrl+V was
      // pasting via the clipboard read below AND via xterm's own native textarea paste listener -
      // both looked like the terminal "doing it twice".
      if (event.type === "keydown" && event.key === "Enter" && event.shiftKey) {
        event.preventDefault();
        handleTerminalData("\x1b\r");
        return false;
      }

      // Ctrl+C is the terminal's own SIGINT byte (0x03) by default, which pre-empts the usual
      // browser "copy selection" convention every other app follows - so only steal it for copy
      // when there is actually a selection to copy, and fall through to the normal SIGINT send
      // (return true, no handling here) otherwise. Matches Windows Terminal/most modern terminal
      // emulators' own Ctrl+C convention.
      if (event.type === "keydown" && event.ctrlKey && !event.shiftKey && !event.altKey && event.key === "c" && term.hasSelection()) {
        event.preventDefault();
        copySelection();
        return false;
      }

      // Ctrl+V has no terminal meaning of its own (real terminals have no "paste" control byte),
      // so unlike Ctrl+C above this is always safe to intercept unconditionally.
      if (event.type === "keydown" && event.ctrlKey && !event.shiftKey && !event.altKey && event.key === "v") {
        event.preventDefault();
        pasteFromClipboard();
        return false;
      }

      return true;
    });

    // FitAddon -> resize-over-the-wire: observe the terminal container itself (not `window`,
    // which only fires on the whole WebView2 control's own size changing and would miss e.g. a
    // GridSplitter drag that resizes panel D without resizing the window) so every real layout
    // change re-fits and re-sends {"resize":[cols,rows]}.
    resizeObserver = new ResizeObserver(function () {
      if (fitAddon) {
        fitAddon.fit();
      }
      sendResize();
    });
    resizeObserver.observe(document.getElementById("term"));
  }

  // Reported bug: scrolling Claude Code's transcript left a "remanence" of previous content -
  // stale glyph pixels (notably the first-column `⏺`/`●` bullets) persisting over areas that
  // should be blank/black after the scroll. Root cause class: with no renderer addon loaded,
  // xterm falls back to its DOM renderer, where erasing old glyphs is delegated entirely to
  // Chromium's paint invalidation - and two things here defeat it: the fractional letter-spacing
  // snapCellWidthToIntegerPixels() deliberately sets (sub-pixel-positioned text on composited
  // layers is a known Chromium invalidation weak spot), and glyphs whose ink overflows their
  // layout box (those bullets, box drawing) leaving pixels outside the rect Chromium repaints
  // when the row re-renders blank. The WebGL renderer clears and redraws the whole frame every
  // render, so stale-pixel ghosting is structurally impossible (it is also what VS Code ships,
  // and much faster for a heavy TUI). DOM renderer remains the automatic fallback: xterm itself
  // reverts to it whenever the WebGL addon is disposed or fails to construct.
  function activateWebglRenderer() {
    // Guarded on the global existing at all so a missing/failed-to-load addon-webgl.js script
    // degrades to the DOM renderer instead of throwing out of createTerminal().
    if (typeof WebglAddon === "undefined") {
      return;
    }

    try {
      var webglAddon = new WebglAddon.WebglAddon();

      // Per the addon's own README: the browser can revoke the WebGL context at any time (GPU
      // reset, driver update, too many live contexts). Disposing the addon on that event is the
      // documented recovery - xterm transparently falls back to the DOM renderer rather than
      // freezing on the last WebGL frame.
      webglAddon.onContextLoss(function () {
        webglAddon.dispose();
        webglActive = false;
      });

      term.loadAddon(webglAddon);
      webglActive = true;
    } catch (e) {
      // WebGL context creation failed (e.g. GPU/driver blocklisted, remote session without GPU) -
      // stay on the DOM renderer, which is functionally complete, just artifact-prone.
    }
  }

  function snapCellWidthToIntegerPixels() {
    try {
      var dimensions = term._core._renderService.dimensions;
      var measuredWidth = dimensions.css.cell.width;

      // CEIL, not round-to-nearest: measured empirically (see this task's report) that
      // dimensions.css.cell.width has a floor of Math.ceil(rawGlyphWidth) which letterSpacing
      // cannot push below - a negative/rounds-down correction (e.g. Math.round's -0.2075 result
      // for this font/size) measurably does nothing, while any positive letterSpacing up to and
      // including the ceiling's own remainder reliably snaps the cell width to that ceiling.
      var integerWidth = Math.ceil(measuredWidth);
      var delta = integerWidth - measuredWidth;

      // Only adjust if there is a real fractional remainder (guards against floating-point noise
      // right at an already-integer value - Math.ceil of an exact integer equals itself, so delta
      // is exactly 0 there, not close to 1).
      if (delta > 0.01) {
        term.options.letterSpacing = delta;
        fitAddon.fit();
      }
    } catch (e) {
      // term._core._renderService.dimensions is xterm 5.5.0's private renderer surface (the only
      // place these numbers are exposed in this vendored version) - if it is ever unavailable,
      // degrade to whatever letterSpacing:0 measures rather than throwing out of createTerminal().
    }
  }

  // navigator.clipboard.writeText() is allowed for a same-page, user-gesture-triggered call with
  // no permission prompt (unlike readText() below) - see TerminalView.InitializeAsync's
  // PermissionRequested handler for the read side.
  function copySelection() {
    var text = term.getSelection();
    if (text) {
      navigator.clipboard.writeText(text).catch(function () {
        // Best-effort - nothing actionable client-side if the OS clipboard write itself fails.
      });
    }
  }

  function pasteFromClipboard() {
    navigator.clipboard.readText().then(function (text) {
      if (text) {
        // term.paste() (not handleTerminalData directly) so xterm applies its own bracketed-paste
        // wrapping when the child has requested it, exactly as its built-in textarea paste path
        // does - handleTerminalData is still what actually reaches the socket underneath.
        term.paste(text);
      }
    }, function () {
      // Best-effort - e.g. an empty/non-text clipboard, or the permission grant not yet applied.
    });
  }

  function handleTerminalData(data) {
    if (!socket || socket.readyState !== WebSocket.OPEN) {
      return;
    }

    // BINARY frame, raw UTF-8 bytes - never text. See this file's header comment for why frame
    // type (not payload content) is what PtyRoutes.cs switches on.
    var bytes = new TextEncoder().encode(data);
    socket.send(bytes);
  }

  function sendResize() {
    if (!socket || socket.readyState !== WebSocket.OPEN || !term) {
      return;
    }

    // TEXT frame - the one JSON control message PtyRoutes.HandleControlMessage parses. Sending a
    // JS string to WebSocket.send always produces a text frame, never binary.
    socket.send(JSON.stringify({ resize: [term.cols, term.rows] }));
  }

  // Called from C# (TerminalView.AttachPtyAsync) once CoreWebView2 navigation has completed and
  // xterm.js is already initialized - see TerminalView.BuildAttachScript's doc comment for the
  // tabId/port-passing mechanism (P2-T5b's stopgap, ahead of Phase 3's real tab/registry).
  window.accelAttachPty = function (tabId, port) {
    if (!term) {
      createTerminal();
    }

    // Without this, a freshly created (or reattached-to) session leaves keyboard focus wherever
    // it last was in the host WPF app (e.g. panel A's tree, or nowhere) instead of xterm's own
    // hidden input textarea - the terminal renders and the session is live, but the user's first
    // keystrokes go nowhere until they click into panel D themselves.
    term.focus();

    if (socket) {
      try {
        socket.close();
      } catch (e) {
        // Best-effort close of a previous attach.
      }
    }

    window.accelReceivedText = "";

    // Deliberately a REAL loopback address, not the virtual host name: CoreWebView2.
    // SetVirtualHostNameToFolderMapping (see TerminalView.xaml.cs) only intercepts document/
    // subresource GET requests for the static files it maps to a folder - it does not proxy a
    // WebSocket upgrade to an arbitrary local TCP listener, and "accel-terminal" is not a real,
    // DNS- or hosts-file-resolvable host, so "wss://accel-terminal/..." would fail outright. The
    // real EventServer/Kestrel PTY route listens on 127.0.0.1:{port} in plain HTTP (loopback
    // only, per EventServer's own binding - see its class doc), so this is ws://, not wss://:
    // there is no TLS listener to speak TLS to. The browser still sends
    // "Origin: https://accel-terminal" on the upgrade request regardless of the connection
    // target, because Origin reflects the *page's* origin (this script runs on a page navigated
    // to https://accel-terminal/index.html) - exactly what PtyRoutes.ExpectedOrigin checks.
    // Confirmed empirically against the real route by this task's end-to-end smoke test, not
    // assumed - see the task report for what was actually observed.
    //
    // `mySocket` is captured per-attach, and every handler below checks identity against the
    // shared `socket` variable before mutating it. Found necessary empirically
    // (terminal-e2e-smoke-test's check (c), reattaching to a second tabId, caught it): closing
    // the OLD socket above does not make its close event fire synchronously, so by the time it
    // does fire, `socket` has already been reassigned to the NEW one - an identity-unchecked
    // `onclose { socket = null; }` would then null out the *new* socket's reference, not the
    // stale one it actually belongs to, since all handlers close over the same outer variable.
    var mySocket = new WebSocket("ws://127.0.0.1:" + port + "/pty/" + tabId);
    socket = mySocket;
    mySocket.binaryType = "arraybuffer";

    mySocket.onopen = function () {
      if (socket === mySocket) {
        sendResize();
      }
    };

    mySocket.onmessage = function (event) {
      if (socket !== mySocket) {
        return;
      }

      // Server -> client is always a text frame (PtyRoutes.PumpOutputAsync), which the WebSocket
      // API always delivers here as a JS string, never an ArrayBuffer - nothing to decode.
      if (typeof event.data === "string") {
        window.accelReceivedText += event.data;
        term.write(event.data);
      }
    };

    mySocket.onclose = function () {
      if (socket === mySocket) {
        socket = null;
      }
    };

    mySocket.onerror = function () {
      // Cleanup happens in onclose; nothing else actionable client-side today.
    };
  };

  // Called from C# (TerminalView.DetachPtyAsync) when panel C has no tab left to show (the last
  // open session's tab just closed) - closes any live socket and wipes the screen buffer so panel
  // D goes back to a blank black surface instead of freezing on the closed session's last frame.
  window.accelDetachPty = function () {
    if (socket) {
      try {
        socket.close();
      } catch (e) {
        // Best-effort close - the socket is being abandoned either way.
      }
      socket = null;
    }

    if (term) {
      term.reset();
    }
  };

  // Test-only entry point for terminal-e2e-smoke-test's raw-Ctrl+C-byte check. Calls the exact
  // same handler xterm.js's real onData is wired to above - not a separate/special-cased send
  // path - so the check exercises production code, matching the task's explicit requirement not
  // to intercept/special-case Ctrl+C separately from normal typed input.
  window.accelSimulateInput = function (text) {
    handleTerminalData(text);
  };

  // Diagnostics for the integer-cell-metrics check (risk register item 4). term._core.
  // _renderService.dimensions is xterm 5.5.0's private renderer surface - the only place these
  // numbers are exposed in this vendored version - so this is wrapped defensively and returns
  // null rather than throwing if the internal shape ever changes.
  window.accelCellMetrics = function () {
    try {
      var dimensions = term._core._renderService.dimensions;
      return {
        cssWidth: dimensions.css.cell.width,
        cssHeight: dimensions.css.cell.height,
        deviceWidth: dimensions.device.cell.width,
        deviceHeight: dimensions.device.cell.height,
      };
    } catch (e) {
      return null;
    }
  };

  // Diagnostics for terminal-e2e-smoke-test's ConPTY-mode check: proves the host's injected build
  // number actually reached the Terminal's options, i.e. that xterm decided for itself whether this
  // machine's ConPTY needs the legacy wrapping workarounds, rather than being forced into them by
  // `windowsMode: true`. reflowEnabled reads xterm 5.5.0's private Buffer._isReflowEnabled - the only
  // place that decision is observable - so it is wrapped defensively like accelCellMetrics above.
  window.accelConPtyOptions = function () {
    if (!term) {
      return null;
    }

    var reflowEnabled = null;
    try {
      reflowEnabled = term._core._bufferService.buffers.normal._isReflowEnabled;
    } catch (e) {
      // Left null - reported as unknown rather than throwing out of a diagnostic.
    }

    return {
      injectedBuildNumber: typeof window.accelConPtyBuildNumber === "number" ? window.accelConPtyBuildNumber : null,
      windowsMode: term.options.windowsMode,
      windowsPtyBackend: term.options.windowsPty ? term.options.windowsPty.backend : null,
      windowsPtyBuildNumber: term.options.windowsPty ? term.options.windowsPty.buildNumber : null,
      reflowEnabled: reflowEnabled,
    };
  };

  window.accelSocketState = function () {
    return socket ? socket.readyState : -1;
  };

  // Diagnostic: which renderer is actually painting - "webgl" only while the WebGL addon is
  // loaded and its context alive (see activateWebglRenderer's onContextLoss fallback), "dom"
  // otherwise. Lets a host/smoke test verify the ghosting fix's renderer actually engaged on
  // this machine instead of silently degrading.
  window.accelRendererType = function () {
    return webglActive ? "webgl" : "dom";
  };

  // Exposed for terminal-e2e-smoke-test's resize-reaches-child check: the exact function the
  // ResizeObserver callback above calls in production, so driving it from a test exercises the
  // real send path, not a look-alike. `term`/`fitAddon`/etc. are private to this file's closure
  // (not reachable from a bare CoreWebView2.ExecuteScriptAsync("term...") call from the host, which
  // runs in the page's own global scope, not inside this IIFE) - window.accelResizeTerminal is
  // the one exposed entry point a test needs to drive a resize the same way FitAddon would.
  window.accelSendResize = sendResize;
  window.accelResizeTerminal = function (cols, rows) {
    if (term) {
      term.resize(cols, rows);
    }
    sendResize();
  };

  // Diagnostic: term.cols/term.rows are otherwise unreachable from outside this closure (same
  // reason accelResizeTerminal exists) - used by terminal-e2e-smoke-test to confirm a resize
  // was actually applied client-side before asserting it reached the child.
  window.accelTermSize = function () {
    return term ? { cols: term.cols, rows: term.rows } : null;
  };

  try {
    createTerminal();
    document.title = "accel-terminal-ready";
  } catch (e) {
    document.title = "accel-terminal-error:" + e.message;
  }
})();

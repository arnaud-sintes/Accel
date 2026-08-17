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

  // Diagnostic accumulator, read back via CoreWebView2.ExecuteScriptAsync by
  // Program.cs's `terminal-e2e-smoke-test` verb to prove real bytes from a real child arrived
  // over the wire. Not used by any production code path.
  window.accelReceivedText = "";

  function createTerminal() {
    term = new Terminal({
      convertEol: true,

      // ConPTY is the backend on every target (locked-in decision 1), so xterm's own
      // Windows-specific reflow heuristic for ConPTY line-wrapping quirks is the right choice
      // here, not just "the P2-T5 rendering-only demo happened to work without it".
      windowsMode: true,

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

  window.accelSocketState = function () {
    return socket ? socket.readyState : -1;
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

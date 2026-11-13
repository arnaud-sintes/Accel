// Clean-up applied to text copied out of the terminal. Pure (no DOM, no xterm) so it can be unit-tested
// under node - see tests/accel.Tests/TerminalCopyTextTests.cs - and is loaded before terminal.js.
//
// Why it exists: Claude Code word-wraps its own output and writes every visual row itself, through ConPTY:
// each row ends in a real line break, continuation rows start with a 2-column left margin made of real
// U+0020 characters, and rows that reach the right edge carry padding spaces. xterm therefore sees hard
// breaks, not soft wraps, and a multi-line copy of one sentence came out as several lines, each continuation
// indented. This turns that back into what the user sees as one paragraph.
//
// What it does (non-rectangular selections only):
//   1. NBSP -> space, trailing spaces/tabs trimmed per line, trailing blank lines dropped.
//   2. Joins a row onto the previous one when the break was a soft wrap. Claude Code wraps at word
//      boundaries, so a break is a soft wrap exactly when the next word would not have fitted on the previous
//      row: len(previous) + 1 + len(first word of next) > columns (less a small slack for the right margin).
//      Only indented rows are candidates (continuation rows always have the margin), and a row that starts a
//      new block - list item, heading, quote, table row, code fence - never joins.
//   3. Strips the 2-column gutter from the remaining rows when the selection starts at a Claude Code marker
//      in column 0 ("● ", "> ", ...): the margin is layout, not content.
//
// A row that really ended there but happens to be long enough to pass the test in (2) would be joined - the
// price of not being able to tell a hard break from a soft wrap; the indentation and block-start guards keep
// that rare. Rectangular (Alt-drag) selections are only line-ending normalised: their whitespace is the
// user's choice.
(function (root) {
  "use strict";

  // Columns of right-hand room Claude Code may leave unused when it wraps, so a wrapped row can be a little
  // shorter than the terminal is wide.
  var WRAP_SLACK = 4;

  // Rows starting like this (after their indentation) begin a new block rather than continue a sentence.
  var BLOCK_START = /^(?:[-*+•◦▪‣] |\d{1,3}[.)] |#{1,6}(?: |$)|>(?: |$)|\||```|~~~|[│┃║┌┐└┘├┤┬┴┼─━═╭╮╰╯])/;

  // Claude Code's own markers for a message / quoted input / tool result, at the start of the first row.
  var GUTTER_MARKER = /^(?:[●⏺>❯⎿] |⎿  )/;

  function indentOf(line) {
    var m = /^ */.exec(line);
    return m ? m[0].length : 0;
  }

  function joinSoftWraps(lines, columns, startColumn) {
    if (!(columns > 0) || lines.length < 2) {
      return lines;
    }

    var out = [];
    for (var i = 0; i < lines.length; i++) {
      var line = lines[i];
      var prev = out.length ? out[out.length - 1] : null;

      if (prev !== null && prev !== "" && line !== "" && isContinuation(prev, line, columns, out.length === 1 ? startColumn : 0)) {
        var rest = line.replace(/^ +/, "");
        var glue = /[A-Za-z0-9]-$/.test(prev) || isOneLongToken(prev, columns) ? "" : " ";
        out[out.length - 1] = prev + glue + rest;
      } else {
        out.push(line);
      }
    }

    return out;
  }

  function isContinuation(prev, line, columns, prevOffset) {
    if (indentOf(line) < 1) {
      return false;
    }

    var rest = line.replace(/^ +/, "");
    if (BLOCK_START.test(rest)) {
      return false;
    }

    // What the previous row's width was on screen: its own text, plus the columns of it the selection left out
    // when it began part-way along that row.
    var width = prev.length + prevOffset;
    var firstWord = /^\S+/.exec(rest)[0].length;
    return width + 1 + firstWord > columns - WRAP_SLACK;
  }

  // A row that is a single unbroken token running to the edge (a long URL or path) was cut mid-token: the next
  // row continues the same word, so no space goes between them.
  function isOneLongToken(prev, columns) {
    var trimmed = prev.replace(/^ +/, "");
    return trimmed.indexOf(" ") < 0 && prev.length >= columns - 1;
  }

  function stripGutter(lines, startColumn) {
    if (startColumn !== 0 || !lines.length || !GUTTER_MARKER.test(lines[0])) {
      return lines;
    }

    return lines.map(function (line, i) {
      return i > 0 && /^  \S/.test(line) ? line.slice(2) : line;
    });
  }

  /**
   * @param {string} text            what term.getSelection() returned
   * @param {{rectangular?: boolean, eol?: string, columns?: number, startColumn?: number}} [options]
   *        rectangular: an Alt-drag column selection (line-ending normalised only); eol: line separator for the
   *        result (default CRLF, what xterm itself joins rows with on Windows); columns: the terminal width, to
   *        recognise soft wraps (omitted = rows are never joined); startColumn: the 0-based column the selection
   *        began at on its first row.
   */
  function normalizeTerminalCopy(text, options) {
    var rectangular = !!(options && options.rectangular);
    var eol = (options && options.eol) || "\r\n";
    var columns = (options && options.columns) || 0;
    var startColumn = (options && options.startColumn) || 0;
    var lines = String(text == null ? "" : text).split(/\r\n|\n|\r/);

    if (!rectangular) {
      lines = lines.map(function (line) {
        // xterm already turns U+00A0 into a plain space on the way out; repeated here so the function is
        // correct on its own. Only trailing blanks go at this stage - interior and leading whitespace is content.
        return line.replace(/ /g, " ").replace(/[ \t]+$/, "");
      });

      // A selection that ends in blank (padding-only) rows should not paste as trailing empty lines.
      while (lines.length > 1 && lines[lines.length - 1] === "") {
        lines.pop();
      }

      lines = stripGutter(joinSoftWraps(lines, columns, startColumn), startColumn);
    }

    return lines.join(eol);
  }

  var api = { normalizeTerminalCopy: normalizeTerminalCopy };
  if (typeof module !== "undefined" && module.exports) {
    module.exports = api;
  } else {
    root.accelCopyText = api;
  }
})(typeof window !== "undefined" ? window : globalThis);

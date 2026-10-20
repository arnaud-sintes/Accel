namespace Accel.Orchestration;

using System.Text;

/// <summary>
/// Follows the DEC private terminal modes a session's output has switched on or off, so a terminal that
/// attaches to the session mid-run can be brought to the same state.
///
/// <para>Why it exists: panel D is one xterm.js reattached per tab, and a session's output is consumed
/// once, so a freshly attached terminal never sees the <c>CSI ? 1049 h</c> / <c>CSI ? 1002 h</c> a
/// full-screen app (Claude Code) sent at startup. It then sits in the normal buffer with mouse
/// reporting unknown, and wheel forwarding / the scrollbar depend on whichever tab was shown before.
/// <see cref="BuildReplay"/> produces the sequences that recreate the state.</para>
///
/// <para>A small state machine over the stream, not a regex: a sequence can be split across any two
/// chunks, so the parse state is kept between <see cref="Observe"/> calls. Only the private modes whose
/// loss changes behaviour are kept; everything else is ignored. Mode 2026 (synchronized output) is
/// deliberately never replayed - replaying "begin" without its "end" would freeze rendering.</para>
///
/// <para>Not thread-safe: the caller must serialise <see cref="Observe"/> and <see cref="BuildReplay"/>.</para>
/// </summary>
public sealed class TerminalModeTracker
{
    /// <summary>Longest parameter text kept for one CSI; a longer sequence is junk and is dropped.</summary>
    private const int MaxParameterChars = 64;

    private enum ParseState
    {
        Ground,
        Escape,
        Csi,
    }

    private ParseState _state = ParseState.Ground;
    private readonly StringBuilder _parameters = new();
    private readonly StringBuilder _pendingRaw = new();
    private bool _csiPrivate;
    private bool _csiInvalid;

    private int _altScreenMode;      // 0 = normal buffer, else the variant that entered it (47/1047/1049)
    private int _mouseTracking;      // 0 = off, else 9/1000/1001/1002/1003 (last one set wins)
    private int _mouseEncoding;      // 0 = default, else 1005/1006/1015
    private bool _appCursorKeys;     // 1
    private bool _cursorHidden;      // 25 reset
    private bool _bracketedPaste;    // 2004
    private bool _focusReporting;    // 1004

    /// <summary>True when the session is in the alternate screen buffer.</summary>
    public bool InAlternateScreen => _altScreenMode != 0;

    /// <summary>True when the app has asked for mouse reporting.</summary>
    public bool MouseTrackingEnabled => _mouseTracking != 0;

    /// <summary>Feeds one decoded output chunk through the tracker.</summary>
    public void Observe(string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        foreach (char c in chunk)
        {
            Step(c);
        }
    }

    /// <summary>
    /// The text that, written to a freshly reset terminal, recreates the tracked state: a full reset
    /// (<c>ESC c</c>), then one <c>CSI ? n h</c> per non-default mode - each in its own sequence, alt
    /// screen first and mouse tracking last - then any half-received sequence, so one that was split
    /// across the attach point still completes.
    /// </summary>
    public string BuildReplay()
    {
        var sb = new StringBuilder("\u001bc");

        if (_altScreenMode != 0)
        {
            AppendSet(sb, _altScreenMode);
        }

        if (_appCursorKeys)
        {
            AppendSet(sb, 1);
        }

        if (_cursorHidden)
        {
            sb.Append("\u001b[?25l");
        }

        if (_bracketedPaste)
        {
            AppendSet(sb, 2004);
        }

        if (_focusReporting)
        {
            AppendSet(sb, 1004);
        }

        if (_mouseEncoding != 0)
        {
            AppendSet(sb, _mouseEncoding);
        }

        if (_mouseTracking != 0)
        {
            AppendSet(sb, _mouseTracking);
        }

        sb.Append(_pendingRaw);
        return sb.ToString();
    }

    private static void AppendSet(StringBuilder sb, int mode) => sb.Append("\u001b[?").Append(mode).Append('h');

    private void Step(char c)
    {
        // ESC restarts a sequence wherever it lands; CAN / SUB abort one.
        if (c == '\u001b')
        {
            ResetParse();
            _state = ParseState.Escape;
            _pendingRaw.Append(c);
            return;
        }

        if (c is '\u0018' or '\u001a')
        {
            ResetParse();
            return;
        }

        switch (_state)
        {
            case ParseState.Ground:
                return;

            case ParseState.Escape:
                if (c == '[')
                {
                    _state = ParseState.Csi;
                    _pendingRaw.Append(c);
                }
                else if (c == 'c')
                {
                    ResetModes(); // RIS
                    ResetParse();
                }
                else
                {
                    ResetParse();
                }

                return;

            case ParseState.Csi:
                StepCsi(c);
                return;
        }
    }

    private void StepCsi(char c)
    {
        if (c < 0x20)
        {
            return; // a C0 control inside a CSI is executed without ending it
        }

        _pendingRaw.Append(c);

        if (c >= 0x40 && c <= 0x7e)
        {
            if (_csiPrivate && !_csiInvalid && (c == 'h' || c == 'l'))
            {
                Apply(_parameters.ToString(), set: c == 'h');
            }

            ResetParse();
            return;
        }

        if (c == '?' && _parameters.Length == 0 && !_csiPrivate && !_csiInvalid)
        {
            _csiPrivate = true;
        }
        else if ((c >= '0' && c <= '9') || c == ';')
        {
            if (_parameters.Length >= MaxParameterChars)
            {
                ResetParse();
                return;
            }

            _parameters.Append(c);
        }
        else
        {
            // '<' '=' '>' prefixes, ':' sub-parameters, intermediates (0x20-0x2f) and a stray '?'
            // all make this something other than a plain DEC private mode set/reset.
            _csiInvalid = true;
        }
    }

    private void Apply(string parameters, bool set)
    {
        foreach (string part in parameters.Split(';'))
        {
            if (int.TryParse(part, out int mode))
            {
                ApplyMode(mode, set);
            }
        }
    }

    private void ApplyMode(int mode, bool set)
    {
        switch (mode)
        {
            case 1:
                _appCursorKeys = set;
                break;
            case 25:
                _cursorHidden = !set;
                break;
            case 47:
            case 1047:
            case 1049:
                _altScreenMode = set ? mode : 0;
                break;
            case 9:
            case 1000:
            case 1001:
            case 1002:
            case 1003:
                // Resetting any of them ends tracking: terminal.js's own handler does the same.
                _mouseTracking = set ? mode : 0;
                break;
            case 1004:
                _focusReporting = set;
                break;
            case 1005:
            case 1006:
            case 1015:
                if (set)
                {
                    _mouseEncoding = mode;
                }
                else if (_mouseEncoding == mode)
                {
                    _mouseEncoding = 0;
                }

                break;
            case 2004:
                _bracketedPaste = set;
                break;
        }
    }

    private void ResetModes()
    {
        _altScreenMode = 0;
        _mouseTracking = 0;
        _mouseEncoding = 0;
        _appCursorKeys = false;
        _cursorHidden = false;
        _bracketedPaste = false;
        _focusReporting = false;
    }

    private void ResetParse()
    {
        _state = ParseState.Ground;
        _parameters.Clear();
        _pendingRaw.Clear();
        _csiPrivate = false;
        _csiInvalid = false;
    }
}

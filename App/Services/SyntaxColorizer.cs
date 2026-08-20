namespace Accel.App.Services;

using System;
using System.Collections.Generic;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

/// <summary>One coloured run inside a single line, expressed in offsets relative to the line's own
/// start (<c>0</c> = first character of the line) rather than document offsets, so the mapping that
/// produces it stays independent of any <see cref="TextDocument"/> and can be unit-tested WPF-free -
/// see <see cref="SyntaxLineSpanMapper"/>. <see cref="ColorHex"/> is always non-null: a
/// <see cref="SyntaxToken"/> with no colour produces no span at all (the editor's default foreground
/// already paints it).</summary>
public readonly record struct SyntaxLineSpan(int Start, int Length, string ColorHex);

/// <summary>
/// Frozen <see cref="SolidColorBrush"/> cache for <see cref="SyntaxToken.ColorHex"/> strings, shared
/// by every consumer of <see cref="SyntaxHighlighter"/>'s palette - <see cref="SyntaxColorizer"/>
/// (panel D's file editor) and <c>MainWindow.BuildHighlightedDocument</c>/<c>AppendToken</c> (the
/// read-only diff viewer's two panes). Deliberately one shared cache rather than one per consumer:
/// the palette is a small fixed set of hex strings, so two caches would hold duplicate brushes for
/// the same colours and could silently drift apart if the palette ever grows.
///
/// <para>Static state is safe here because every brush is <see cref="Freezable.Freeze"/>n before it
/// is stored - a frozen brush has no thread affinity and can be handed to any UI element. The
/// dictionary itself is only ever touched from the UI thread (both consumers run there), so it needs
/// no lock.</para>
/// </summary>
public static class SyntaxBrushCache
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.Ordinal);

    /// <summary>Returns the frozen brush for <paramref name="colorHex"/>, parsing it at most once per
    /// distinct string for the lifetime of the process.</summary>
    public static SolidColorBrush Get(string colorHex)
    {
        if (Cache.TryGetValue(colorHex, out var cached))
        {
            return cached;
        }

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
        brush.Freeze();
        Cache[colorHex] = brush;
        return brush;
    }
}

/// <summary>
/// Pure, WPF-free half of <see cref="SyntaxColorizer"/>: turns <see cref="SyntaxHighlighter"/>'s flat
/// token stream into per-line coloured spans. Split out as its own static class purely so it is
/// unit-testable without a UI thread or an AvalonEdit document (same convention as
/// <see cref="SyntaxHighlighter"/> itself).
/// </summary>
public static class SyntaxLineSpanMapper
{
    /// <summary>
    /// Maps <paramref name="tokens"/> (whose concatenated <see cref="SyntaxToken.Text"/> is the whole
    /// document) to one span array per line. Tokens routinely straddle line breaks - a block comment
    /// or a triple-quoted string is a single token spanning many lines - so each token is split on
    /// <c>'\n'</c> and contributes one span per line it covers. The result has exactly
    /// <c>(number of '\n' in the content) + 1</c> entries, i.e. one per line including a trailing
    /// empty one, so the caller can index it by <c>DocumentLine.LineNumber - 1</c> without a
    /// bounds dance. Uncoloured (<see langword="null"/>-hex) tokens and zero-length segments produce
    /// no span, keeping the arrays as small as the render path actually needs.
    /// </summary>
    public static IReadOnlyList<SyntaxLineSpan[]> Map(IReadOnlyList<SyntaxToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var lines = new List<SyntaxLineSpan[]>();
        List<SyntaxLineSpan>? current = null;
        int column = 0;

        foreach (var token in tokens)
        {
            string text = token.Text;
            int segmentStart = 0;

            while (true)
            {
                int newline = text.IndexOf('\n', segmentStart);
                int segmentEnd = newline < 0 ? text.Length : newline;
                int length = segmentEnd - segmentStart;

                if (length > 0 && token.ColorHex is not null)
                {
                    (current ??= new List<SyntaxLineSpan>()).Add(new SyntaxLineSpan(column, length, token.ColorHex));
                }

                column += length;

                if (newline < 0)
                {
                    break;
                }

                lines.Add(current is null ? Array.Empty<SyntaxLineSpan>() : current.ToArray());
                current = null;
                column = 0;
                segmentStart = newline + 1;
            }
        }

        lines.Add(current is null ? Array.Empty<SyntaxLineSpan>() : current.ToArray());
        return lines;
    }
}

/// <summary>
/// Bridges <see cref="SyntaxHighlighter.Tokenize"/> - the single source of colour truth for this app,
/// shared with the read-only diff viewer and covered by <c>SyntaxHighlighterTests</c> - into
/// AvalonEdit's rendering pipeline for panel D's file editor. Deliberately not an <c>.xshd</c> syntax
/// definition: that would fork the colour scheme into a second, untested definition that could drift
/// away from <see cref="SyntaxHighlighter"/>'s.
///
/// <para><b>Why a cache.</b> <see cref="ColorizeLine"/> is called by the render path for every
/// visible line, on every redraw (scroll, caret move, resize) - running a whole-document regex there
/// would be O(document) per line per frame. So tokenizing happens exactly once per document version,
/// in <see cref="Rebuild"/>, and <see cref="ColorizeLine"/> only ever reads the resulting per-line
/// span array.</para>
///
/// <para><b>Why the rebuild is debounced.</b> A rebuild re-tokenizes the entire document, so doing it
/// synchronously on every <see cref="TextDocument.TextChanged"/> would put a whole-file regex scan
/// between each keystroke and its echo. Instead the existing <see cref="Accel.Cli.DebounceCoalescer"/>
/// (the same one <see cref="TelemetryFeed"/> uses) collapses a burst of edits into one rebuild once
/// typing pauses for <see cref="RebuildDebounce"/>. Between the edit and that rebuild the cache is
/// knowingly stale - colours lag typing by a fraction of a second, which is why
/// <see cref="ColorizeLine"/> clamps every span to the line's real bounds instead of trusting the
/// cached offsets.</para>
/// </summary>
public sealed class SyntaxColorizer : DocumentColorizingTransformer, IDisposable
{
    /// <summary>
    /// Documents longer than this are not coloured at all (the cache is dropped and every line renders
    /// in the editor's default foreground). 256 KB, deliberately half of
    /// <see cref="SyntaxHighlighter.MaxHighlightedLength"/>: that limit sizes a one-shot scan for a
    /// read-only viewer, whereas this one has to survive being re-run every
    /// <see cref="RebuildDebounce"/> while someone types, on the UI thread. A file this big is a
    /// generated blob or a log, not something read line by line, so plain text is the right
    /// degradation - and it keeps the worst-case regex pass well inside one frame budget.
    /// </summary>
    public const int MaxColorizedLength = 256 * 1024;

    /// <summary>
    /// Second cutoff, on line count rather than character count, because the cache costs one array per
    /// line regardless of how short the lines are - a 200k-line file of one-character lines is far
    /// under <see cref="MaxColorizedLength"/> yet would allocate 200k arrays on every rebuild. 20k
    /// lines is roughly the largest hand-written source file that turns up in a repo.
    /// </summary>
    public const int MaxColorizedLines = 20_000;

    /// <summary>150 ms - long enough that continuous typing rebuilds once at the end of a burst rather
    /// than per keystroke, short enough that a pause to look at the screen already shows correct
    /// colours.</summary>
    public static readonly TimeSpan RebuildDebounce = TimeSpan.FromMilliseconds(150);

    private readonly IDebounceTimer _timer;
    private readonly Accel.Cli.DebounceCoalescer _coalescer;

    private TextDocument? _document;
    private SourceLanguage _language = SourceLanguage.PlainText;
    private IReadOnlyList<SyntaxLineSpan[]>? _lines;
    private bool _disposed;

    /// <param name="timer">The one-shot debounce timer, injected for the same reason
    /// <see cref="TelemetryFeed"/> injects one: no wall-clock timer is hard-wired into a class that
    /// would then be untestable. Production passes a <see cref="DispatcherDebounceTimer"/> at
    /// <see cref="RebuildDebounce"/>.</param>
    public SyntaxColorizer(IDebounceTimer timer)
    {
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        _coalescer = new Accel.Cli.DebounceCoalescer(restartTimer: _timer.Restart, stopTimer: _timer.Stop);
        _timer.Tick += OnDebounceElapsed;
    }

    /// <summary>Raised after the span cache has been rebuilt. The colorizer does not own the
    /// <see cref="TextView"/> that hosts it, so it cannot redraw itself - the owner subscribes and
    /// calls <c>TextView.Redraw()</c>. Without this, the lines that were already on screen when a
    /// debounced rebuild finished would keep painting their pre-edit colours until something else
    /// happened to invalidate them.</summary>
    public event Action? CacheRebuilt;

    /// <summary>
    /// Points the colorizer at the document currently loaded in the editor and re-colours it for
    /// <paramref name="language"/>, rebuilding the cache immediately (no debounce - this is a tab
    /// switch, not typing, and the new content must be correct on its first frame). Unhooks the
    /// previous document's <see cref="TextDocument.TextChanged"/>, so the shared editor control can be
    /// re-pointed at any number of documents without leaking subscriptions.
    /// </summary>
    public void SetDocument(TextDocument? document, SourceLanguage language)
    {
        if (_document is not null)
        {
            _document.TextChanged -= OnDocumentTextChanged;
        }

        _document = document;
        _language = language;

        if (_document is not null)
        {
            _document.TextChanged += OnDocumentTextChanged;
        }

        // A pending debounced rebuild would otherwise fire against the new document; drop it, since
        // the rebuild below already brings the cache up to date.
        _ = _coalescer.Elapsed();
        Rebuild();
    }

    private void OnDocumentTextChanged(object? sender, EventArgs e) => _coalescer.Signal();

    private void OnDebounceElapsed()
    {
        if (_coalescer.Elapsed())
        {
            Rebuild();
            CacheRebuilt?.Invoke();
        }
    }

    private void Rebuild()
    {
        if (_document is null || _language == SourceLanguage.PlainText)
        {
            _lines = null;
            return;
        }

        // Both cutoffs are checked before any text is materialised or scanned - see the constants'
        // remarks for why a big file degrades to uncoloured plain text rather than to a slow editor.
        if (_document.TextLength > MaxColorizedLength || _document.LineCount > MaxColorizedLines)
        {
            _lines = null;
            return;
        }

        _lines = SyntaxLineSpanMapper.Map(SyntaxHighlighter.Tokenize(_document.Text, _language));
    }

    /// <summary>
    /// Render path - reads the cache only, never tokenizes. Offsets are clamped to the line's real
    /// bounds because the cache can legitimately be one debounce window behind the document (and, on
    /// a document whose line delimiter is CRLF, a cached span may include the <c>'\r'</c> that
    /// AvalonEdit excludes from the line): a stale offset past <see cref="DocumentLine.EndOffset"/>
    /// would otherwise throw out of the renderer.
    /// </summary>
    protected override void ColorizeLine(DocumentLine line)
    {
        if (_lines is null)
        {
            return;
        }

        int index = line.LineNumber - 1;
        if (index < 0 || index >= _lines.Count)
        {
            return;
        }

        int lineStart = line.Offset;
        int lineEnd = line.EndOffset;

        foreach (var span in _lines[index])
        {
            int start = lineStart + span.Start;
            if (start >= lineEnd)
            {
                continue;
            }

            int end = Math.Min(start + span.Length, lineEnd);
            var brush = SyntaxBrushCache.Get(span.ColorHex);
            ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_document is not null)
        {
            _document.TextChanged -= OnDocumentTextChanged;
            _document = null;
        }

        _timer.Tick -= OnDebounceElapsed;
        _timer.Dispose();
    }
}

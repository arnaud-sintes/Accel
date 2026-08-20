namespace Accel.App.Services;

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// The line-ending style a file uses on disk. <see cref="Mixed"/> is a real, common case (a file
/// touched by both a Windows editor and a POSIX tool), not an error - it exists so a save can be
/// deliberate about which style it writes back instead of silently picking one.
/// </summary>
public enum LineEnding
{
    /// <summary>Every newline in the file is a bare LF (and a file with no newline at all).</summary>
    Lf,

    /// <summary>Every newline in the file is CRLF.</summary>
    CrLf,

    /// <summary>The file contains both CRLF and bare-LF newlines.</summary>
    Mixed,
}

/// <summary>
/// Everything <see cref="FileTextCodec.WriteAsync"/> needs to put an edited file back on disk in
/// exactly the byte shape it was found in, plus the load-time identity (<see cref="LastWriteUtc"/> /
/// <see cref="Length"/>) a caller compares against to notice that something else - typically a
/// Claude Code session, which is the whole reason this app exists - rewrote the file underneath the
/// editor.
/// </summary>
/// <remarks>
/// <see cref="Text"/> is LF-normalised because everything downstream (the syntax colouriser's
/// line splitting, the editor document, line counting) is far simpler when one newline means one
/// character. That normalisation is *not* lossy for round-tripping: the CRLF-vs-LF decision is
/// carried by <see cref="Eol"/> / <see cref="CrLfCount"/> / <see cref="LfOnlyCount"/>, and a
/// missing trailing newline survives simply because <see cref="Text"/> then does not end in
/// <c>'\n'</c> (see <see cref="HasTrailingNewline"/>, which is exposed for callers/UI rather than
/// because the writer needs it).
/// </remarks>
public sealed record FileTextSnapshot
{
    /// <summary>File content with every CRLF collapsed to a single LF. Empty for a snapshot whose
    /// <see cref="IsTextEditable"/> is <see langword="false"/>.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// The encoding the file was decoded with, always one of the canonical framework singletons
    /// (<see cref="Encoding.UTF8"/>, <see cref="Encoding.Unicode"/>, ...). That matters: a custom
    /// instance built with <c>byteOrderMark: false</c> returns an *empty* preamble, so the writer
    /// could no longer re-emit a BOM it had to preserve. BOM emission is decided solely by
    /// <see cref="HasBom"/>; <c>GetBytes</c> never emits one for any encoding.
    /// </summary>
    public required Encoding Encoding { get; init; }

    /// <summary>Whether the file physically started with a byte-order mark. Re-emitted verbatim on
    /// save - adding or dropping a BOM is a whole-file diff and, for UTF-8, sometimes a build
    /// break.</summary>
    public required bool HasBom { get; init; }

    /// <summary>The file's line-ending style as found on disk.</summary>
    public required LineEnding Eol { get; init; }

    /// <summary>Number of CRLF newlines seen while decoding. Kept (rather than just
    /// <see cref="Eol"/>) so the <see cref="LineEnding.Mixed"/> tie-break in
    /// <see cref="EffectiveEol"/> is auditable from the snapshot alone.</summary>
    public required int CrLfCount { get; init; }

    /// <summary>Number of bare-LF newlines seen while decoding.</summary>
    public required int LfOnlyCount { get; init; }

    /// <summary>Whether the file ended with a newline. Informational (the writer preserves this via
    /// <see cref="Text"/> itself) - useful for a caller that wants to surface it.</summary>
    public required bool HasTrailingNewline { get; init; }

    /// <summary>Load-time last-write timestamp, for external-change detection.</summary>
    public required DateTime LastWriteUtc { get; init; }

    /// <summary>Load-time byte length, paired with <see cref="LastWriteUtc"/> because a same-second
    /// rewrite of a different size is otherwise invisible.</summary>
    public required long Length { get; init; }

    /// <summary>
    /// <see langword="false"/> when the bytes are not safely editable as text - embedded NUL bytes
    /// or an invalid UTF-8 sequence. A caller must keep such a tab read-only: decoding would
    /// substitute U+FFFD for the offending bytes and saving would then write those replacement
    /// characters back, destroying data the user never touched.
    /// </summary>
    public required bool IsTextEditable { get; init; }

    /// <summary>
    /// The line ending a save actually writes. For a <see cref="LineEnding.Mixed"/> original the
    /// rule is <b>the style that occurred more often wins</b>, ties going to CRLF.
    /// </summary>
    /// <remarks>
    /// WHY dominance rather than per-line preservation: the editor hands text back as a plain
    /// LF-normalised string, so the original per-line styles are gone by then; the only alternatives
    /// were "always CRLF" (rewrites every LF line) or "always LF" (rewrites every CRLF line). Both
    /// turn a one-character edit into a whole-file git diff on a mixed file, whereas dominance
    /// rewrites only the minority lines - the smallest possible damage without carrying a per-line
    /// EOL map through the whole edit pipeline. The CRLF tie-break is arbitrary but fixed, chosen
    /// because this is a Windows tool and a file already containing CRLF is more likely to be
    /// consumed by something that expects it.
    /// </remarks>
    public LineEnding EffectiveEol => Eol switch
    {
        LineEnding.Mixed => LfOnlyCount > CrLfCount ? LineEnding.Lf : LineEnding.CrLf,
        _ => Eol,
    };
}

/// <summary>
/// Reads and writes panel D's editable file tabs while preserving the file's on-disk byte shape:
/// encoding, BOM presence, line-ending style, and the presence or absence of a trailing newline.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the read-only viewer's habit of normalising line endings before display is
/// harmless for reading and destructive for editing: saving normalised text would rewrite every
/// line ending in a CRLF file, turning a one-character edit into a whole-file git diff (and, in a
/// repo watched by a Claude Code session, a confusing one). The codec keeps the display-side
/// normalisation but records enough to undo it exactly on the way out.
/// </para>
/// <para>
/// WPF-free by design (same convention as <see cref="SyntaxHighlighter"/>) so the round-trip
/// behaviour - the one thing here that can silently corrupt a user's file - is unit-testable
/// without a UI thread.
/// </para>
/// </remarks>
public static class FileTextCodec
{
    /// <summary>Strict UTF-8: throwing on invalid bytes is how a binary/mis-encoded file is
    /// detected, so the decoder must never be the lenient default that substitutes U+FFFD.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Reads <paramref name="path"/> into a snapshot. Never throws for content reasons - a file
    /// that cannot be treated as text comes back with <see cref="FileTextSnapshot.IsTextEditable"/>
    /// <see langword="false"/> instead, so a caller can open the tab read-only rather than having
    /// to distinguish "unreadable" from "not text" by catching exceptions. I/O failures (missing
    /// file, sharing violation) still propagate: those are the caller's problem to report.
    /// </summary>
    public static FileTextSnapshot Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var info = new FileInfo(path);

        (Encoding encoding, bool hasBom, int bomLength) = DetectEncoding(bytes);

        // NUL bytes are the cheap binary tell, but only for the single-byte encodings: in UTF-16/32
        // every ASCII character legitimately contains NUL bytes, so applying the check there would
        // reject every well-formed UTF-16 file. Those encodings are only ever selected here off an
        // explicit BOM, which is itself strong evidence the file is text.
        bool nulSuspect = ReferenceEquals(encoding, Encoding.UTF8) && HasNulByte(bytes, bomLength);

        string? decoded = nulSuspect ? null : TryDecode(bytes, bomLength, encoding);
        if (decoded is null)
        {
            return NotEditable(encoding, hasBom, info);
        }

        (string text, int crLfCount, int lfOnlyCount) = NormalizeToLf(decoded);

        return new FileTextSnapshot
        {
            Text = text,
            Encoding = encoding,
            HasBom = hasBom,
            Eol = ClassifyEol(crLfCount, lfOnlyCount),
            CrLfCount = crLfCount,
            LfOnlyCount = lfOnlyCount,
            HasTrailingNewline = text.EndsWith('\n'),
            LastWriteUtc = info.LastWriteTimeUtc,
            Length = info.Length,
            IsTextEditable = true,
        };
    }

    /// <summary>
    /// Writes <paramref name="lfText"/> (the editor's LF-normalised text) to
    /// <paramref name="path"/> in <paramref name="original"/>'s byte shape: its encoding, its BOM
    /// presence, and its <see cref="FileTextSnapshot.EffectiveEol"/> line ending (see that property
    /// for the <see cref="LineEnding.Mixed"/> rule). A trailing newline is neither added nor removed
    /// - whatever <paramref name="lfText"/> ends with is what lands on disk.
    /// </summary>
    /// <remarks>
    /// Writes the file in place rather than via a temp file + replace: an in-place write keeps the
    /// existing ACLs, attributes and any hard links, which a replace would silently drop on a file
    /// the user did not ask to have re-created.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="original"/> is not editable as text. Writing decoded text back over such a
    /// file would persist U+FFFD replacement characters, so this is a caller bug, not a runtime
    /// condition to swallow.
    /// </exception>
    public static async Task WriteAsync(string path, string lfText, FileTextSnapshot original, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);

        if (!original.IsTextEditable)
        {
            throw new InvalidOperationException($"Refusing to write '{path}': its snapshot is not editable as text.");
        }

        byte[] bytes = Encode(lfText, original);

        // FileMode.Create (not OpenOrCreate) so a shrinking edit cannot leave the tail of the old,
        // longer file behind.
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The pure part of <see cref="WriteAsync"/>: LF-normalised text plus a snapshot in, the exact
    /// bytes that belong on disk out. Separated so the round-trip guarantee can be asserted without
    /// touching the filesystem.
    /// </summary>
    public static byte[] Encode(string lfText, FileTextSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);

        // Defensive re-normalisation: the editor control may itself insert CRLF for a newly typed
        // line (AvalonEdit's newline is configurable and defaults to the platform's), and a raw
        // Replace("\n", "\r\n") over such text would produce CRCRLF. Collapsing first makes the
        // conversion idempotent regardless of what the caller hands over.
        string text = lfText.Replace("\r\n", "\n");

        string onDisk = original.EffectiveEol == LineEnding.CrLf ? text.Replace("\n", "\r\n") : text;

        byte[] preamble = original.HasBom ? original.Encoding.GetPreamble() : Array.Empty<byte>();
        byte[] body = original.Encoding.GetBytes(onDisk);

        if (preamble.Length == 0)
        {
            return body;
        }

        byte[] result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    /// <summary>
    /// Picks the encoding from a leading BOM, defaulting to UTF-8 without one. Deliberately does
    /// *not* try to guess UTF-16 from a NUL-byte histogram: a wrong guess corrupts the file on
    /// save, and a BOM-less UTF-16 file instead falls out as "not editable as text" (its NUL bytes
    /// trip the binary check), which is the safe failure.
    /// </summary>
    private static (Encoding Encoding, bool HasBom, int BomLength) DetectEncoding(byte[] bytes)
    {
        // UTF-32LE must be tested before UTF-16LE: its BOM starts with the same FF FE pair.
        if (StartsWith(bytes, 0xFF, 0xFE, 0x00, 0x00))
        {
            return (Encoding.UTF32, true, 4);
        }

        if (StartsWith(bytes, 0xEF, 0xBB, 0xBF))
        {
            return (Encoding.UTF8, true, 3);
        }

        if (StartsWith(bytes, 0xFF, 0xFE))
        {
            return (Encoding.Unicode, true, 2);
        }

        if (StartsWith(bytes, 0xFE, 0xFF))
        {
            return (Encoding.BigEndianUnicode, true, 2);
        }

        return (Encoding.UTF8, false, 0);
    }

    private static bool StartsWith(byte[] bytes, params byte[] prefix)
    {
        if (bytes.Length < prefix.Length)
        {
            return false;
        }

        for (int i = 0; i < prefix.Length; i++)
        {
            if (bytes[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasNulByte(byte[] bytes, int from)
    {
        for (int i = from; i < bytes.Length; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns <see langword="null"/> when the bytes are not valid in
    /// <paramref name="encoding"/> - the signal that the file must stay read-only.</summary>
    private static string? TryDecode(byte[] bytes, int bomLength, Encoding encoding)
    {
        // Only UTF-8 gets a strict decoder: it is the one encoding here that was assumed rather
        // than proven by a BOM, so it is the one where invalid bytes actually mean "not text".
        Encoding decoder = ReferenceEquals(encoding, Encoding.UTF8) ? StrictUtf8 : encoding;

        try
        {
            return decoder.GetString(bytes, bomLength, bytes.Length - bomLength);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Collapses CRLF to LF and counts both styles in one pass. A <b>lone CR</b> is deliberately
    /// left as-is instead of being folded into LF the way a display-only normaliser would: it is
    /// not a line ending this codec can reconstruct, so turning it into LF here would mean silently
    /// converting it to a real newline on the next save.
    /// </summary>
    private static (string Text, int CrLfCount, int LfOnlyCount) NormalizeToLf(string decoded)
    {
        int crLf = 0;
        int lfOnly = 0;
        var builder = new StringBuilder(decoded.Length);

        for (int i = 0; i < decoded.Length; i++)
        {
            char c = decoded[i];

            if (c == '\r' && i + 1 < decoded.Length && decoded[i + 1] == '\n')
            {
                crLf++;
                builder.Append('\n');
                i++;
                continue;
            }

            if (c == '\n')
            {
                lfOnly++;
            }

            builder.Append(c);
        }

        return (builder.ToString(), crLf, lfOnly);
    }

    /// <summary>A file with no newline at all reports <see cref="LineEnding.Lf"/>: there is no
    /// evidence for CRLF, and inventing one would add carriage returns the file never had the
    /// moment the user presses Enter.</summary>
    private static LineEnding ClassifyEol(int crLfCount, int lfOnlyCount) => (crLfCount, lfOnlyCount) switch
    {
        (> 0, > 0) => LineEnding.Mixed,
        (> 0, _) => LineEnding.CrLf,
        _ => LineEnding.Lf,
    };

    private static FileTextSnapshot NotEditable(Encoding encoding, bool hasBom, FileInfo info) => new()
    {
        Text = string.Empty,
        Encoding = encoding,
        HasBom = hasBom,
        Eol = LineEnding.Lf,
        CrLfCount = 0,
        LfOnlyCount = 0,
        HasTrailingNewline = false,
        LastWriteUtc = info.LastWriteTimeUtc,
        Length = info.Length,
        IsTextEditable = false,
    };
}

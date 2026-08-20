using System.Text;
using Accel.App.Services;
using Xunit;

namespace Accel.Tests;

/// <summary>
/// The highest-stakes tests in the editable-file-tab feature: they prove that opening a file and
/// saving it back without editing produces a <b>byte-identical</b> file. Anything less means a
/// one-character edit shows up as a whole-file git diff - or, worse, silently rewrites encoding or
/// line endings in a repo a Claude Code session is also working in.
/// </summary>
public class FileTextCodecTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch { /* best effort cleanup */ }
        }
    }

    private string WriteTempFile(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"accel-codec-test-{Guid.NewGuid():N}.txt");
        _tempFiles.Add(path);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Read, then write the untouched text straight back, and compare raw bytes - the only
    /// assertion that actually catches a fidelity bug.</summary>
    private async Task AssertRoundTripsByteIdentical(byte[] original)
    {
        string path = WriteTempFile(original);

        var snapshot = FileTextCodec.Read(path);
        Assert.True(snapshot.IsTextEditable);

        await FileTextCodec.WriteAsync(path, snapshot.Text, snapshot);

        Assert.Equal(original, File.ReadAllBytes(path));
    }

    private static byte[] Utf8NoBom(string text) => new UTF8Encoding(false).GetBytes(text);

    private static byte[] Utf8Bom(string text) =>
        Encoding.UTF8.GetPreamble().Concat(new UTF8Encoding(false).GetBytes(text)).ToArray();

    private static byte[] Utf16Le(string text) =>
        Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();

    // ---- Round-trip matrix -------------------------------------------------------------------

    [Fact]
    public async Task RoundTrip_LfOnly_IsByteIdentical() =>
        await AssertRoundTripsByteIdentical(Utf8NoBom("alpha\nbeta\ngamma\n"));

    [Fact]
    public async Task RoundTrip_CrLfOnly_IsByteIdentical() =>
        await AssertRoundTripsByteIdentical(Utf8NoBom("alpha\r\nbeta\r\ngamma\r\n"));

    [Fact]
    public async Task RoundTrip_Utf8NoBom_WithNonAsciiIsByteIdentical() =>
        await AssertRoundTripsByteIdentical(Utf8NoBom("café naïve 中文\n"));

    [Fact]
    public async Task RoundTrip_Utf8WithBom_IsByteIdentical() =>
        await AssertRoundTripsByteIdentical(Utf8Bom("alpha\r\nbeta\r\n"));

    [Fact]
    public async Task RoundTrip_Utf16Le_IsByteIdentical() =>
        await AssertRoundTripsByteIdentical(Utf16Le("alpha\r\nbeta\r\né 中文\r\n"));

    [Fact]
    public async Task RoundTrip_NoTrailingNewline_IsByteIdentical() =>
        await AssertRoundTripsByteIdentical(Utf8NoBom("alpha\nbeta"));

    [Fact]
    public async Task RoundTrip_EmptyFile_IsByteIdentical() =>
        await AssertRoundTripsByteIdentical(Array.Empty<byte>());

    [Fact]
    public async Task RoundTrip_MixedEol_RewritesOnlyTheMinorityStyleLines()
    {
        // Two CRLF lines beat one LF line, so the dominant style is CRLF - and re-writing converts
        // the minority LF line, which is *not* byte-identical. That is the documented trade-off, so
        // assert the actual bytes rather than pretending mixed files round-trip.
        byte[] original = Utf8NoBom("a\r\nb\nc\r\n");
        string path = WriteTempFile(original);

        var snapshot = FileTextCodec.Read(path);
        Assert.Equal(LineEnding.Mixed, snapshot.Eol);
        Assert.Equal(LineEnding.CrLf, snapshot.EffectiveEol);

        await FileTextCodec.WriteAsync(path, snapshot.Text, snapshot);

        Assert.Equal(Utf8NoBom("a\r\nb\r\nc\r\n"), File.ReadAllBytes(path));
    }

    // ---- Detection --------------------------------------------------------------------------

    [Theory]
    [InlineData("a\nb\n", LineEnding.Lf)]
    [InlineData("a\r\nb\r\n", LineEnding.CrLf)]
    [InlineData("a\r\nb\n", LineEnding.Mixed)]
    [InlineData("single line, no newline", LineEnding.Lf)]
    [InlineData("", LineEnding.Lf)]
    public void Read_ClassifiesLineEndings(string content, LineEnding expected)
    {
        string path = WriteTempFile(Utf8NoBom(content));

        Assert.Equal(expected, FileTextCodec.Read(path).Eol);
    }

    [Fact]
    public void Read_NormalizesCrLfForDisplayButKeepsTheEolOnRecord()
    {
        string path = WriteTempFile(Utf8NoBom("a\r\nb\r\n"));

        var snapshot = FileTextCodec.Read(path);

        Assert.Equal("a\nb\n", snapshot.Text);
        Assert.DoesNotContain('\r', snapshot.Text);
        Assert.Equal(LineEnding.CrLf, snapshot.Eol);
    }

    [Fact]
    public void Read_LoneCarriageReturn_IsLeftInTheTextRatherThanBecomingANewline()
    {
        // A bare CR is not an EOL this codec can reconstruct, so folding it to LF on read would
        // silently promote it to a real newline on the next save.
        string path = WriteTempFile(Utf8NoBom("a\rb\n"));

        var snapshot = FileTextCodec.Read(path);

        Assert.Equal("a\rb\n", snapshot.Text);
        Assert.Equal(LineEnding.Lf, snapshot.Eol);
    }

    [Fact]
    public void Read_TracksBomPresence()
    {
        var withBom = FileTextCodec.Read(WriteTempFile(Utf8Bom("x\n")));
        var withoutBom = FileTextCodec.Read(WriteTempFile(Utf8NoBom("x\n")));

        Assert.True(withBom.HasBom);
        Assert.False(withoutBom.HasBom);
        Assert.Equal("x\n", withBom.Text); // the BOM must never leak into the editable text
        Assert.Equal(withoutBom.Text, withBom.Text);
    }

    [Fact]
    public void Read_TracksTrailingNewline()
    {
        Assert.True(FileTextCodec.Read(WriteTempFile(Utf8NoBom("a\n"))).HasTrailingNewline);
        Assert.False(FileTextCodec.Read(WriteTempFile(Utf8NoBom("a"))).HasTrailingNewline);
    }

    [Fact]
    public void Read_RecordsLoadTimeIdentityForExternalChangeDetection()
    {
        byte[] bytes = Utf8NoBom("hello\n");
        string path = WriteTempFile(bytes);

        var snapshot = FileTextCodec.Read(path);

        Assert.Equal(bytes.Length, snapshot.Length);
        Assert.Equal(File.GetLastWriteTimeUtc(path), snapshot.LastWriteUtc);
    }

    // ---- Not-editable-as-text ---------------------------------------------------------------

    [Fact]
    public void Read_EmbeddedNulByte_IsNotEditableAsText()
    {
        string path = WriteTempFile(new byte[] { 0x68, 0x69, 0x00, 0x68, 0x69 });

        var snapshot = FileTextCodec.Read(path);

        Assert.False(snapshot.IsTextEditable);
        Assert.Equal(string.Empty, snapshot.Text);
    }

    [Fact]
    public void Read_InvalidUtf8Sequence_IsNotEditableAsText()
    {
        // 0xC3 starts a two-byte sequence; 0x28 cannot continue it.
        string path = WriteTempFile(new byte[] { 0x61, 0xC3, 0x28, 0x62 });

        Assert.False(FileTextCodec.Read(path).IsTextEditable);
    }

    [Fact]
    public void Read_Utf16WithBom_StaysEditableDespiteItsNulBytes()
    {
        // The NUL-byte binary heuristic must not fire on UTF-16, where ASCII text is half NULs.
        string path = WriteTempFile(Utf16Le("plain ascii\r\n"));

        Assert.True(FileTextCodec.Read(path).IsTextEditable);
    }

    [Fact]
    public async Task WriteAsync_NonEditableSnapshot_Throws()
    {
        string path = WriteTempFile(new byte[] { 0x00, 0x01 });
        var snapshot = FileTextCodec.Read(path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => FileTextCodec.WriteAsync(path, "text", snapshot));
    }

    // ---- Write behaviour --------------------------------------------------------------------

    [Fact]
    public async Task WriteAsync_AppliesOriginalEolAndBomToEditedText()
    {
        string path = WriteTempFile(Utf8Bom("a\r\nb\r\n"));
        var snapshot = FileTextCodec.Read(path);

        await FileTextCodec.WriteAsync(path, "a\nb\nc\n", snapshot);

        Assert.Equal(Utf8Bom("a\r\nb\r\nc\r\n"), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task WriteAsync_DoesNotInventATrailingNewline()
    {
        string path = WriteTempFile(Utf8NoBom("a\nb"));
        var snapshot = FileTextCodec.Read(path);

        await FileTextCodec.WriteAsync(path, snapshot.Text + "\nc", snapshot);

        Assert.Equal(Utf8NoBom("a\nb\nc"), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task WriteAsync_ShrinkingEdit_DoesNotLeaveTheOldTail()
    {
        string path = WriteTempFile(Utf8NoBom("a long original line\nand another\n"));
        var snapshot = FileTextCodec.Read(path);

        await FileTextCodec.WriteAsync(path, "x\n", snapshot);

        Assert.Equal(Utf8NoBom("x\n"), File.ReadAllBytes(path));
    }

    [Fact]
    public void Encode_CrLfInputAgainstACrLfOriginal_DoesNotDoubleTheCarriageReturn()
    {
        // The editor control may hand back CRLF for a newly typed line; naive "\n" -> "\r\n" on
        // such text would produce CR CR LF.
        string path = WriteTempFile(Utf8NoBom("a\r\n"));
        var snapshot = FileTextCodec.Read(path);

        byte[] bytes = FileTextCodec.Encode("a\r\nb\r\n", snapshot);

        Assert.Equal(Utf8NoBom("a\r\nb\r\n"), bytes);
    }
}

using System.Text;
using Accel.App.Services;
using Xunit;

namespace Accel.Tests;

/// <summary>
/// Covers the pure half of external-change detection: given what a buffer read and what is on disk
/// now, is the buffer stale? Getting this wrong in either direction is expensive - a false negative
/// lets the editor overwrite a Claude Code session's edit with no prompt at all, a false positive
/// puts a "which version do you want to lose" dialog in front of the user for no reason.
///
/// <para>The conflict prompt itself (<c>MainWindow.ResolveExternalFileChangeAsync</c> and
/// <c>AccelMessageDialog.ShowChoice</c>) is not covered here: it is a modal WPF dialog on the UI
/// thread, so its outcomes are verified by hand, and the decision logic that hangs off it is written
/// to depend only on <see cref="ExternalFileChangeDetector.HasChangedOnDisk"/>, which is.</para>
/// </summary>
public class ExternalFileChangeDetectorTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch { /* best effort cleanup */ }
        }
    }

    private string WriteTempFile(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), $"accel-extchange-test-{Guid.NewGuid():N}.txt");
        _tempFiles.Add(path);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static FileTextSnapshot SnapshotOf(DateTime lastWriteUtc, long length, bool isTextEditable = true) => new()
    {
        Text = string.Empty,
        Encoding = Encoding.UTF8,
        HasBom = false,
        Eol = LineEnding.Lf,
        CrLfCount = 0,
        LfOnlyCount = 0,
        HasTrailingNewline = false,
        LastWriteUtc = lastWriteUtc,
        Length = length,
        IsTextEditable = isTextEditable,
    };

    private static readonly DateTime Stamp = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void HasChangedOnDisk_IsFalse_WhenTimestampAndLengthBothMatch()
    {
        var snapshot = SnapshotOf(Stamp, 1234);
        var current = new ExternalFileState(true, Stamp, 1234);

        Assert.False(ExternalFileChangeDetector.HasChangedOnDisk(snapshot, current));
    }

    [Fact]
    public void HasChangedOnDisk_IsTrue_WhenTimestampMovedButLengthDidNot()
    {
        // The common Claude Code case: a rewrite that happens to keep the file the same size.
        var snapshot = SnapshotOf(Stamp, 1234);
        var current = new ExternalFileState(true, Stamp.AddSeconds(1), 1234);

        Assert.True(ExternalFileChangeDetector.HasChangedOnDisk(snapshot, current));
    }

    [Fact]
    public void HasChangedOnDisk_IsTrue_WhenLengthMovedButTimestampDidNot()
    {
        // Two writes inside the filesystem's timestamp resolution; length is the only tell left.
        var snapshot = SnapshotOf(Stamp, 1234);
        var current = new ExternalFileState(true, Stamp, 1200);

        Assert.True(ExternalFileChangeDetector.HasChangedOnDisk(snapshot, current));
    }

    [Fact]
    public void HasChangedOnDisk_IsTrue_WhenTheFileWentBackwardsInTime()
    {
        // A restore / checkout / git stash pop can hand back an older stamp. "Different", not "newer".
        var snapshot = SnapshotOf(Stamp, 1234);
        var current = new ExternalFileState(true, Stamp.AddMinutes(-30), 1234);

        Assert.True(ExternalFileChangeDetector.HasChangedOnDisk(snapshot, current));
    }

    [Fact]
    public void HasChangedOnDisk_IsFalse_WhenTheFileNoLongerExists()
    {
        // A delete offers no version to reload, so it is deliberately not a "conflict" - the buffer
        // keeps its text and a save re-creates the file.
        var snapshot = SnapshotOf(Stamp, 1234);
        var current = new ExternalFileState(false, DateTime.MinValue, 0);

        Assert.False(ExternalFileChangeDetector.HasChangedOnDisk(snapshot, current));
    }

    [Fact]
    public void HasChangedOnDisk_IsFalse_ForASnapshotThatWasNeverEditableAsText()
    {
        var snapshot = SnapshotOf(Stamp, 1234, isTextEditable: false);
        var current = new ExternalFileState(true, Stamp.AddSeconds(5), 99);

        Assert.False(ExternalFileChangeDetector.HasChangedOnDisk(snapshot, current));
    }

    [Fact]
    public void Probe_ReportsTheCurrentTimestampAndLength()
    {
        string path = WriteTempFile("hello");

        var state = ExternalFileChangeDetector.Probe(path);

        Assert.True(state.Exists);
        Assert.Equal(5, state.Length);
        Assert.Equal(File.GetLastWriteTimeUtc(path), state.LastWriteUtc);
    }

    [Fact]
    public void Probe_ReportsNotExisting_RatherThanThrowing_ForAMissingPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"accel-extchange-missing-{Guid.NewGuid():N}.txt");

        var state = ExternalFileChangeDetector.Probe(path);

        Assert.False(state.Exists);
    }

    [Fact]
    public async Task ReadThenProbe_AgreeForAnUntouchedFile_AndDisagreeAfterAnExternalWrite()
    {
        // End-to-end over the real filesystem: the identity FileTextCodec.Read records must be the
        // same identity Probe reads back, or every check would report a phantom change.
        string path = WriteTempFile("original\n");
        var snapshot = FileTextCodec.Read(path);

        Assert.False(ExternalFileChangeDetector.HasChangedOnDisk(snapshot, ExternalFileChangeDetector.Probe(path)));

        // Stand in for a Claude Code session rewriting the file underneath the open tab. The explicit
        // timestamp bump keeps the test independent of the filesystem's clock resolution.
        await File.WriteAllTextAsync(path, "rewritten by someone else\n");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));

        Assert.True(ExternalFileChangeDetector.HasChangedOnDisk(snapshot, ExternalFileChangeDetector.Probe(path)));
    }
}

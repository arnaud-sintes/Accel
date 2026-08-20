namespace Accel.App.Services;

using System;
using System.IO;

/// <summary>
/// What a cheap stat of an open buffer's file found on disk right now: whether the file is still
/// there, and the two cheap identity fields (<see cref="LastWriteUtc"/>/<see cref="Length"/>) an
/// external-change check compares against the buffer's
/// <see cref="FileTextSnapshot.LastWriteUtc"/>/<see cref="FileTextSnapshot.Length"/>.
/// </summary>
/// <param name="Exists">Whether the path still resolves to a file. See
/// <see cref="ExternalFileChangeDetector.HasChangedOnDisk"/> for why a vanished file is
/// deliberately not reported as an external change.</param>
/// <param name="LastWriteUtc">The file's current last-write timestamp, or
/// <see cref="DateTime.MinValue"/> when it no longer exists.</param>
/// <param name="Length">The file's current byte length, or 0 when it no longer exists.</param>
public readonly record struct ExternalFileState(bool Exists, DateTime LastWriteUtc, long Length);

/// <summary>
/// Answers one question for panel D's editor: <i>did something else rewrite this file since the
/// buffer read it?</i>
///
/// <para><b>Why this is the highest-stakes check in the editor.</b> Accel's entire reason to exist is
/// watching Claude Code sessions - which rewrite exactly the files a user is likely to have open in a
/// tab here, while they have it open. An editor that saves a stale buffer over a concurrent agent
/// edit destroys work that nobody asked it to touch and says nothing. So the rule is: notice first,
/// and when both sides changed, never pick a winner - see
/// <c>MainWindow.ResolveExternalFileChangeAsync</c> for the prompt this feeds.</para>
///
/// <para>WPF-free and I/O-free apart from <see cref="Probe"/>, same convention as
/// <see cref="FileTextCodec"/>: the comparison rule is the part that can silently lose data, so it is
/// a pure function that can be unit-tested without a UI thread or a filesystem.</para>
/// </summary>
public static class ExternalFileChangeDetector
{
    /// <summary>
    /// Stats <paramref name="path"/> without reading it. Deliberately a stat and not a re-read: this
    /// runs on every tab activation and every save, and hashing (or diffing) file content on the UI
    /// thread to answer "probably unchanged" would make tab switching pay for the rare case.
    /// </summary>
    /// <remarks>
    /// Never throws: a path that has become unreachable (deleted, its parent removed, permissions
    /// revoked mid-session) comes back as <see cref="ExternalFileState.Exists"/>
    /// <see langword="false"/> rather than as an exception, because the caller is a change *check* -
    /// it must not be able to break a tab switch that would otherwise have worked.
    /// </remarks>
    public static ExternalFileState Probe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new ExternalFileState(true, info.LastWriteTimeUtc, info.Length)
                : new ExternalFileState(false, DateTime.MinValue, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new ExternalFileState(false, DateTime.MinValue, 0);
        }
    }

    /// <summary>
    /// Whether <paramref name="current"/> shows the file as having been rewritten since
    /// <paramref name="snapshot"/> was taken. Pure - this is the rule the whole conflict flow hangs
    /// off, so it is separated from the stat that produces its input.
    /// </summary>
    /// <remarks>
    /// <para><b>Why both fields.</b> Timestamp alone misses a rewrite that lands inside the
    /// filesystem's timestamp resolution (NTFS is coarse enough that an agent writing a file twice in
    /// quick succession can produce two identical stamps); length alone misses every same-size edit,
    /// which is most of them. Either differing is enough.</para>
    ///
    /// <para><b>Why a vanished file is not "changed".</b> Reporting it as a change would offer the
    /// user a "reload from disk" outcome with nothing to reload from, and would block a save that is
    /// perfectly well-defined - re-creating the file the user still has open. A delete therefore
    /// leaves the buffer exactly as it is; the save path re-creates the file, and a non-dirty buffer
    /// keeps showing the last known content rather than being blanked by something that happened
    /// outside the editor.</para>
    ///
    /// <para><b>Why a snapshot that was never editable is not "changed".</b> Such a buffer has no
    /// text of its own to defend (see <see cref="FileTextSnapshot.IsTextEditable"/>) and can never be
    /// saved, so there is no conflict to resolve.</para>
    /// </remarks>
    public static bool HasChangedOnDisk(FileTextSnapshot snapshot, ExternalFileState current)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!current.Exists || !snapshot.IsTextEditable)
        {
            return false;
        }

        return current.LastWriteUtc != snapshot.LastWriteUtc || current.Length != snapshot.Length;
    }
}

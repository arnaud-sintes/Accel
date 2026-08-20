namespace Accel.App.Services;

using System;
using System.ComponentModel;
using ICSharpCode.AvalonEdit.Document;

/// <summary>
/// Everything panel D keeps alive for <b>one</b> open editable file tab: the AvalonEdit
/// <see cref="TextDocument"/> holding its (possibly unsaved) text, the
/// <see cref="FileTextSnapshot"/> the text was loaded from, and where the user was looking when they
/// last left the tab.
///
/// <para><b>Why this exists at all.</b> Panel D hosts exactly <i>one</i> <c>TextEditor</c> control,
/// re-pointed on every tab selection (the same one-control-and-reattach design its single
/// <c>TerminalView</c> follows, and for the same reasons). So the control cannot be where unsaved
/// text or undo history lives: switching tabs would destroy both, silently. Ownership therefore sits
/// here, one instance per open editable tab, and a tab switch is nothing more than
/// <c>FileEditor.Document = buffer.Document</c>.</para>
///
/// <para><b>Why the document is the whole trick.</b> An AvalonEdit <see cref="TextDocument"/> owns
/// its own <see cref="UndoStack"/>, so keeping the document per tab yields per-tab undo/redo
/// <i>and</i> per-tab dirty tracking (<see cref="UndoStack.IsOriginalFile"/>, armed by
/// <see cref="UndoStack.MarkAsOriginalFile"/>) for free - no hand-rolled undo history, and no
/// diffing the editor's text against a baseline string to decide whether a tab is dirty.</para>
///
/// <para>A buffer is only ever created for content that has a working-tree file behind it to save
/// back to; read-only content (a Deleted GIT entry's <c>git show</c> fallback, a diff side, a
/// non-text file, a failed read) is rendered into a throwaway document instead and never cached
/// here - see <c>MainWindow.ShowFileTabAsync</c>.</para>
/// </summary>
public sealed class FileEditBuffer
{
    /// <param name="document">The document the editor is pointed at while this tab is selected. Its
    /// undo stack must already be marked as the original file by the caller (see
    /// <see cref="UndoStack.MarkAsOriginalFile"/>) - doing it here would hide the one call that
    /// defines what "not dirty" means for this buffer.</param>
    /// <param name="snapshot">What <see cref="FileTextCodec.Read"/> found on disk: the encoding, BOM
    /// and line-ending shape a save has to reproduce, plus the load-time
    /// <see cref="FileTextSnapshot.LastWriteUtc"/>/<see cref="FileTextSnapshot.Length"/> an
    /// external-change check compares against. Not duplicated onto this class - the snapshot is the
    /// single record of it.</param>
    /// <param name="language">The syntax language resolved from the path once, at load, so a tab
    /// switch does not have to re-resolve it to re-point the colouriser.</param>
    public FileEditBuffer(TextDocument document, FileTextSnapshot snapshot, SourceLanguage language)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Language = language;
    }

    /// <summary>See this class's remarks: the text, the undo stack and the dirty flag all live here.</summary>
    public TextDocument Document { get; }

    /// <summary>
    /// The on-disk shape the text was read in. Settable because a successful save re-reads its own
    /// identity (a new <see cref="FileTextSnapshot.LastWriteUtc"/>/<see cref="FileTextSnapshot.Length"/>)
    /// without the buffer being rebuilt.
    /// </summary>
    public FileTextSnapshot Snapshot { get; set; }

    /// <summary>See the constructor's <c>language</c> parameter.</summary>
    public SourceLanguage Language { get; }

    /// <summary>
    /// The on-disk state the user has explicitly decided to overwrite, answering "Keep my version" to
    /// the external-change conflict prompt, or <see langword="null"/> when no such decision is
    /// outstanding.
    ///
    /// <para><b>Why remember it.</b> Answering the prompt does not (and must not) update
    /// <see cref="Snapshot"/>: the snapshot is what a save reproduces the byte shape from and what
    /// staleness is measured against, so adopting the foreign file's identity into it would make the
    /// *next* check believe the buffer is current and let it overwrite the other writer's work with
    /// no prompt at all. Recording the decision separately keeps the check honest while stopping the
    /// same, already-answered conflict from re-prompting on every tab switch - and because it stores
    /// the exact state that was acknowledged, a <i>further</i> external write produces a different
    /// state and does prompt again.</para>
    ///
    /// <para>Cleared whenever the buffer is re-read from disk, since the conflict it recorded no
    /// longer exists.</para>
    /// </summary>
    public ExternalFileState? AcknowledgedDiskState { get; set; }

    /// <summary>Caret offset as of the last time this tab was deactivated, so returning to a tab puts
    /// the caret back where the user left it rather than at the top of the file. Clamped to the
    /// document's length on restore - a discard/reload can shorten the text underneath it.</summary>
    public int CaretOffset { get; set; }

    /// <summary>Scroll offsets as of the last deactivation, paired with <see cref="CaretOffset"/>:
    /// the caret alone does not determine the viewport (a caret at offset 0 in a file scrolled by a
    /// search hit would jump the view), so both are restored.</summary>
    public double VerticalOffset { get; set; }

    /// <summary>See <see cref="VerticalOffset"/>.</summary>
    public double HorizontalOffset { get; set; }

    /// <summary>
    /// The <see cref="UndoStack.IsOriginalFile"/> subscription that pushes this buffer's dirty state
    /// onto its tab, kept so eviction can unsubscribe it. Held here rather than in the owner's
    /// dictionary because it is per-buffer state with exactly the same lifetime as the buffer.
    /// </summary>
    public PropertyChangedEventHandler? DirtyListener { get; set; }

    /// <summary>Detaches <see cref="DirtyListener"/> - called when the tab closes, so a document that
    /// is about to be dropped cannot keep writing into the ViewModel of a tab that no longer
    /// exists.</summary>
    public void Detach()
    {
        if (DirtyListener is not null)
        {
            Document.UndoStack.PropertyChanged -= DirtyListener;
            DirtyListener = null;
        }
    }
}

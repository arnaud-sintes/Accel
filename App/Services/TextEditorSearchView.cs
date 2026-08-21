namespace Accel.App.Services;

using System.Collections.Generic;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;

/// <summary>
/// <see cref="IDocumentSearchView"/> over an AvalonEdit <see cref="TextEditor"/> - panel D's
/// single-pane file viewer (<c>MainWindow.FileEditor</c>) and the diff view's editable "After" pane
/// (<c>MainWindow.DiffNewEditor</c>). Reads the live document, so one instance keeps working across
/// every tab that editor is pointed at; the find bar only has to re-run its query when the document
/// changes underneath it.
/// </summary>
public sealed class TextEditorSearchView : IDocumentSearchView
{
    private readonly TextEditor _editor;
    private readonly SearchMatchColorizer _colorizer;

    /// <summary>Installs the match colorizer on <paramref name="editor"/>'s text view. Appended after
    /// whatever transformers are already there, which is what makes a hit paint on top of the syntax
    /// and added-line colours - see <see cref="SearchMatchColorizer"/>.</summary>
    public TextEditorSearchView(TextEditor editor, Brush matchBrush, Brush currentMatchBrush)
    {
        _editor = editor;
        _colorizer = new SearchMatchColorizer(matchBrush, currentMatchBrush);
        _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
    }

    public string Text => _editor.Document?.Text ?? string.Empty;

    public int CaretOffset => _editor.SelectionLength > 0 ? _editor.SelectionStart : _editor.CaretOffset;

    public void ShowMatches(IReadOnlyList<TextSearchMatch> matches, int currentIndex)
    {
        _colorizer.SetMatches(matches, currentIndex);
        _editor.TextArea.TextView.Redraw();
    }

    public void Reveal(TextSearchMatch match)
    {
        if (_editor.Document is not { } document || match.EndOffset > document.TextLength)
        {
            return;
        }

        // ScrollTo takes a line/column, not an offset, and centers vertically only when the target is
        // off-screen - the same behaviour AvalonEdit's own search panel has, so repeated Find Next
        // inside one screenful does not jerk the view around.
        var location = document.GetLocation(match.Offset);
        _editor.ScrollTo(location.Line, location.Column);

        // Selecting (not just moving the caret) leaves the match ready to copy and leaves the caret
        // on it for the next "find next from here". The selection itself is invisible while the match
        // is the current one - SearchMatchColorizer's background paints over the selection layer -
        // and becomes the only marker once the bar closes and the highlights are dropped.
        _editor.Select(match.Offset, match.Length);
    }

    public void ClearMatches()
    {
        _colorizer.Clear();
        _editor.TextArea.TextView.Redraw();
    }

    public void FocusDocument() => _editor.TextArea.Focus();
}

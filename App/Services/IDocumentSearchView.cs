namespace Accel.App.Services;

using System.Collections.Generic;

/// <summary>
/// What the find bar (<c>App/Controls/DocumentSearchBar.xaml</c>) needs from whatever control is
/// showing the document underneath it. It exists because panel D shows a document in two structurally
/// different controls: the single-pane file viewer and the diff view's editable "After" side are
/// AvalonEdit <c>TextEditor</c>s (<see cref="TextEditorSearchView"/>), while the diff view's
/// read-only "After" side is a <c>RichTextBox</c> over a hand-built <c>FlowDocument</c>
/// (<see cref="RichTextBoxSearchView"/>). One find bar drives either through this interface rather
/// than the bar growing two code paths - and the plain <see cref="Text"/>/offset shape keeps the
/// actual searching in <see cref="TextSearchEngine"/>, which knows about neither control.
/// </summary>
public interface IDocumentSearchView
{
    /// <summary>The document as one plain string. Offsets in every other member of this interface -
    /// and in the <see cref="TextSearchMatch"/>es handed back - index into exactly this string.</summary>
    string Text { get; }

    /// <summary>Where a freshly-opened search should start looking from (the caret, or the current
    /// selection's start), as an offset into <see cref="Text"/>.</summary>
    int CaretOffset { get; }

    /// <summary>Marks <paramref name="matches"/> in the document, with <paramref name="currentIndex"/>
    /// (an index into <paramref name="matches"/>, or -1 for none) distinguished as the active one.
    /// Does not scroll - <see cref="Reveal"/> does that.</summary>
    void ShowMatches(IReadOnlyList<TextSearchMatch> matches, int currentIndex);

    /// <summary>Scrolls <paramref name="match"/> into view and puts the caret/selection on it, so
    /// closing the bar leaves the user where the match was.</summary>
    void Reveal(TextSearchMatch match);

    /// <summary>Removes every mark <see cref="ShowMatches"/> added. Called when the bar closes or is
    /// pointed at a different control.</summary>
    void ClearMatches();

    /// <summary>Returns keyboard focus to the document, so Escape out of the bar lands the user back
    /// in the text rather than nowhere.</summary>
    void FocusDocument();
}

namespace Accel.Tests;

using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Accel.App.Controls;
using Accel.App.Services;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using Xunit;

/// <summary>
/// Covers the Ctrl+F find bar's two document adapters and the bar's own state machine against the
/// real WPF controls panel D uses - the parts <see cref="TextSearchEngineTests"/> deliberately cannot
/// reach, since they are exactly the translation from plain-text offsets into a control's own
/// addressing.
///
/// <para>The load-bearing case here is <see cref="RichTextBoxSearchView"/>: the diff view's read-only
/// "After" pane is a <see cref="FlowDocument"/> whose lines are split into one <see cref="Run"/> per
/// syntax token, so "does a match spanning several runs still resolve to the right selection" is a
/// real risk, not a hypothetical one.</para>
///
/// <para>Constructing WPF controls requires an STA thread - see <see cref="RunOnSta"/>, the same
/// pattern (and reason) as <see cref="CreateSessionDialogTests"/>.</para>
/// </summary>
public sealed class DocumentSearchTests
{
    [Fact]
    public void RichTextBoxSearchView_FlattensRunsAndLineBreaksIntoOneOffsetSpace()
    {
        RunOnSta(() =>
        {
            var view = new RichTextBoxSearchView(new RichTextBox(BuildDiffLikeDocument()));

            // One '\n' per LineBreak, and no separators between the runs that make up a single line.
            Assert.Equal("if (foo)\nreturn foo;", view.Text);
        });
    }

    [Fact]
    public void RichTextBoxSearchView_RevealSelectsTheMatchWithinOneRun()
    {
        RunOnSta(() =>
        {
            var box = new RichTextBox(BuildDiffLikeDocument());
            var view = new RichTextBoxSearchView(box);

            view.Reveal(new TextSearchMatch(4, 3));

            Assert.Equal("foo", box.Selection.Text);
        });
    }

    [Fact]
    public void RichTextBoxSearchView_RevealSelectsAMatchSpanningSeveralSyntaxRuns()
    {
        RunOnSta(() =>
        {
            var box = new RichTextBox(BuildDiffLikeDocument());
            var view = new RichTextBoxSearchView(box);

            // "f (f" straddles three of the document's runs ("if", " (", "foo").
            view.Reveal(new TextSearchMatch(1, 4));

            Assert.Equal("f (f", box.Selection.Text);
        });
    }

    [Fact]
    public void RichTextBoxSearchView_CaretOffsetMapsTheSelectionBackToTheFlatOffset()
    {
        RunOnSta(() =>
        {
            var box = new RichTextBox(BuildDiffLikeDocument());
            var view = new RichTextBoxSearchView(box);

            // Round-trips the offset map in both directions: Reveal turns 16 into pointers, CaretOffset
            // turns those pointers back into 16 - which is what makes "find next from where I am" land
            // on the following match rather than back at the top of the pane.
            view.Reveal(new TextSearchMatch(16, 3));

            Assert.Equal("foo", box.Selection.Text);
            Assert.Equal(16, view.CaretOffset);
        });
    }

    [Fact]
    public void RichTextBoxSearchView_RebuildsItsMapWhenThePaneIsGivenANewDocument()
    {
        RunOnSta(() =>
        {
            var box = new RichTextBox(BuildDiffLikeDocument());
            var view = new RichTextBoxSearchView(box);
            Assert.Equal("if (foo)\nreturn foo;", view.Text);

            // Every diff tab assigns a brand-new FlowDocument to this pane.
            box.Document = new FlowDocument(new Paragraph(new Run("other")));

            Assert.Equal("other", view.Text);
        });
    }

    [Fact]
    public void TextEditorSearchView_ReadsTheLiveDocumentAndSelectsRevealedMatches()
    {
        RunOnSta(() =>
        {
            var editor = new TextEditor { Document = new TextDocument("foo bar foo") };
            var view = NewEditorView(editor);

            Assert.Equal("foo bar foo", view.Text);

            view.Reveal(new TextSearchMatch(8, 3));

            Assert.Equal(8, editor.SelectionStart);
            Assert.Equal(3, editor.SelectionLength);

            // Selection start, not caret: the caret lands at the end of a selection, and "find next
            // from here" must not skip the match that was just revealed.
            Assert.Equal(8, view.CaretOffset);
        });
    }

    [Fact]
    public void TextEditorSearchView_TracksTheDocumentSwapEveryTabSwitchDoes()
    {
        RunOnSta(() =>
        {
            var editor = new TextEditor { Document = new TextDocument("first") };
            var view = NewEditorView(editor);
            Assert.Equal("first", view.Text);

            editor.Document = new TextDocument("second");

            Assert.Equal("second", view.Text);
        });
    }

    [Fact]
    public void TextEditorSearchView_RevealIgnoresAnOffsetPastTheEndOfTheDocument()
    {
        RunOnSta(() =>
        {
            // The bar's offsets go stale the moment the document is swapped underneath it; Reveal has
            // to tolerate that rather than throw on the way to the Refresh that fixes it.
            var editor = new TextEditor { Document = new TextDocument("short") };
            var view = NewEditorView(editor);

            view.Reveal(new TextSearchMatch(100, 3));

            Assert.Equal(0, editor.SelectionLength);
        });
    }

    [Fact]
    public void SearchBar_CountsMatchesAndStepsThroughThemWrappingAtTheEnd()
    {
        RunOnSta(() =>
        {
            var editor = new TextEditor { Document = new TextDocument("foo bar foo") };
            var (bar, _) = NewAttachedBar(editor);

            bar.Open();
            bar.QueryBox.Text = "foo";

            Assert.Equal("1/2", bar.StatusText.Text);
            Assert.Equal(0, editor.SelectionStart);

            bar.FindNext();
            Assert.Equal("2/2", bar.StatusText.Text);
            Assert.Equal(8, editor.SelectionStart);

            bar.FindNext();
            Assert.Equal("1/2", bar.StatusText.Text);
            Assert.Equal(0, editor.SelectionStart);

            bar.FindPrevious();
            Assert.Equal("2/2", bar.StatusText.Text);
            Assert.Equal(8, editor.SelectionStart);
        });
    }

    [Fact]
    public void SearchBar_ReportsNoResultsButStaysBlankForAnEmptyQuery()
    {
        RunOnSta(() =>
        {
            var (bar, _) = NewAttachedBar(new TextEditor { Document = new TextDocument("foo") });
            bar.Open();

            Assert.Equal(string.Empty, bar.StatusText.Text);
            Assert.False(bar.NextButton.IsEnabled);

            bar.QueryBox.Text = "zzz";

            Assert.Equal("No results", bar.StatusText.Text);
            Assert.False(bar.NextButton.IsEnabled);

            bar.QueryBox.Text = string.Empty;

            Assert.Equal(string.Empty, bar.StatusText.Text);
        });
    }

    [Fact]
    public void SearchBar_OptionTogglesNarrowTheMatchSet()
    {
        RunOnSta(() =>
        {
            var (bar, _) = NewAttachedBar(new TextEditor { Document = new TextDocument("Foo foo food") });
            bar.Open();
            bar.QueryBox.Text = "foo";

            Assert.Equal("1/3", bar.StatusText.Text);

            bar.MatchCaseToggle.IsChecked = true;
            Assert.Equal("1/2", bar.StatusText.Text);

            bar.WholeWordToggle.IsChecked = true;
            Assert.Equal("1/1", bar.StatusText.Text);
        });
    }

    [Fact]
    public void SearchBar_TypingKeepsTheUserNearTheMatchTheyWereOn()
    {
        RunOnSta(() =>
        {
            var editor = new TextEditor { Document = new TextDocument("foo ... foobar") };
            var (bar, _) = NewAttachedBar(editor);
            bar.Open();

            bar.QueryBox.Text = "foo";
            bar.FindNext();
            Assert.Equal(8, editor.SelectionStart);

            // Extending the query must not throw the user back to the first match at offset 0.
            bar.QueryBox.Text = "foob";
            Assert.Equal("1/1", bar.StatusText.Text);
            Assert.Equal(8, editor.SelectionStart);
        });
    }

    [Fact]
    public void SearchBar_OpenAndCloseFlipVisibilityAndKeepTheQueryForNextTime()
    {
        RunOnSta(() =>
        {
            var (bar, _) = NewAttachedBar(new TextEditor { Document = new TextDocument("foo") });

            Assert.False(bar.IsOpen);

            bar.Open();
            Assert.True(bar.IsOpen);

            bar.QueryBox.Text = "foo";
            bar.Close();

            Assert.False(bar.IsOpen);
            Assert.Equal(Visibility.Collapsed, bar.Visibility);
            Assert.Equal("foo", bar.QueryBox.Text);

            // Closed means "no matches held", so the counter cannot go on claiming a hit.
            Assert.Equal(string.Empty, bar.StatusText.Text);
        });
    }

    [Fact]
    public void SearchBar_ReattachingToAnotherPaneRerunsTheQueryThere()
    {
        RunOnSta(() =>
        {
            // Exactly what a diff tab does when the "After" side flips between its editable
            // (AvalonEdit) and read-only (RichTextBox) control.
            var editor = new TextEditor { Document = new TextDocument("foo foo foo") };
            var (bar, _) = NewAttachedBar(editor);
            bar.Open();
            bar.QueryBox.Text = "foo";
            Assert.Equal("1/3", bar.StatusText.Text);

            var box = new RichTextBox(BuildDiffLikeDocument());
            bar.Attach(new RichTextBoxSearchView(box));

            // "if (foo)\nreturn foo;" - two hits, and the new pane is the one now marked.
            Assert.Equal("1/2", bar.StatusText.Text);
            Assert.Equal("foo", box.Selection.Text);
        });
    }

    [Fact]
    public void SearchBar_StaysInertWhileClosedEvenThoughItRemembersTheQuery()
    {
        RunOnSta(() =>
        {
            var editor = new TextEditor { Document = new TextDocument("foo foo") };
            var (bar, _) = NewAttachedBar(editor);
            bar.Open();
            bar.QueryBox.Text = "foo";
            bar.Close();

            // Opening another tab re-attaches the bar. A closed bar must not reach into the new pane
            // and move its selection to a match the user never asked to see.
            var other = new TextEditor { Document = new TextDocument("foo bar") };
            bar.Attach(NewEditorView(other));

            Assert.Equal(0, other.SelectionLength);
            Assert.Equal(string.Empty, bar.StatusText.Text);

            // ...and it comes straight back to life on the next Ctrl+F, with the remembered query.
            bar.Open();
            Assert.Equal("1/1", bar.StatusText.Text);
            Assert.Equal(3, other.SelectionLength);
        });
    }

    [Fact]
    public void SearchBar_RefreshRerunsAgainstAReplacedDocument()
    {
        RunOnSta(() =>
        {
            var editor = new TextEditor { Document = new TextDocument("foo foo") };
            var (bar, _) = NewAttachedBar(editor);
            bar.Open();
            bar.QueryBox.Text = "foo";
            Assert.Equal("1/2", bar.StatusText.Text);

            editor.Document = new TextDocument("foo");
            bar.Refresh();

            Assert.Equal("1/1", bar.StatusText.Text);
        });
    }

    /// <summary>
    /// A stand-in for what <c>MainWindow.BuildHighlightedDocument</c> produces for a diff pane: one
    /// <see cref="Paragraph"/>, one <see cref="Run"/> per syntax token (so a single line is several
    /// runs), lines separated by <see cref="LineBreak"/>s. Flattens to
    /// <c>"if (foo)\nreturn foo;"</c>.
    /// </summary>
    private static FlowDocument BuildDiffLikeDocument()
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        paragraph.Inlines.Add(new Run("if") { Foreground = Brushes.Orange });
        paragraph.Inlines.Add(new Run(" ("));
        paragraph.Inlines.Add(new Run("foo"));
        paragraph.Inlines.Add(new Run(")"));
        paragraph.Inlines.Add(new LineBreak());
        paragraph.Inlines.Add(new Run("return") { Foreground = Brushes.Orange });
        paragraph.Inlines.Add(new Run(" foo;"));

        return new FlowDocument(paragraph) { PageWidth = 1_000_000 };
    }

    private static TextEditorSearchView NewEditorView(TextEditor editor) =>
        new(editor, Brushes.Transparent, Brushes.Transparent);

    private static (DocumentSearchBar Bar, TextEditorSearchView View) NewAttachedBar(TextEditor editor)
    {
        var view = NewEditorView(editor);
        var bar = new DocumentSearchBar();
        bar.Attach(view);
        return (bar, view);
    }

    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            throw captured;
        }
    }
}

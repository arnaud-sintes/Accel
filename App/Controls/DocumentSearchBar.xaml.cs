namespace Accel.App.Controls;

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Accel.App.Services;

/// <summary>
/// The Ctrl+F find bar for panel D's document panes - see DocumentSearchBar.xaml for the layout and
/// why it floats rather than docks.
///
/// <para>It owns only presentation state (the query, the option toggles, which match is current);
/// the search itself is <see cref="TextSearchEngine"/>'s, and everything document-shaped goes through
/// the <see cref="IDocumentSearchView"/> handed to <see cref="Attach"/> - which is what lets one bar
/// serve both an AvalonEdit pane and the diff view's <c>RichTextBox</c> pane without knowing which it
/// is pointed at.</para>
/// </summary>
public partial class DocumentSearchBar : UserControl
{
    private IDocumentSearchView? _view;
    private IReadOnlyList<TextSearchMatch> _matches = Array.Empty<TextSearchMatch>();
    private int _currentIndex = -1;
    private bool _truncated;

    public DocumentSearchBar()
    {
        InitializeComponent();
        UpdateStatus();
    }

    /// <summary>Whether the bar is showing. The window's Ctrl+F/F3/Escape handling reads this to
    /// decide between opening the bar and stepping through the matches it already has.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>
    /// Points the bar at the control now showing the document. Called once per AvalonEdit pane (they
    /// keep the same control across tabs) and on every diff tab for the "After" side, which switches
    /// control depending on whether that side is editable - see <c>MainWindow.ShowGitDiffTabAsync</c>.
    /// Clears the outgoing view's highlights, then re-runs the query against the new one so an open
    /// bar stays meaningful instead of pointing at stale offsets.
    /// </summary>
    public void Attach(IDocumentSearchView? view)
    {
        if (ReferenceEquals(_view, view))
        {
            return;
        }

        _view?.ClearMatches();
        _view = view;
        Rerun(keepCurrentMatch: false);
    }

    /// <summary>Shows the bar and puts the caret in the query box, selecting whatever query was there
    /// before so typing replaces it - the standard Ctrl+F-on-an-already-open-bar behaviour. When it is
    /// already open this only re-focuses: re-running would scroll the view out from under a user who
    /// pressed Ctrl+F merely to get back to the box.</summary>
    public void Open()
    {
        if (_view is null)
        {
            return;
        }

        bool wasOpen = IsOpen;
        Visibility = Visibility.Visible;

        if (!wasOpen)
        {
            Rerun(keepCurrentMatch: false);
        }

        // Deferred to Input priority: the bar was Collapsed until a line ago, and focusing an element
        // WPF has not yet laid out silently fails, leaving the user typing into the document instead
        // of the find box.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                QueryBox.Focus();
                QueryBox.SelectAll();
            }));
    }

    /// <summary>Hides the bar, drops its highlights and hands focus back to the document. The query
    /// text is deliberately kept, so the next Ctrl+F re-offers it.</summary>
    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        Visibility = Visibility.Collapsed;
        _matches = Array.Empty<TextSearchMatch>();
        _currentIndex = -1;
        UpdateStatus();

        _view?.ClearMatches();
        _view?.FocusDocument();
    }

    /// <summary>Re-runs the query because the document changed underneath the bar (a different tab, a
    /// reload, an edit) - every offset the bar is holding is stale at that point. A no-op while
    /// closed.</summary>
    public void Refresh()
    {
        if (IsOpen)
        {
            Rerun(keepCurrentMatch: true);
        }
    }

    public void FindNext() => Step(forward: true);

    public void FindPrevious() => Step(forward: false);

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e) => Rerun(keepCurrentMatch: true);

    private void Option_Changed(object sender, RoutedEventArgs e) => Rerun(keepCurrentMatch: true);

    private void NextButton_Click(object sender, RoutedEventArgs e) => FindNext();

    private void PreviousButton_Click(object sender, RoutedEventArgs e) => FindPrevious();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// The bar's own keyboard contract, handled at Preview so the <see cref="TextBox"/> underneath
    /// never gets a chance to swallow Enter/Escape. <see cref="Key.System"/> unwrapping is needed for
    /// the Alt+C/Alt+W option shortcuts: WPF reports any Alt combination as
    /// <see cref="Key.System"/> with the real key in <see cref="KeyEventArgs.SystemKey"/>.
    /// </summary>
    private void QueryBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        switch (key)
        {
            case Key.Enter or Key.F3 when shift:
                FindPrevious();
                break;
            case Key.Enter or Key.F3:
                FindNext();
                break;
            case Key.Escape:
                Close();
                break;
            case Key.C when alt:
                MatchCaseToggle.IsChecked = MatchCaseToggle.IsChecked != true;
                break;
            case Key.W when alt:
                WholeWordToggle.IsChecked = WholeWordToggle.IsChecked != true;
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Searches the attached view from scratch and re-marks it.
    ///
    /// <para><paramref name="keepCurrentMatch"/> chooses the anchor the new match set is seeded from:
    /// the match the bar was already on (find-as-you-type - extending "fo" to "foo" must not throw the
    /// user back to the top of the file), or the document's own caret (a fresh open, or a new
    /// document, where the caret is the only sensible starting point).</para>
    /// </summary>
    private void Rerun(bool keepCurrentMatch)
    {
        // The IsOpen guard is what keeps a dismissed bar inert: the query survives Close (so the next
        // Ctrl+F re-offers it), and Attach is called on every diff tab whether the bar is open or not -
        // without this, opening a diff tab after closing the bar would silently highlight and scroll
        // that pane to a match nobody asked for.
        if (_view is null || !IsOpen)
        {
            _matches = Array.Empty<TextSearchMatch>();
            _currentIndex = -1;
            _truncated = false;
            UpdateStatus();
            return;
        }

        int anchor = keepCurrentMatch && _currentIndex >= 0 && _currentIndex < _matches.Count
            ? _matches[_currentIndex].Offset
            : _view.CaretOffset;

        var result = TextSearchEngine.FindAll(
            _view.Text,
            QueryBox.Text,
            MatchCaseToggle.IsChecked == true,
            WholeWordToggle.IsChecked == true);

        _matches = result.Matches;
        _truncated = result.Truncated;
        _currentIndex = _matches.Count == 0 ? -1 : TextSearchEngine.IndexAtOrAfter(_matches, anchor);

        _view.ShowMatches(_matches, _currentIndex);
        if (_currentIndex >= 0)
        {
            _view.Reveal(_matches[_currentIndex]);
        }

        UpdateStatus();
    }

    /// <summary>Moves to the next/previous match, wrapping at either end (see
    /// <see cref="TextSearchEngine.Next"/>). The match set itself is not recomputed - only which one
    /// is current - so stepping stays cheap on a large file.</summary>
    private void Step(bool forward)
    {
        if (_view is null || _matches.Count == 0)
        {
            return;
        }

        _currentIndex = forward
            ? TextSearchEngine.Next(_currentIndex, _matches.Count)
            : TextSearchEngine.Previous(_currentIndex, _matches.Count);

        _view.ShowMatches(_matches, _currentIndex);
        _view.Reveal(_matches[_currentIndex]);
        UpdateStatus();
    }

    /// <summary>
    /// The "3/17" counter and the prev/next enablement. Blank (not "0 results") while the query is
    /// empty or the bar is closed, so neither an untouched nor a dismissed bar reads as a failed
    /// search - <see cref="Close"/> drops its matches but keeps the query for next time. A trailing
    /// "+" means <see cref="TextSearchEngine.MaxMatches"/> capped the scan, i.e. "at least this many".
    /// </summary>
    private void UpdateStatus()
    {
        StatusText.Text = !IsOpen || QueryBox.Text.Length == 0
            ? string.Empty
            : _matches.Count == 0
                ? "No results"
                : $"{_currentIndex + 1}/{_matches.Count}{(_truncated ? "+" : string.Empty)}";

        PreviousButton.IsEnabled = _matches.Count > 0;
        NextButton.IsEnabled = _matches.Count > 0;
    }
}

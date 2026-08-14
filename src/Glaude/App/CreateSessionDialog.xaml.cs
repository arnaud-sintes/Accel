namespace Glaude.App;

using System.ComponentModel;
using System.Windows;
using Glaude.App.ViewModels;

/// <summary>
/// P2-T6's "Create session" popup: thin code-behind over <see cref="CreateSessionDialogViewModel"/>.
/// All the argv/launch logic lives in the ViewModel (unit-testable headlessly); this class only
/// wires <c>DataContext</c>, sets the advanced-args warning label's text from the ViewModel's single
/// source of truth (<see cref="CreateSessionDialogViewModel.AdvancedArgsWarning"/>), toggles the
/// error label's visibility, and closes itself when the ViewModel asks
/// (<see cref="CreateSessionDialogViewModel.RequestClose"/>).
/// </summary>
public partial class CreateSessionDialog : Window
{
    private readonly CreateSessionDialogViewModel _viewModel;

    public CreateSessionDialog(CreateSessionDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = _viewModel;

        ExtraArgsWarningText.Text = CreateSessionDialogViewModel.AdvancedArgsWarning;
        UpdateErrorVisibility();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.RequestClose += OnRequestClose;

        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.RequestClose -= OnRequestClose;
        };
    }

    /// <summary>
    /// Whether the dialog was confirmed (true, <see cref="CreateSessionDialogViewModel.LastStartedSession"/>
    /// is set) vs cancelled/closed without confirming (false). Mirrors <see cref="Window.DialogResult"/>
    /// but does not depend on this window having been shown via <c>ShowDialog</c>.
    /// </summary>
    public bool Confirmed { get; private set; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CreateSessionDialogViewModel.ErrorMessage) or null)
        {
            UpdateErrorVisibility();
        }
    }

    private void UpdateErrorVisibility() =>
        ErrorText.Visibility = string.IsNullOrEmpty(_viewModel.ErrorMessage) ? Visibility.Collapsed : Visibility.Visible;

    private void OnRequestClose(object? sender, bool confirmed)
    {
        Confirmed = confirmed;

        // Only set DialogResult when actually shown modally (ShowDialog) - it throws otherwise
        // (e.g. this window was shown non-modally, or from a headless test that never called
        // ShowDialog at all).
        if (IsLoaded)
        {
            try
            {
                DialogResult = confirmed;
            }
            catch (InvalidOperationException)
            {
                // Not shown via ShowDialog - Close() below still ends it.
            }
        }

        Close();
    }
}

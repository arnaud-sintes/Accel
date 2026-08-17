namespace Accel.App;

using System;
using System.ComponentModel;
using System.Windows;
using Accel.App.ViewModels;

/// <summary>
/// P4-T2's "Rename session" popup: thin code-behind over <see cref="RenameSessionDialogViewModel"/>,
/// same shape as <see cref="CreateSessionDialog"/> - all validation lives in the ViewModel, this class
/// only wires <c>DataContext</c>, toggles the error label's visibility, and closes itself when the
/// ViewModel asks.
/// </summary>
public partial class RenameSessionDialog : Window
{
    private readonly RenameSessionDialogViewModel _viewModel;

    public RenameSessionDialog(RenameSessionDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = _viewModel;

        UpdateErrorVisibility();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.RequestClose += OnRequestClose;

        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.RequestClose -= OnRequestClose;
        };
    }

    /// <summary>Whether the dialog was confirmed (true, <see cref="RenameSessionDialogViewModel.ConfirmedName"/>
    /// is set) vs cancelled/closed without confirming (false).</summary>
    public bool Confirmed { get; private set; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RenameSessionDialogViewModel.ErrorMessage) or null)
        {
            UpdateErrorVisibility();
        }
    }

    private void UpdateErrorVisibility() =>
        ErrorText.Visibility = string.IsNullOrEmpty(_viewModel.ErrorMessage) ? Visibility.Collapsed : Visibility.Visible;

    private void OnRequestClose(object? sender, bool confirmed)
    {
        Confirmed = confirmed;

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

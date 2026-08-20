namespace Accel.App;

using System;
using System.ComponentModel;
using System.Windows;
using Accel.App.ViewModels;

/// <summary>
/// The FILES panel's "Rename / Move…" popup: thin code-behind over
/// <see cref="MoveRenameDialogViewModel"/>, same shape as <see cref="RenameSessionDialog"/> - all
/// validation lives in the ViewModel, this class only wires <c>DataContext</c>, toggles the error
/// label's visibility, and closes itself when the ViewModel asks.
/// </summary>
public partial class MoveRenameDialog : Window
{
    private readonly MoveRenameDialogViewModel _viewModel;

    public MoveRenameDialog(MoveRenameDialogViewModel viewModel)
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

    /// <summary>Whether the dialog was confirmed (true, <see cref="MoveRenameDialogViewModel.ConfirmedNewPath"/>
    /// is set) vs cancelled/closed without confirming (false).</summary>
    public bool Confirmed { get; private set; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MoveRenameDialogViewModel.ErrorMessage) or null)
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

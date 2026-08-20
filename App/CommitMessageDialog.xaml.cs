namespace Accel.App;

using System;
using System.ComponentModel;
using System.Windows;
using Accel.App.ViewModels;

/// <summary>
/// The GIT panel's "Commit" popup: thin code-behind over
/// <see cref="CommitMessageDialogViewModel"/>, same shape as <see cref="NewEntryDialog"/> - all
/// validation lives in the ViewModel, this class only wires <c>DataContext</c>, toggles the error
/// label's visibility, and closes itself when the ViewModel asks.
/// </summary>
public partial class CommitMessageDialog : Window
{
    private readonly CommitMessageDialogViewModel _viewModel;

    public CommitMessageDialog(CommitMessageDialogViewModel viewModel)
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

    /// <summary>Whether the dialog was confirmed (true, <see cref="CommitMessageDialogViewModel.ConfirmedMessage"/>
    /// is set) vs cancelled/closed without confirming (false).</summary>
    public bool Confirmed { get; private set; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CommitMessageDialogViewModel.ErrorMessage) or null)
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

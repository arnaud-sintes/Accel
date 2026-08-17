namespace Accel.App;

using System.Windows;
using Accel.App.ViewModels;

/// <summary>
/// "Edit launch args…" popup: thin code-behind over <see cref="EditSessionArgsDialogViewModel"/>,
/// same shape as <see cref="RenameSessionDialog"/>/<see cref="CreateSessionDialog"/> - all state and
/// the argv-building logic live in the ViewModel; this class only wires <c>DataContext</c>, sets the
/// advanced-args warning label's text, and closes itself when the ViewModel asks.
/// </summary>
public partial class EditSessionArgsDialog : Window
{
    private readonly EditSessionArgsDialogViewModel _viewModel;

    public EditSessionArgsDialog(EditSessionArgsDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = _viewModel;

        ExtraArgsWarningText.Text = EditSessionArgsDialogViewModel.AdvancedArgsWarning;

        _viewModel.RequestClose += OnRequestClose;
        Closed += (_, _) => _viewModel.RequestClose -= OnRequestClose;
    }

    /// <summary>Whether the dialog was confirmed (true, <see cref="EditSessionArgsDialogViewModel.ConfirmedArguments"/>
    /// is set) vs cancelled/closed without confirming (false).</summary>
    public bool Confirmed { get; private set; }

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

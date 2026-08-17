namespace Accel.App.ViewModels;

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Accel.Orchestration;

/// <summary>
/// P4-T2's "Rename session" popup's ViewModel - the same no-WPF-dependency shape as
/// <see cref="CreateSessionDialogViewModel"/>: pure validation logic here, dialog code-behind
/// (<c>RenameSessionDialog</c>) only wires <c>DataContext</c> and reacts to <see cref="RequestClose"/>.
/// The actual PTY injection (writing <c>/rename &lt;name&gt;</c> into a live session and polling its
/// status file) is deliberately <b>not</b> this class's job - it happens in
/// <c>MainWindow.RenameSession_Click</c>, once this dialog has produced a validated
/// <see cref="ConfirmedName"/>, mirroring how <c>CreateSessionDialogViewModel</c> never touches
/// <see cref="Accel.Server.RootFoldersConfig"/> itself.
/// </summary>
public sealed partial class RenameSessionDialogViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string? _errorMessage;

    public RenameSessionDialogViewModel(string? initialName)
    {
        _name = initialName ?? string.Empty;
    }

    /// <summary>The validated name from the most recent successful <see cref="Confirm"/>, or null before
    /// one has happened (or after one that failed validation).</summary>
    public string? ConfirmedName { get; private set; }

    /// <summary>Raised once <see cref="Confirm"/> succeeds or <see cref="Cancel"/> runs. The bool is
    /// "confirmed" (true, <see cref="ConfirmedName"/> is set) vs "cancelled" (false) - same contract as
    /// <see cref="CreateSessionDialogViewModel.RequestClose"/>.</summary>
    public event EventHandler<bool>? RequestClose;

    /// <summary>
    /// Validates <see cref="Name"/> with the exact same rule the PTY write path will apply
    /// (<see cref="SlashCommandInputSanitizer.TryValidate"/>) - so a name that would be rejected at
    /// injection time is caught here instead, before any dialog is closed or any byte reaches the
    /// session. A blank name is also rejected (nothing meaningful to rename to).
    /// </summary>
    [RelayCommand]
    private void Confirm()
    {
        string trimmed = Name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            ErrorMessage = "Enter a name.";
            return;
        }

        if (!SlashCommandInputSanitizer.TryValidate(trimmed, out string? rejectionReason))
        {
            ErrorMessage = rejectionReason;
            return;
        }

        ErrorMessage = null;
        ConfirmedName = trimmed;
        RequestClose?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, false);
}

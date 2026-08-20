namespace Accel.App.ViewModels;

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

/// <summary>
/// The GIT panel's "Commit" popup's ViewModel - same no-WPF-dependency shape as
/// <see cref="NewEntryDialogViewModel"/>. Validation here is just "message is non-empty"; git
/// itself is the authoritative check (e.g. nothing staged), surfaced back to the user after this
/// dialog has already closed.
/// </summary>
public sealed partial class CommitMessageDialogViewModel : ObservableObject
{
    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public CommitMessageDialogViewModel(int stagedFileCount)
    {
        StagedFileCount = stagedFileCount;
    }

    public int StagedFileCount { get; }

    public string Title => "Commit changes";

    public string PromptLabel => StagedFileCount == 1
        ? "Committing 1 staged file"
        : $"Committing {StagedFileCount} staged files";

    public string ConfirmButtonText => "Commit";

    /// <summary>The validated message from the most recent successful <see cref="Confirm"/>, or
    /// null before one has happened (or after one that failed validation).</summary>
    public string? ConfirmedMessage { get; private set; }

    /// <summary>Raised once <see cref="Confirm"/> succeeds or <see cref="Cancel"/> runs -
    /// "confirmed" (true, <see cref="ConfirmedMessage"/> is set) vs "cancelled" (false).</summary>
    public event EventHandler<bool>? RequestClose;

    [RelayCommand]
    private void Confirm()
    {
        string trimmed = Message?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            ErrorMessage = "Enter a commit message.";
            return;
        }

        ErrorMessage = null;
        ConfirmedMessage = trimmed;
        RequestClose?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, false);
}

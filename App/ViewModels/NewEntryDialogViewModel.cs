namespace Accel.App.ViewModels;

using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

/// <summary>What kind of entry <see cref="NewEntryDialogViewModel"/> is prompting a name for - only
/// changes the dialog's title/prompt copy, never its validation.</summary>
public enum NewFileSystemEntryKind
{
    File,
    Folder,
}

/// <summary>
/// The FILES panel's "New File…"/"New Folder…" name-entry popup's ViewModel - same no-WPF-dependency
/// shape as <see cref="RenameSessionDialogViewModel"/>. This dialog's own validation is a best-effort,
/// immediate-feedback pre-check only; the authoritative check happens once, in
/// <see cref="Accel.Orchestration.FileSystemEntryPlanner"/>, after this dialog has already closed.
/// </summary>
public sealed partial class NewEntryDialogViewModel : ObservableObject
{
    private readonly string _parentDirectoryPath;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public NewEntryDialogViewModel(NewFileSystemEntryKind kind, string parentDirectoryPath)
    {
        Kind = kind;
        _parentDirectoryPath = parentDirectoryPath ?? string.Empty;
    }

    public NewFileSystemEntryKind Kind { get; }

    public string Title => Kind == NewFileSystemEntryKind.File ? "New file" : "New folder";

    public string PromptLabel => Kind == NewFileSystemEntryKind.File ? "File name" : "Folder name";

    public string ConfirmButtonText => "Create";

    /// <summary>The validated name from the most recent successful <see cref="Confirm"/>, or null
    /// before one has happened (or after one that failed validation).</summary>
    public string? ConfirmedName { get; private set; }

    /// <summary>Raised once <see cref="Confirm"/> succeeds or <see cref="Cancel"/> runs - "confirmed"
    /// (true, <see cref="ConfirmedName"/> is set) vs "cancelled" (false).</summary>
    public event EventHandler<bool>? RequestClose;

    [RelayCommand]
    private void Confirm()
    {
        string trimmed = Name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            ErrorMessage = "Enter a name.";
            return;
        }

        if (trimmed is "." or "..")
        {
            ErrorMessage = $"'{trimmed}' is not a valid name.";
            return;
        }

        foreach (char c in trimmed)
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0)
            {
                ErrorMessage = $"'{trimmed}' contains an invalid character ('{c}').";
                return;
            }
        }

        if (!string.IsNullOrEmpty(_parentDirectoryPath))
        {
            string candidate = Path.Combine(_parentDirectoryPath, trimmed);
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                ErrorMessage = $"'{trimmed}' already exists here.";
                return;
            }
        }

        ErrorMessage = null;
        ConfirmedName = trimmed;
        RequestClose?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, false);
}

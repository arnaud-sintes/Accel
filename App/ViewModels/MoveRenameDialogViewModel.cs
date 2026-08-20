namespace Accel.App.ViewModels;

using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Accel.App.Services;

/// <summary>
/// The FILES panel's "Rename / Move…" dialog's ViewModel - shows the current path read-only, and an
/// editable new-path field pre-filled with it (plus a Browse button to pick a different destination
/// folder while keeping the already-typed filename). Same no-WPF-dependency shape as
/// <see cref="RenameSessionDialogViewModel"/>; the authoritative validation (containment, collision,
/// move-into-self) happens once, in <see cref="Accel.Orchestration.FileSystemEntryPlanner.PlanMove"/>,
/// after this dialog has already closed.
/// </summary>
public sealed partial class MoveRenameDialogViewModel : ObservableObject
{
    private readonly IFolderPickerService _folderPicker;

    [ObservableProperty]
    private string _newPath;

    [ObservableProperty]
    private string? _errorMessage;

    public MoveRenameDialogViewModel(string currentFullPath, bool isDirectory, IFolderPickerService folderPicker)
    {
        CurrentPath = currentFullPath ?? string.Empty;
        IsDirectory = isDirectory;
        _folderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));
        _newPath = CurrentPath;
    }

    public string CurrentPath { get; }

    public bool IsDirectory { get; }

    /// <summary>The validated new path from the most recent successful <see cref="Confirm"/>, or null
    /// before one has happened (or after one that failed validation).</summary>
    public string? ConfirmedNewPath { get; private set; }

    /// <summary>Raised once <see cref="Confirm"/> succeeds or <see cref="Cancel"/> runs - "confirmed"
    /// (true, <see cref="ConfirmedNewPath"/> is set) vs "cancelled" (false).</summary>
    public event EventHandler<bool>? RequestClose;

    /// <summary>Replaces <see cref="NewPath"/>'s destination folder while preserving whatever filename
    /// leaf the user has already typed - Browse only ever picks a folder, never a full path.</summary>
    [RelayCommand]
    private void Browse()
    {
        string? folder = _folderPicker.PickFolder("Select a destination folder");
        if (folder is null)
        {
            return;
        }

        string current = NewPath ?? string.Empty;
        string leaf = Path.GetFileName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        NewPath = string.IsNullOrEmpty(leaf) ? folder : Path.Combine(folder, leaf);
    }

    [RelayCommand]
    private void Confirm()
    {
        string trimmed = NewPath?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            ErrorMessage = "Enter a path.";
            return;
        }

        if (string.Equals(trimmed, CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = "Choose a different name or location.";
            return;
        }

        ErrorMessage = null;
        ConfirmedNewPath = trimmed;
        RequestClose?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, false);
}

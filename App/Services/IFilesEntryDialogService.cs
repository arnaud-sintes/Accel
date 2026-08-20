namespace Accel.App.Services;

using System.Windows;
using Accel.App.ViewModels;

/// <summary>
/// The seam through which panel B's create/rename/move commands ask the user for input. Exists as an
/// interface purely so <see cref="Accel.App.ViewModels.FilesPanelViewModel"/> is unit-testable
/// headlessly - tests supply a fake that returns a fixed string (or null, for "cancelled") without a
/// real dialog ever appearing, the same role <see cref="IFolderPickerService"/> plays for panel A.
/// </summary>
public interface IFilesEntryDialogService
{
    /// <summary>Prompts for a new file/folder name. Returns the validated name, or
    /// <see langword="null"/> if the user cancelled.</summary>
    string? PromptForNewEntryName(NewFileSystemEntryKind kind, string parentDirectoryPath);

    /// <summary>Prompts for a move/rename destination, pre-filled with <paramref name="currentFullPath"/>.
    /// Returns the candidate new path, or <see langword="null"/> if the user cancelled.</summary>
    string? PromptForMoveDestination(string currentFullPath, bool isDirectory);
}

/// <summary>Production <see cref="IFilesEntryDialogService"/>: the real WPF dialogs
/// (<see cref="NewEntryDialog"/>/<see cref="MoveRenameDialog"/>), owned end-to-end here so
/// <see cref="Accel.App.ViewModels.FilesPanelViewModel"/> itself never needs a WPF or
/// <see cref="IFolderPickerService"/> dependency of its own.</summary>
public sealed class WpfFilesEntryDialogService : IFilesEntryDialogService
{
    public string? PromptForNewEntryName(NewFileSystemEntryKind kind, string parentDirectoryPath)
    {
        var viewModel = new NewEntryDialogViewModel(kind, parentDirectoryPath);
        var dialog = new Accel.App.NewEntryDialog(viewModel) { Owner = Application.Current?.MainWindow };
        dialog.ShowDialog();
        return dialog.Confirmed ? viewModel.ConfirmedName : null;
    }

    public string? PromptForMoveDestination(string currentFullPath, bool isDirectory)
    {
        var viewModel = new MoveRenameDialogViewModel(currentFullPath, isDirectory, new WinFormsFolderPickerService());
        var dialog = new Accel.App.MoveRenameDialog(viewModel) { Owner = Application.Current?.MainWindow };
        dialog.ShowDialog();
        return dialog.Confirmed ? viewModel.ConfirmedNewPath : null;
    }
}

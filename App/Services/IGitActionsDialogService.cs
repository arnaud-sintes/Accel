namespace Accel.App.Services;

using System.Windows;
using Accel.App.ViewModels;

/// <summary>
/// The seam through which the GIT panel's commit action asks the user for a commit message.
/// Exists as an interface purely so <see cref="Accel.App.ViewModels.GitPanelViewModel"/> is
/// unit-testable headlessly, the same role <see cref="IFilesEntryDialogService"/> plays for panel
/// B's file-tree create/rename/move commands.
/// </summary>
public interface IGitActionsDialogService
{
    /// <summary>Prompts for a commit message. Returns the message, or <see langword="null"/> if
    /// the user cancelled.</summary>
    string? PromptForCommitMessage(int stagedFileCount);
}

/// <summary>Production <see cref="IGitActionsDialogService"/>: the real WPF
/// <see cref="CommitMessageDialog"/>, owned end-to-end here so
/// <see cref="Accel.App.ViewModels.GitPanelViewModel"/> itself never needs a WPF dependency of its
/// own.</summary>
public sealed class WpfGitActionsDialogService : IGitActionsDialogService
{
    public string? PromptForCommitMessage(int stagedFileCount)
    {
        var viewModel = new CommitMessageDialogViewModel(stagedFileCount);
        var dialog = new Accel.App.CommitMessageDialog(viewModel) { Owner = Application.Current?.MainWindow };
        dialog.ShowDialog();
        return dialog.Confirmed ? viewModel.ConfirmedMessage : null;
    }
}

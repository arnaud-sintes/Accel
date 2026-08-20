namespace Accel.App.Services;

/// <summary>
/// The seam through which panel B's Delete/Delete Permanently confirmations are shown. A deliberately
/// separate interface from <see cref="IUserConfirmationService"/> rather than a second method on it:
/// that one is hardcoded to <see cref="Accel.App.AccelDialogIcon.Question"/>, right for "stop
/// monitoring" but wrong for either delete tier, and the two tiers here need visibly different
/// wording/icons (<see cref="Accel.App.AccelDialogIcon.Warning"/> vs
/// <see cref="Accel.App.AccelDialogIcon.Error"/>), not just a different message string.
/// </summary>
public interface IFilesEntryConfirmationService
{
    /// <summary>Recycle-bin delete confirmation - recoverable, so a lighter-weight warning.</summary>
    bool ConfirmDelete(string name, bool isDirectory);

    /// <summary>Permanent delete confirmation - irrecoverable, so a stronger warning.</summary>
    bool ConfirmPermanentDelete(string name, bool isDirectory);

    /// <summary>Discard-changes confirmation for the GIT panel - tiered the same way delete is:
    /// discarding only unstaged edits is a lighter warning, while discarding a file that also has
    /// staged changes unwinds a `git add` too, so it gets the stronger error-tier wording.</summary>
    bool ConfirmDiscardChanges(string path, bool isStaged);
}

/// <summary>Production <see cref="IFilesEntryConfirmationService"/>: the themed
/// <see cref="Accel.App.AccelMessageDialog"/>, same as <see cref="MessageBoxConfirmationService"/>.</summary>
public sealed class MessageBoxFilesEntryConfirmationService : IFilesEntryConfirmationService
{
    public bool ConfirmDelete(string name, bool isDirectory) => Accel.App.AccelMessageDialog.ShowConfirm(
        null,
        $"Move \"{name}\" to the recycle bin?" + (isDirectory ? " This folder and everything inside it will be moved." : ""),
        "Delete",
        Accel.App.AccelDialogIcon.Warning);

    public bool ConfirmPermanentDelete(string name, bool isDirectory) => Accel.App.AccelMessageDialog.ShowConfirm(
        null,
        $"Permanently delete \"{name}\"?" + (isDirectory ? " Everything inside this folder will be deleted." : "") +
        " This cannot be undone.",
        "Delete permanently",
        Accel.App.AccelDialogIcon.Error);

    public bool ConfirmDiscardChanges(string path, bool isStaged) => Accel.App.AccelMessageDialog.ShowConfirm(
        null,
        isStaged
            ? $"Discard all changes to \"{path}\", including staged changes? This cannot be undone."
            : $"Discard changes to \"{path}\"? This cannot be undone.",
        "Discard changes",
        isStaged ? Accel.App.AccelDialogIcon.Error : Accel.App.AccelDialogIcon.Warning);
}

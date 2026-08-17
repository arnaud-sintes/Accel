namespace Accel.App.Services;

/// <summary>
/// P1-T3b: the seam through which panel A's "Add root…" command asks the user for a folder.
/// Exists as an interface purely so <see cref="Accel.App.ViewModels.RootsPanelViewModel"/> is
/// unit-testable headlessly - tests supply a fake that returns a fixed path (or null, for
/// "cancelled") without a real dialog ever appearing.
/// </summary>
public interface IFolderPickerService
{
    /// <summary>
    /// Prompts the user to pick a folder. Returns the chosen path, or <see langword="null"/> if
    /// the user cancelled.
    /// </summary>
    string? PickFolder(string description);
}

/// <summary>
/// Production <see cref="IFolderPickerService"/>: a <see cref="System.Windows.Forms.FolderBrowserDialog"/>.
/// <c>UseWindowsForms</c> is already enabled alongside WPF on this project's TFM (see
/// <c>Accel.csproj</c>), so this reuses that rather than hand-rolling a WPF folder picker.
/// </summary>
public sealed class WinFormsFolderPickerService : IFolderPickerService
{
    public string? PickFolder(string description)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? dialog.SelectedPath
            : null;
    }
}

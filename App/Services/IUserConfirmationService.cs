namespace Accel.App.Services;

/// <summary>
/// P1-T3b: the seam through which panel A's "stop monitoring this folder" confirmation is shown.
/// Exists as an interface purely so <see cref="Accel.App.ViewModels.RootsPanelViewModel"/> is
/// unit-testable headlessly - tests supply a fake that returns a fixed yes/no without a real
/// dialog ever appearing.
/// </summary>
public interface IUserConfirmationService
{
    /// <summary>Shows a yes/no prompt and returns whether the user confirmed.</summary>
    bool Confirm(string message, string title);
}

/// <summary>Production <see cref="IUserConfirmationService"/>: the themed
/// <see cref="Accel.App.AccelMessageDialog"/> (Theme.xaml + CustomTitleBar), not a native
/// <see cref="System.Windows.MessageBox"/> - see AccelMessageDialog's doc comment for why.</summary>
public sealed class MessageBoxConfirmationService : IUserConfirmationService
{
    public bool Confirm(string message, string title) =>
        Accel.App.AccelMessageDialog.ShowConfirm(null, message, title, Accel.App.AccelDialogIcon.Question);
}

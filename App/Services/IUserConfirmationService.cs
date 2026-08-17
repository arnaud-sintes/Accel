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

/// <summary>Production <see cref="IUserConfirmationService"/>: a WPF <see cref="System.Windows.MessageBox"/>.</summary>
public sealed class MessageBoxConfirmationService : IUserConfirmationService
{
    public bool Confirm(string message, string title) =>
        System.Windows.MessageBox.Show(
            message,
            title,
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;
}

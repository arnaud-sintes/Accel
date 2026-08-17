namespace Accel.App;

using System.Windows;
using System.Windows.Media;

/// <summary>Which glyph/accent colour <see cref="AccelMessageDialog"/> shows - the themed
/// equivalent of <see cref="System.Windows.MessageBoxImage"/>.</summary>
public enum AccelDialogIcon
{
    Info,
    Question,
    Warning,
    Error,
}

/// <summary>Which button row <see cref="AccelMessageDialog"/> shows - the themed equivalent of
/// <see cref="System.Windows.MessageBoxButton"/> (only the two shapes this app actually uses).</summary>
public enum AccelDialogButtons
{
    Ok,
    YesNo,
}

/// <summary>
/// The themed replacement for every native <see cref="System.Windows.MessageBox"/> call in the
/// app (info/error notices and yes-no confirmations) - see AccelMessageDialog.xaml for the shared
/// shell (Theme.xaml + CustomTitleBar) this reuses from CreateSessionDialog/RenameSessionDialog.
///
/// <para>Two static entry points mirror the two shapes callers need:
/// <see cref="ShowMessage"/> (OK only) and <see cref="ShowConfirm"/> (Yes/No, returns the choice).
/// Both default the owner to <see cref="Application.Current"/>'s <see cref="Application.MainWindow"/>
/// when the caller doesn't have a specific owner window at hand (e.g. a ViewModel with no WPF
/// reference), so the dialog still centers over the app rather than the whole screen.</para>
/// </summary>
public partial class AccelMessageDialog : Window
{
    // Segoe MDL2 Assets glyphs (private-use-area code points, escaped so the source file stays
    // plain ASCII regardless of editor/encoding): Info, Help (question), Warning, StatusErrorFull.
    private const string InfoGlyph = "\uE946";
    private const string QuestionGlyph = "\uE897";
    private const string WarningGlyph = "\uE7BA";
    private const string ErrorGlyph = "\uE783";

    public bool Confirmed { get; private set; }

    public AccelMessageDialog(string message, string title, AccelDialogIcon icon, AccelDialogButtons buttons)
    {
        InitializeComponent();

        Title = title;
        TitleBar.TitleText = title;
        MessageText.Text = message;

        (IconText.Text, IconText.Foreground) = icon switch
        {
            AccelDialogIcon.Info => (InfoGlyph, (Brush)FindResource("TealBrush")),
            AccelDialogIcon.Question => (QuestionGlyph, (Brush)FindResource("AccentBrush")),
            AccelDialogIcon.Warning => (WarningGlyph, (Brush)FindResource("WarningTextBrush")),
            AccelDialogIcon.Error => (ErrorGlyph, (Brush)FindResource("DangerBrush")),
            _ => (InfoGlyph, (Brush)FindResource("TealBrush")),
        };

        if (buttons == AccelDialogButtons.Ok)
        {
            YesButton.Visibility = Visibility.Collapsed;
            NoButton.Visibility = Visibility.Collapsed;
            OkButton.Visibility = Visibility.Visible;
        }
        else
        {
            OkButton.Visibility = Visibility.Collapsed;
            YesButton.Visibility = Visibility.Visible;
            NoButton.Visibility = Visibility.Visible;

            // Safe-by-default: "No" is the focused/Escape/close-box result for a yes-no prompt,
            // exactly like the native MessageBox.Show(..., MessageBoxResult.No) calls this
            // replaces - a stray Enter/Escape/click-outside never accidentally confirms.
            Loaded += (_, _) => NoButton.Focus();
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void YesButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void NoButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    /// <summary>Shows an OK-only notice (info/warning/error) - the themed replacement for
    /// <c>MessageBox.Show(owner, message, title, MessageBoxButton.OK, image)</c>.</summary>
    public static void ShowMessage(Window? owner, string message, string title, AccelDialogIcon icon = AccelDialogIcon.Info)
    {
        var dialog = new AccelMessageDialog(message, title, icon, AccelDialogButtons.Ok)
        {
            Owner = owner ?? Application.Current?.MainWindow,
        };
        dialog.ShowDialog();
    }

    /// <summary>Shows a Yes/No confirmation and returns whether the user chose Yes - the themed
    /// replacement for <c>MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, image, ...)
    /// == MessageBoxResult.Yes</c>.</summary>
    public static bool ShowConfirm(Window? owner, string message, string title, AccelDialogIcon icon = AccelDialogIcon.Warning)
    {
        var dialog = new AccelMessageDialog(message, title, icon, AccelDialogButtons.YesNo)
        {
            Owner = owner ?? Application.Current?.MainWindow,
        };
        dialog.ShowDialog();
        return dialog.Confirmed;
    }
}

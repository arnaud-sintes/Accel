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

    /// <summary>Two distinct actions plus Cancel - see <see cref="AccelMessageDialog.ShowChoice"/>.</summary>
    ThreeWay,
}

/// <summary>
/// Which of <see cref="AccelMessageDialog.ShowChoice"/>'s three buttons the user pressed. A distinct
/// type rather than a nullable bool because the point of the three-way shape is that "not the primary
/// action" and "do nothing" are different answers, and a caller must be forced to handle both.
/// </summary>
public enum AccelDialogChoice
{
    /// <summary>The user dismissed the dialog (Cancel button, Escape, or the close box). The safe
    /// default: every dismissal gesture maps here, so nothing destructive can happen by accident.</summary>
    Cancel,

    /// <summary>The left-hand action button.</summary>
    Secondary,

    /// <summary>The accented, right-hand action button.</summary>
    Primary,
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

    /// <summary>The three-way result, meaningful only for <see cref="AccelDialogButtons.ThreeWay"/>.
    /// Initialised to <see cref="AccelDialogChoice.Cancel"/> so a dialog closed by Escape or the
    /// title bar's close box - neither of which runs a button handler - still reports the
    /// do-nothing answer.</summary>
    public AccelDialogChoice Choice { get; private set; } = AccelDialogChoice.Cancel;

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
        else if (buttons == AccelDialogButtons.ThreeWay)
        {
            OkButton.Visibility = Visibility.Collapsed;
            YesButton.Visibility = Visibility.Visible;
            AltButton.Visibility = Visibility.Visible;
            NoButton.Visibility = Visibility.Visible;

            // Three self-describing labels do not fit the two-button shell's width, and a clipped
            // button in a prompt about losing work is the worst place to save 80 pixels.
            Width = 560;
            MessageText.MaxWidth = 440;

            // Same safe-by-default posture as the yes-no shape, and it matters more here: the two
            // action buttons of a conflict prompt each destroy one side of the work, so the focused
            // (and Escape/Enter) target has to be the one that destroys neither.
            Loaded += (_, _) => NoButton.Focus();
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
        Choice = AccelDialogChoice.Primary;
        Close();
    }

    private void AltButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = AccelDialogChoice.Secondary;
        Close();
    }

    private void NoButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Choice = AccelDialogChoice.Cancel;
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

    /// <summary>
    /// Shows two named actions plus Cancel and returns which one was chosen. For prompts where the
    /// alternative to the primary action is itself a real, different action rather than "do nothing"
    /// - the external-change conflict prompt (keep my version / reload from disk / cancel) being the
    /// case this was added for.
    /// </summary>
    /// <param name="primaryText">Label of the accented right-hand button, returned as
    /// <see cref="AccelDialogChoice.Primary"/>.</param>
    /// <param name="secondaryText">Label of the middle button, returned as
    /// <see cref="AccelDialogChoice.Secondary"/>.</param>
    /// <param name="cancelText">Label of the left-hand dismissal button. Also what Escape and the
    /// close box resolve to.</param>
    /// <remarks>
    /// Both action labels are the caller's to write, because a generic Yes/No pair is exactly what
    /// makes this class of prompt dangerous: the user has to be able to tell which button loses
    /// which side of the work from the button itself, not from the body text.
    /// </remarks>
    public static AccelDialogChoice ShowChoice(
        Window? owner,
        string message,
        string title,
        string primaryText,
        string secondaryText,
        string cancelText = "Cancel",
        AccelDialogIcon icon = AccelDialogIcon.Warning)
    {
        var dialog = new AccelMessageDialog(message, title, icon, AccelDialogButtons.ThreeWay)
        {
            Owner = owner ?? Application.Current?.MainWindow,
        };
        dialog.YesButton.Content = primaryText;
        dialog.AltButton.Content = secondaryText;
        dialog.NoButton.Content = cancelText;
        dialog.ShowDialog();
        return dialog.Choice;
    }
}

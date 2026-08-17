namespace Accel.App.Controls;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

/// <summary>
/// The custom title bar shown on all three of this app's windows (MainWindow,
/// CreateSessionDialog, RenameSessionDialog), replacing the native OS caption entirely - see
/// App/Theme.xaml's Window style (WindowStyle="None" + a matching WindowChrome.WindowChrome).
///
/// <para>Minimize/maximize are hidden on the two dialogs (<see cref="ShowMinimizeButton"/>/
/// <see cref="ShowMaximizeButton"/> both default <c>true</c>, set <c>false</c> where
/// <c>ResizeMode="NoResize"</c>) - toggling <see cref="Window.WindowState"/> on a fixed-size
/// dialog would make no sense. The close button always resolves <see cref="Window.GetWindow"/>
/// and calls the plain <see cref="Window.Close"/> - the exact same call both
/// <c>Program.cs</c>'s Ctrl+C handler and this app's existing teardown (registry/Terminal
/// disposal wired to <c>Window.Closed</c>) already use, so no new shutdown path is introduced
/// here.</para>
/// </summary>
public partial class CustomTitleBar : UserControl
{
    public static readonly DependencyProperty TitleTextProperty = DependencyProperty.Register(
        nameof(TitleText), typeof(string), typeof(CustomTitleBar), new PropertyMetadata(string.Empty));

    /// <summary>Optional version caption shown right after <see cref="TitleText"/>, lighter/smaller
    /// (TextMutedBrush, caption size) than the title itself - empty by default (the two dialogs that
    /// also host this control don't set it), which the view's StringToVis converter hides entirely
    /// rather than leaving a gap.</summary>
    public static readonly DependencyProperty VersionTextProperty = DependencyProperty.Register(
        nameof(VersionText), typeof(string), typeof(CustomTitleBar), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ShowMinimizeButtonProperty = DependencyProperty.Register(
        nameof(ShowMinimizeButton), typeof(bool), typeof(CustomTitleBar), new PropertyMetadata(true));

    public static readonly DependencyProperty ShowMaximizeButtonProperty = DependencyProperty.Register(
        nameof(ShowMaximizeButton), typeof(bool), typeof(CustomTitleBar), new PropertyMetadata(true));

    /// <summary>Segoe MDL2 Assets glyph for the maximize button in its two states - a single square
    /// (maximize) vs. two overlapping squares (restore), the same glyphs the native Windows caption
    /// uses for the same states.</summary>
    private const string MaximizeGlyph = "\uE922";
    private const string RestoreGlyph = "\uE923";

    private Window? _window;

    public CustomTitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Keeps the maximize button's glyph (and its screen-reader name) in sync with the owning
    /// window's actual state - a plain unconditional maximize icon would keep showing "maximize"
    /// even after the window is already maximized, unlike every native Windows title bar.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not { } window || ReferenceEquals(window, _window))
        {
            return;
        }

        _window = window;
        window.StateChanged += (_, _) => UpdateMaximizeButtonGlyph(window);
        UpdateMaximizeButtonGlyph(window);
    }

    private void UpdateMaximizeButtonGlyph(Window window)
    {
        bool isMaximized = window.WindowState == WindowState.Maximized;
        MaximizeButton.Content = isMaximized ? RestoreGlyph : MaximizeGlyph;
        AutomationProperties.SetName(MaximizeButton, isMaximized ? "Restore" : "Maximize");
    }

    /// <summary>See CustomTitleBar.xaml's comment on why this exists alongside WindowChrome's own
    /// caption hit-testing. Buttons opt out via <c>WindowChrome.IsHitTestVisibleInChrome</c> and mark
    /// their own click routed events handled, so a caption-button click never reaches this handler.</summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Window.GetWindow(this) is not { } window)
        {
            return;
        }

        if (e.ClickCount == 2 && ShowMaximizeButton)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            window.DragMove();
        }
    }

    public string TitleText
    {
        get => (string)GetValue(TitleTextProperty);
        set => SetValue(TitleTextProperty, value);
    }

    public string VersionText
    {
        get => (string)GetValue(VersionTextProperty);
        set => SetValue(VersionTextProperty, value);
    }

    public bool ShowMinimizeButton
    {
        get => (bool)GetValue(ShowMinimizeButtonProperty);
        set => SetValue(ShowMinimizeButtonProperty, value);
    }

    public bool ShowMaximizeButton
    {
        get => (bool)GetValue(ShowMaximizeButtonProperty);
        set => SetValue(ShowMaximizeButtonProperty, value);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Window.GetWindow(this)?.Close();
    }
}

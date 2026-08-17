namespace Accel.App.ViewModels;

/// <summary>
/// One resolved row visual state: the glyph shape, whether the row's label renders bold, the
/// accent colour, and an accessible text description - the four IsRunning x IsFocused
/// combinations panel A's rows render per locked-in decision 9. Colour is never the only signal:
/// <see cref="Glyph"/> alone distinguishes running/not-running, and <see cref="IsBold"/> alone
/// distinguishes focused/not-focused, so an accessibility mode that strips colour entirely still
/// lets a sighted user tell all four states apart.
/// </summary>
public readonly record struct SessionVisualState(string Glyph, bool IsBold, string ColorHex, string AutomationName);

/// <summary>
/// Pure (no WPF types) mapping from a session/agent row's <c>IsRunning</c> x <c>IsFocused</c>
/// state to <see cref="SessionVisualState"/>, per P1-T4 / locked-in decision 9.
///
/// <para><b>IsFocused is not really wired yet.</b> A real focus signal is <c>ISessionSelectionService</c>
/// (P3-T1) - later, not part of this task. Until then every caller in this codebase passes
/// <c>isFocused: false</c> (see <see cref="RootsPanelNodeViewModel.IsFocused"/>'s doc comment), so
/// only the "not focused" column of the table below is currently reachable at runtime. Both
/// columns are implemented and unit-tested regardless, so P3-T1 only has to supply a real boolean
/// - the visual mapping itself needs no further work.</para>
///
/// <para>The constants below are the single source of truth for both this resolver's own tests
/// and <c>MainWindow.xaml</c>'s <c>ItemContainerStyle</c> triggers, referenced from XAML via
/// <c>{x:Static}</c> so the two can never drift apart.</para>
/// </summary>
public static class SessionVisualStateResolver
{
    /// <summary>Filled circle - IsRunning=true.</summary>
    public const string RunningGlyph = "●";

    /// <summary>Hollow circle - IsRunning=false (covers both Historical and Stale nodes, which
    /// P1-T4 collapses onto the same "not running" side of this axis).</summary>
    public const string IdleGlyph = "○";

    /// <summary>Accent colour: running, focused. Dark-mode palette - see MainWindow.xaml's
    /// RunningFocusedBrush/RunningBrush/IdleFocusedBrush/IdleBrush, which must stay in sync with
    /// these four constants.</summary>
    public const string RunningFocusedColorHex = "#FF4FC3F7";

    /// <summary>Accent colour: running, not focused.</summary>
    public const string RunningColorHex = "#FF66BB6A";

    /// <summary>Accent colour: idle, focused.</summary>
    public const string IdleFocusedColorHex = "#FF9FA8DA";

    /// <summary>Accent colour: idle, not focused.</summary>
    public const string IdleColorHex = "#FF9E9E9E";

    public static SessionVisualState Resolve(bool isRunning, bool isFocused)
    {
        string glyph = isRunning ? RunningGlyph : IdleGlyph;
        bool isBold = isFocused;

        string colorHex = (isRunning, isFocused) switch
        {
            (true, true) => RunningFocusedColorHex,
            (true, false) => RunningColorHex,
            (false, true) => IdleFocusedColorHex,
            (false, false) => IdleColorHex,
        };

        string automationName = isRunning
            ? (isFocused ? "Running, focused" : "Running")
            : (isFocused ? "Idle, focused" : "Idle");

        return new SessionVisualState(glyph, isBold, colorHex, automationName);
    }
}

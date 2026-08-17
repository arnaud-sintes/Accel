namespace Accel.App;

using System.Windows;

/// <summary>
/// The WPF shell's <see cref="Application"/> subclass — the app's one and only real UI, wired in
/// directly from <c>Program.cs</c>'s <c>RunCombinedAsync</c>.
///
/// This is intentionally NOT the process entry point. <c>Program.cs</c>'s top-level-statements
/// <c>Main</c> is the single entry point for the whole combined app (server + this WPF shell) —
/// see the project plan's "Locked-in architecture decisions" #8. If <c>App/App.xaml</c> were
/// compiled with the SDK's default <c>ApplicationDefinition</c> build action, the WPF SDK would
/// auto-generate a second <c>Main</c> from it, which collides with the existing one and fails the
/// build. Instead, <c>Accel.csproj</c> compiles <c>App/App.xaml</c> as <c>Page</c> (see its
/// ItemGroup), and this class is constructed manually by <c>RunCombinedAsync</c> on its dedicated
/// STA thread.
///
/// <para>The constructor explicitly calls <see cref="Application.InitializeComponent"/> - since
/// this is compiled as <c>Page</c> rather than <c>ApplicationDefinition</c>, nothing does that
/// automatically, and without it the dark-theme resources declared in <c>App.xaml</c> would never
/// load.</para>
/// </summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }
}

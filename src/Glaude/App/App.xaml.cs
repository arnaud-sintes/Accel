namespace Glaude.App;

using System.Windows;

/// <summary>
/// The WPF shell's <see cref="Application"/> subclass — scaffolding only (P1-T1b), no behavior
/// wired in yet.
///
/// This is intentionally NOT the process entry point. <c>Program.cs</c>'s top-level-statements
/// <c>Main</c> is the single entry point for the whole combined app (server + WinForms monitor
/// + this future WPF shell) — see the project plan's "Locked-in architecture decisions" #8. If
/// <c>App/App.xaml</c> were compiled with the SDK's default <c>ApplicationDefinition</c> build
/// action, the WPF SDK would auto-generate a second <c>Main</c> from it, which collides with the
/// existing one and fails the build. Instead, <c>Glaude.csproj</c> compiles <c>App/App.xaml</c>
/// as <c>Page</c> (see its ItemGroup), and this class is constructed manually — currently only
/// from <c>Program.cs</c>'s hidden/throwaway <c>ui-preview</c> verb, used to visually verify this
/// scaffolding during development. It is not wired into the real `glaude` startup path yet.
/// </summary>
public partial class App : Application
{
}

namespace Accel.App.Controls;

using System.Reflection;

/// <summary>Accel's own version - read once from the executing assembly's version (set via
/// accel.csproj's <c>&lt;Version&gt;</c>), shown next to the title in <see cref="CustomTitleBar"/>.
/// Never throws: a host with no version info (e.g. a designer preview) degrades to an empty
/// string, which <see cref="CustomTitleBar.xaml"/>'s string-to-visibility converter treats as
/// "show nothing" rather than a stray "v".</summary>
public static class AppVersionInfo
{
    public static readonly string DisplayText = Resolve();

    private static string Resolve()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? string.Empty : $"v{version.Major}.{version.Minor}.{version.Build}";
    }
}

namespace Accel.App.Controls;

using System;
using System.Reflection;

/// <summary>Accel's own version - read once from the executing assembly's version (set via
/// accel.csproj's <c>&lt;Version&gt;</c>), shown next to the title in <see cref="CustomTitleBar"/>.
/// Never throws: a host with no version info (e.g. a designer preview) degrades to an empty
/// string, which <see cref="CustomTitleBar.xaml"/>'s string-to-visibility converter treats as
/// "show nothing" rather than a stray "v".</summary>
public static class AppVersionInfo
{
    /// <summary>The raw assembly version, or null for a host with no version info (e.g. a designer
    /// preview). Exposed alongside <see cref="DisplayText"/> for callers that need to compare
    /// versions (e.g. <see cref="Accel.Versioning.AccelUpdateProbe"/>) rather than just display one.</summary>
    public static readonly Version? Current = Assembly.GetExecutingAssembly().GetName().Version;

    public static readonly string DisplayText = Resolve();

    private static string Resolve()
    {
        var version = Current;
        return version is null ? string.Empty : $"v{version.Major}.{version.Minor}.{version.Build}";
    }
}

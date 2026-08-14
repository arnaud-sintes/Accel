namespace Glaude.Cli;

using System;
using System.Diagnostics;
using System.IO;
using Glaude.Versioning;

/// <summary>
/// Shared path/probe helpers for the CLI verbs, factored out so each verb command stays
/// unit-testable (tests pass explicit paths/probes; production code uses these defaults).
/// </summary>
public static class GlaudePaths
{
    /// <summary>
    /// The real, live <c>%USERPROFILE%\.claude\settings.json</c>. Per project.md, Glaude
    /// installs into user scope only (not project-scope <c>.claude/settings.json</c>).
    /// </summary>
    public static string DefaultSettingsPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "settings.json");

    /// <summary>
    /// Resolves the path to Glaude's own running executable, so hooks call the real binary
    /// rather than e.g. "dotnet". Falls back through <see cref="Environment.ProcessPath"/> then
    /// <see cref="Process.MainModule"/>, and finally a bare "glaude.exe" if both are unavailable
    /// (never throws).
    /// </summary>
    public static string CurrentExePath()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
            {
                return processPath;
            }
        }
        catch
        {
            // Fall through to the next strategy.
        }

        try
        {
            var mainModule = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(mainModule))
            {
                return mainModule;
            }
        }
        catch
        {
            // Fall through to the last-resort default.
        }

        return "glaude.exe";
    }

    /// <summary>Runs a version probe defensively — never lets an exception escape to a CLI verb.</summary>
    public static ClaudeVersion? SafeProbe(Func<ClaudeVersion?> probe)
    {
        try
        {
            return probe();
        }
        catch
        {
            return null;
        }
    }
}

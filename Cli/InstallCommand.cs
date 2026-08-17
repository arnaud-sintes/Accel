namespace Accel.Cli;

using System;
using System.IO;
using System.Linq;
using Accel.Settings;
using Accel.Versioning;

/// <summary>
/// `accel install` — loads the real settings.json, builds the expected
/// <see cref="AccelHookSpec"/> for the current port + exe path, version-gates
/// SubagentStart/subagentStatusLine, and applies via <see cref="SettingsMerger.InstallInto"/>.
///
/// Never crashes or corrupts the file on any error: all safety (atomic write, `.accel.bak`
/// backup, refuse-on-malformed) lives in <see cref="SettingsFile"/>/<see cref="SettingsMerger"/>
/// and is reused as-is here.
/// </summary>
public static class InstallCommand
{
    /// <summary>Convenience entry point for the CLI: real settings.json, real exe path, real probe.</summary>
    public static int Run(int port, TextWriter output) =>
        Run(
            port,
            AccelPaths.DefaultSettingsPath(),
            AccelPaths.CurrentExePath(),
            FileBackedStatusLineChainStore.DefaultPath(),
            output,
            ClaudeVersionProbe.GetInstalledVersion);

    /// <summary>Test seam: explicit settings/exe/state paths and version probe.</summary>
    public static int Run(
        int port,
        string settingsPath,
        string exePath,
        string statePath,
        TextWriter output,
        Func<ClaudeVersion?> versionProbe)
    {
        ArgumentNullException.ThrowIfNull(output);

        var file = SettingsFile.Load(settingsPath);

        if (!file.IsWritableForInstall)
        {
            output.WriteLine($"Refused: settings file at '{settingsPath}' is {file.Status} — install aborted, nothing was written.");
            if (file.ErrorMessage is not null)
            {
                output.WriteLine($"  {file.ErrorMessage}");
            }

            return 1;
        }

        var version = AccelPaths.SafeProbe(versionProbe);
        var includeSubagentStart = VersionGate.Supports(version, Feature.SubagentStartEvent);
        var includeSubagentStatusLine = VersionGate.ShouldRegisterSubagentStatusLine(version);

        var spec = new AccelHookSpec(port, exePath, includeSubagentStart, includeSubagentStatusLine);
        var store = new FileBackedStatusLineChainStore(statePath);

        // Detect BEFORE installing so the summary can describe what changed (missing events,
        // pre-existing foreign status lines) rather than only the post-install steady state.
        var before = SettingsMerger.Detect(file.Root, spec);

        var outcome = SettingsMerger.InstallInto(file, spec, store);

        switch (outcome)
        {
            case InstallOutcome.Refused:
                // Re-checked defensively: InstallInto reaches the same conclusion independently.
                output.WriteLine($"Refused: settings file at '{settingsPath}' is not writable — install aborted, nothing was written.");
                return 1;

            case InstallOutcome.NoChange:
                output.WriteLine($"Already installed on port {port}. Nothing changed.");
                return 0;

            case InstallOutcome.Applied:
                PrintAppliedSummary(output, settingsPath, port, version, includeSubagentStart, includeSubagentStatusLine, before, file);
                return 0;

            default:
                return 1;
        }
    }

    private static void PrintAppliedSummary(
        TextWriter output,
        string settingsPath,
        int port,
        ClaudeVersion? version,
        bool includeSubagentStart,
        bool includeSubagentStatusLine,
        DetectionResult before,
        SettingsFile file)
    {
        output.WriteLine($"Installed Accel into '{settingsPath}' (port {port}).");
        output.WriteLine(
            $"  Claude Code version: {(version is null ? "not detected — degraded to the most conservative feature set" : version.ToString())}");
        output.WriteLine($"  SubagentStart hook: {(includeSubagentStart ? "included" : "omitted (requires a newer Claude Code)")}");
        output.WriteLine($"  subagentStatusLine: {(includeSubagentStatusLine ? "included" : "omitted (requires a newer Claude Code)")}");

        if (before.MissingEvents.Count > 0)
        {
            output.WriteLine($"  Hooks added: {string.Join(", ", before.MissingEvents)}");
        }

        if (before.FoundEvents.Count > 0)
        {
            output.WriteLine($"  Hooks rewritten (already present): {string.Join(", ", before.FoundEvents.Keys.OrderBy(k => k))}");
        }

        if (before.StrayEvents.Count > 0)
        {
            output.WriteLine($"  Stray Accel hooks removed (no longer expected): {string.Join(", ", before.StrayEvents)}");
        }

        PrintStatusLineChange(output, "statusLine", before.StatusLine);

        if (includeSubagentStatusLine)
        {
            PrintStatusLineChange(output, "subagentStatusLine", before.SubagentStatusLine);
        }

        if (before.DriftingPorts.Count > 0)
        {
            output.WriteLine($"  Port drift repaired (was: {string.Join(", ", before.DriftingPorts.Distinct())})");
        }

        output.WriteLine($"  Backup: {file.BackupPath}");
    }

    private static void PrintStatusLineChange(TextWriter output, string fieldName, StatusLineOwnership before)
    {
        switch (before)
        {
            case StatusLineOwnership.Foreign:
                output.WriteLine($"  {fieldName}: pre-existing third-party command captured — will be restored on uninstall.");
                break;
            case StatusLineOwnership.None:
                output.WriteLine($"  {fieldName}: installed (none was present before).");
                break;
            case StatusLineOwnership.Accel:
                output.WriteLine($"  {fieldName}: rewritten (already Accel-owned).");
                break;
        }
    }
}

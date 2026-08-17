namespace Accel.Cli;

using System;
using System.IO;
using Accel.Settings;

/// <summary>
/// `accel uninstall` — loads the real settings.json, removes every Accel-tagged hook entry
/// and restores any captured pre-existing `statusLine`/`subagentStatusLine`, then prints a
/// summary of what was removed/restored.
/// </summary>
public static class UninstallCommand
{
    /// <summary>Convenience entry point for the CLI: real settings.json, real state file.</summary>
    public static int Run(TextWriter output) =>
        Run(AccelPaths.DefaultSettingsPath(), FileBackedStatusLineChainStore.DefaultPath(), output);

    /// <summary>Test seam: explicit settings/state paths.</summary>
    public static int Run(string settingsPath, string statePath, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var file = SettingsFile.Load(settingsPath);

        if (file.Status is SettingsLoadStatus.Missing)
        {
            output.WriteLine($"Nothing to uninstall: no settings file at '{settingsPath}'.");
            return 0;
        }

        if (file.Status is SettingsLoadStatus.Empty or SettingsLoadStatus.Malformed)
        {
            output.WriteLine($"Cannot uninstall: settings file at '{settingsPath}' is {file.Status}.");
            if (file.ErrorMessage is not null)
            {
                output.WriteLine($"  {file.ErrorMessage}");
            }

            return 1;
        }

        var store = new FileBackedStatusLineChainStore(statePath);

        // Snapshot what's captured BEFORE Uninstall consumes/clears it, so the summary can say
        // what was restored vs simply removed.
        var hasStatusLineCapture = store.TryGet(StatusLineField.StatusLine, out var statusLineCapture);
        var hasSubagentCapture = store.TryGet(StatusLineField.SubagentStatusLine, out var subagentCapture);

        var changed = SettingsMerger.Uninstall(file.Root!, store);

        if (!changed)
        {
            output.WriteLine("Nothing to uninstall: no Accel entries were found.");
            return 0;
        }

        file.Save(file.Root!);

        output.WriteLine($"Uninstalled Accel from '{settingsPath}'.");
        PrintRestoreLine(output, "statusLine", hasStatusLineCapture, statusLineCapture);
        PrintRestoreLine(output, "subagentStatusLine", hasSubagentCapture, subagentCapture);
        output.WriteLine($"  Backup: {file.BackupPath}");

        return 0;
    }

    private static void PrintRestoreLine(TextWriter output, string fieldName, bool hadCapture, StatusLineCapture? capture)
    {
        if (!hadCapture || capture is null)
        {
            return;
        }

        output.WriteLine(
            capture.HadOriginal
                ? $"  {fieldName}: restored the pre-existing third-party command."
                : $"  {fieldName}: removed (none pre-existed).");
    }
}

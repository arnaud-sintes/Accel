namespace Glaude.App.Services;

using System;
using System.IO;
using System.Linq;
using Glaude.Server;

/// <summary>
/// P1-T3b: the pure add/remove logic behind panel A's root add/remove UI. Deliberately has no
/// dependency on WPF/WinForms - <see cref="IFolderPickerService"/>/<see cref="IUserConfirmationService"/>
/// own the actual dialogs, so this class is unit-testable against a fixture config path exactly
/// like <see cref="RootFoldersConfig"/> itself.
///
/// <para><b>Data-safety invariant (not a suggestion):</b> <see cref="RemoveRoot"/> only ever
/// dereferences <paramref name="folderPath"/> from the config's <c>roots</c> list. It never calls
/// <see cref="Directory.Delete(string, bool)"/> or touches the folder or its contents in any way -
/// the folder and everything in it must still exist on disk after a remove. See
/// <c>RootFolderEditorTests.RemoveRoot_NeverTouchesTheFolderOnDisk</c> for the assertion that
/// pins this down.</para>
/// </summary>
public static class RootFolderEditor
{
    /// <summary>
    /// Confirmation copy shown before a remove. Deliberately avoids the word "delete" anywhere -
    /// nothing is being deleted, only dereferenced from Glaude's own config, so calling it a
    /// deletion would be actively misleading.
    /// </summary>
    public const string StopMonitoringConfirmationText =
        "Stop monitoring this folder? Glaude will forget about it, but the folder and everything " +
        "in it will stay exactly where it is on disk.";

    public const string StopMonitoringConfirmationTitle = "Stop monitoring folder";

    /// <summary>
    /// Creates <paramref name="folderPath"/> on disk if it doesn't already exist, then appends it
    /// to the roots list persisted at <paramref name="configPath"/> (v2-aware, via
    /// <see cref="RootFoldersConfig.Save"/>). A no-op (beyond the directory-creation check) if the
    /// folder is already tracked.
    /// </summary>
    public static void AddRoot(string configPath, string folderPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(configPath);
        ArgumentException.ThrowIfNullOrEmpty(folderPath);

        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }

        var current = RootFoldersConfig.LoadFull(new[] { configPath });

        if (current.Roots.Contains(folderPath, StringComparer.OrdinalIgnoreCase))
        {
            // Already tracked - nothing left to persist. Still fine if the directory itself was
            // just (re)created above.
            return;
        }

        string[] newRoots = current.Roots.Append(folderPath).ToArray();
        var keepSessionIds = new HashSet<string>(current.Sessions.Keys, StringComparer.Ordinal);

        RootFoldersConfig.Save(configPath, newRoots, current.Sessions, keepSessionIds);
    }

    /// <summary>
    /// Removes <paramref name="folderPath"/> from the roots list persisted at
    /// <paramref name="configPath"/>. <b>Never touches the filesystem</b> - see the type-level
    /// doc comment for the data-safety invariant this method must uphold.
    /// </summary>
    public static void RemoveRoot(string configPath, string folderPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(configPath);
        ArgumentException.ThrowIfNullOrEmpty(folderPath);

        var current = RootFoldersConfig.LoadFull(new[] { configPath });

        string[] newRoots = current.Roots
            .Where(r => !string.Equals(r, folderPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var keepSessionIds = new HashSet<string>(current.Sessions.Keys, StringComparer.Ordinal);

        // Deliberately no Directory.Delete/File.Delete anywhere in this method - dereferencing
        // the config entry is the entire operation.
        RootFoldersConfig.Save(configPath, newRoots, current.Sessions, keepSessionIds);
    }
}

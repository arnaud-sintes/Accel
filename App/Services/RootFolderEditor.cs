namespace Accel.App.Services;

using System;
using System.IO;
using System.Linq;
using Accel.Server;

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
    /// nothing is being deleted, only dereferenced from Accel's own config, so calling it a
    /// deletion would be actively misleading.
    /// </summary>
    public const string StopMonitoringConfirmationText =
        "Stop monitoring this folder? Accel will forget about it, but the folder and everything " +
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

    /// <summary>
    /// Records <paramref name="displayName"/> as <paramref name="sessionId"/>'s <c>accel_override</c>
    /// (the top tier of <c>RootsTreeBuilder.BuildSessionDto</c>'s name ladder), preserving any existing
    /// <see cref="SessionOverride.Pinned"/>/<see cref="SessionOverride.Hidden"/>/<see cref="SessionOverride.LastOpenedUtc"/>
    /// already on file for this session. Used right after a session is created so panel A's row shows
    /// the same name the tab strip does, instead of falling through to the transcript-derived tiers
    /// until a live <c>/rename</c> happens to set one.
    /// </summary>
    public static void SetSessionDisplayName(string configPath, string sessionId, string displayName)
    {
        ArgumentException.ThrowIfNullOrEmpty(configPath);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentException.ThrowIfNullOrEmpty(displayName);

        var current = RootFoldersConfig.LoadFull(new[] { configPath });

        var updated = current.Sessions.TryGetValue(sessionId, out var existing)
            ? existing with { DisplayName = displayName }
            : new SessionOverride(displayName, Pinned: false, Hidden: false, LastOpenedUtc: null);

        var newSessions = new Dictionary<string, SessionOverride>(current.Sessions, StringComparer.Ordinal)
        {
            [sessionId] = updated,
        };
        var keepSessionIds = new HashSet<string>(newSessions.Keys, StringComparer.Ordinal);

        RootFoldersConfig.Save(configPath, current.Roots, newSessions, keepSessionIds);
    }
}

namespace Glaude.Tests;

using System;
using System.IO;
using System.Linq;
using Glaude.App.Services;
using Glaude.Server;
using Xunit;

/// <summary>
/// Unit tests for P1-T3b's <see cref="RootFolderEditor"/> - the pure add/remove logic behind
/// panel A's root add/remove UI. Every test uses a fixture <c>glaude-folders.json</c> path under
/// the OS temp directory; the real durable-home config (<c>%USERPROFILE%\.claude\glaude-folders.json</c>)
/// is never touched.
///
/// <para>The critical data-safety test is <see cref="RemoveRoot_NeverTouchesTheFolderOrItsContentsOnDisk"/>:
/// remove must dereference the root from the config only, never call
/// <see cref="Directory.Delete(string, bool)"/> or otherwise touch the folder.</para>
/// </summary>
public class RootFolderEditorTests : IDisposable
{
    private readonly List<string> _tempPaths = new();

    public void Dispose()
    {
        foreach (var path in _tempPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                string bak = path + ".bak";
                if (File.Exists(bak))
                {
                    File.Delete(bak);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }

        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private readonly List<string> _tempDirs = new();

    private string NewFixtureConfigPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"glaude-folders-editor-test-{Guid.NewGuid():N}.json");
        _tempPaths.Add(path);
        return path;
    }

    private string NewFixtureFolderPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"glaude-root-editor-test-{Guid.NewGuid():N}");
        _tempDirs.Add(path);
        return path;
    }

    [Fact]
    public void AddRoot_CreatesTheDirectoryAndPersistsItToTheFixtureConfig()
    {
        string configPath = NewFixtureConfigPath();
        string folder = NewFixtureFolderPath();
        Assert.False(Directory.Exists(folder));

        RootFolderEditor.AddRoot(configPath, folder);

        Assert.True(Directory.Exists(folder));

        var loaded = RootFoldersConfig.LoadFull(new[] { configPath });
        Assert.Contains(folder, loaded.Roots);
    }

    [Fact]
    public void AddRoot_WhenDirectoryAlreadyExists_DoesNotErrorAndStillPersists()
    {
        string configPath = NewFixtureConfigPath();
        string folder = NewFixtureFolderPath();
        Directory.CreateDirectory(folder); // pre-existing

        var exception = Record.Exception(() => RootFolderEditor.AddRoot(configPath, folder));

        Assert.Null(exception);
        Assert.True(Directory.Exists(folder));
        var loaded = RootFoldersConfig.LoadFull(new[] { configPath });
        Assert.Contains(folder, loaded.Roots);
    }

    [Fact]
    public void AddRoot_CalledTwiceForTheSameFolder_DoesNotDuplicateTheRootsEntry()
    {
        string configPath = NewFixtureConfigPath();
        string folder = NewFixtureFolderPath();

        RootFolderEditor.AddRoot(configPath, folder);
        RootFolderEditor.AddRoot(configPath, folder);

        var loaded = RootFoldersConfig.LoadFull(new[] { configPath });
        Assert.Single(loaded.Roots.Where(r => string.Equals(r, folder, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void RemoveRoot_NeverTouchesTheFolderOrItsContentsOnDisk()
    {
        // The critical data-safety test: remove must dereference the config entry ONLY.
        string configPath = NewFixtureConfigPath();
        string folder = NewFixtureFolderPath();
        Directory.CreateDirectory(folder);
        string fileInFolder = Path.Combine(folder, "keep-me.txt");
        File.WriteAllText(fileInFolder, "still here");

        RootFolderEditor.AddRoot(configPath, folder);
        Assert.Contains(folder, RootFoldersConfig.LoadFull(new[] { configPath }).Roots);

        RootFolderEditor.RemoveRoot(configPath, folder);

        // Dereferenced from the config...
        Assert.DoesNotContain(folder, RootFoldersConfig.LoadFull(new[] { configPath }).Roots);

        // ...but the folder and its contents are untouched on disk.
        Assert.True(Directory.Exists(folder));
        Assert.True(File.Exists(fileInFolder));
        Assert.Equal("still here", File.ReadAllText(fileInFolder));
    }

    [Fact]
    public void RemoveRoot_LeavesOtherRootsAndSessionOverridesIntact()
    {
        string configPath = NewFixtureConfigPath();
        string folderToRemove = NewFixtureFolderPath();
        string folderToKeep = NewFixtureFolderPath();

        var sessions = new Dictionary<string, SessionOverride>
        {
            ["session-a"] = new SessionOverride("Kept Session", Pinned: true, Hidden: false, LastOpenedUtc: null),
        };
        RootFoldersConfig.Save(
            configPath,
            new[] { folderToRemove, folderToKeep },
            sessions,
            new HashSet<string> { "session-a" });

        RootFolderEditor.RemoveRoot(configPath, folderToRemove);

        var loaded = RootFoldersConfig.LoadFull(new[] { configPath });
        Assert.DoesNotContain(folderToRemove, loaded.Roots);
        Assert.Contains(folderToKeep, loaded.Roots);
        var kept = Assert.Single(loaded.Sessions);
        Assert.Equal("session-a", kept.Key);
    }

    [Fact]
    public void RemoveRoot_OnAConfigThatDoesNotYetExist_DoesNotThrow()
    {
        string configPath = NewFixtureConfigPath(); // never written to
        string folder = NewFixtureFolderPath();

        var exception = Record.Exception(() => RootFolderEditor.RemoveRoot(configPath, folder));

        Assert.Null(exception);
    }

    [Fact]
    public void StopMonitoringConfirmationText_NeverContainsTheWordDelete()
    {
        Assert.DoesNotContain("delete", RootFolderEditor.StopMonitoringConfirmationText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete", RootFolderEditor.StopMonitoringConfirmationTitle, StringComparison.OrdinalIgnoreCase);
    }
}

namespace Accel.Cli;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>One folder or file row in panel B's read-only hierarchy tree - one level only.
/// <see cref="HasChildren"/> is a cheap existence check, not a recursive count: a directory's own
/// children are built separately, only once it is actually expanded (see
/// <see cref="Accel.App.ViewModels.FilesPanelNodeViewModel"/>'s lazy-load remarks).</summary>
public sealed record FileTreeNode(string Path, string Name, bool IsDirectory, bool HasChildren);

/// <summary>
/// Pure, WPF-free builder for panel B's file/folder tree - the filesystem-walking counterpart to
/// <see cref="MonitorTreeBuilder"/> (which walks session/agent telemetry, not disk). Every call
/// enumerates exactly one directory level - never the whole subtree - so a folder with a deep or
/// huge branch (e.g. a monorepo's vendored dependencies) cannot make one focus change (or one
/// expand click) walk an unbounded amount of disk. Called only on a focus change or a folder's own
/// expand (see <see cref="Accel.App.ViewModels.FilesPanelViewModel"/>'s remarks), never on a timer
/// or <c>FileSystemWatcher</c>.
///
/// <para>Never throws: an unreadable or since-removed directory degrades to "no children" for that
/// level, matching <c>RootsTreeBuilder</c>'s "never propagate an I/O exception" convention.</para>
/// </summary>
public static class FilesTreeBuilder
{
    /// <summary>The focused root's own top-level children, or null if <paramref name="rootPath"/> is
    /// empty or no longer exists on disk. The root row itself is never returned/rendered - the panel
    /// already names it in its header/status text.</summary>
    public static FileTreeNode[]? BuildRootChildren(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return null;
        }

        return BuildChildren(rootPath);
    }

    /// <summary>One directory's immediate children only - directories first, then files, both
    /// alphabetical. Public so a folder node can call this again, with its own path, the first time
    /// it is expanded.</summary>
    public static FileTreeNode[] BuildChildren(string directoryPath)
    {
        var directories = SafeEnumerateDirectories(directoryPath).OrderBy(SafeGetFileName, StringComparer.OrdinalIgnoreCase);
        var files = SafeEnumerateFiles(directoryPath).OrderBy(SafeGetFileName, StringComparer.OrdinalIgnoreCase);

        var result = new List<FileTreeNode>();

        foreach (string dir in directories)
        {
            result.Add(new FileTreeNode(dir, SafeGetFileName(dir), IsDirectory: true, HasChildren: HasAnyEntries(dir)));
        }

        foreach (string file in files)
        {
            result.Add(new FileTreeNode(file, SafeGetFileName(file), IsDirectory: false, HasChildren: false));
        }

        return result.ToArray();
    }

    private static bool HasAnyEntries(string directoryPath)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directoryPath).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static string SafeGetFileName(string path)
    {
        try
        {
            string name = Path.GetFileName(path);
            return string.IsNullOrEmpty(name) ? path : name;
        }
        catch (Exception)
        {
            return path;
        }
    }
}

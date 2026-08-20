namespace Accel.Orchestration;

using System;
using System.Collections.Generic;
using System.IO;

/// <summary>What one <see cref="FileSystemEntryPlan"/> is asking <see cref="FileSystemEntryExecutor"/>
/// to do.</summary>
public enum FileSystemEntryOperationKind
{
    CreateFile,
    CreateFolder,

    /// <summary>Covers both a move and a plain rename - a rename is just a move whose
    /// <see cref="FileSystemEntryPlan.DestinationPath"/> shares the source's parent directory.</summary>
    Move,

    Delete,
}

/// <summary>
/// The result of planning one panel-B explorer operation: pure data, no I/O mutation, matching this
/// codebase's plan/execute convention (<see cref="SessionRemovalPlan"/>). <see cref="Exists"/> is a
/// plan-time snapshot only - <see cref="FileSystemEntryExecutor"/> re-checks immediately before acting,
/// since it can go stale the instant it is produced.
/// </summary>
public sealed record FileSystemEntryPlan(
    FileSystemEntryOperationKind Kind,
    string TargetPath,
    string? DestinationPath,
    bool IsDirectory,
    bool Exists,
    bool IsSafe,
    IReadOnlyList<string> Warnings);

/// <summary>The result of validating one candidate path against its containment root.</summary>
internal readonly record struct FileSystemEntryValidation(bool IsValid, string? RejectionReason);

/// <summary>
/// Planner half of panel B's explorer operations (create/move/rename/delete) - pure, never mutates
/// disk. Modeled directly on <see cref="SessionRemover"/>: every candidate path is validated here so
/// <see cref="FileSystemEntryExecutor"/>'s job narrows to "act on exactly what was already proven
/// safe", not "prove safety while also mutating things".
///
/// <para><b>Containment root.</b> Every plan method takes <c>rootPath</c> - the panel's currently
/// resolved focused folder (<see cref="Accel.App.ViewModels.FilesPanelViewModel.CurrentRootPath"/>).
/// Unlike <see cref="SessionRemover"/>'s fixed <c>%USERPROFILE%\.claude</c> boundary, this root is an
/// arbitrary user project folder that may itself legitimately be a symlink/junction - so the reparse-
/// point walk below deliberately stops <b>short of</b> (exclusive of) the root itself, rather than
/// <see cref="SessionRemover.ValidateTarget"/>'s inclusive walk up through and past its fixed home
/// directory. Only ancestors strictly between the candidate and the root are rejected.</para>
/// </summary>
public static class FileSystemEntryPlanner
{
    public static FileSystemEntryPlan PlanCreateFile(string parentDirectoryPath, string fileName, string rootPath) =>
        PlanCreate(parentDirectoryPath, fileName, rootPath, isDirectory: false, FileSystemEntryOperationKind.CreateFile);

    public static FileSystemEntryPlan PlanCreateFolder(string parentDirectoryPath, string folderName, string rootPath) =>
        PlanCreate(parentDirectoryPath, folderName, rootPath, isDirectory: true, FileSystemEntryOperationKind.CreateFolder);

    private static FileSystemEntryPlan PlanCreate(
        string parentDirectoryPath, string name, string rootPath, bool isDirectory, FileSystemEntryOperationKind kind)
    {
        var warnings = new List<string>();

        string? nameRejection = ValidateName(name);
        if (nameRejection is not null)
        {
            warnings.Add(nameRejection);
            return new FileSystemEntryPlan(kind, string.Empty, null, isDirectory, false, false, warnings);
        }

        string fullRoot = NormalizeRoot(rootPath);
        var parentValidation = ValidateWithinRoot(parentDirectoryPath, fullRoot);
        if (!parentValidation.IsValid)
        {
            warnings.Add($"Parent folder: {parentValidation.RejectionReason}");
            return new FileSystemEntryPlan(kind, string.Empty, null, isDirectory, false, false, warnings);
        }

        if (!Directory.Exists(parentDirectoryPath))
        {
            warnings.Add("Parent folder no longer exists.");
            return new FileSystemEntryPlan(kind, string.Empty, null, isDirectory, false, false, warnings);
        }

        string targetPath = Path.Combine(parentDirectoryPath, name.Trim());
        var targetValidation = ValidateWithinRoot(targetPath, fullRoot);
        if (!targetValidation.IsValid)
        {
            warnings.Add(targetValidation.RejectionReason ?? "Target path failed validation.");
            return new FileSystemEntryPlan(kind, targetPath, null, isDirectory, false, false, warnings);
        }

        bool exists = Directory.Exists(targetPath) || File.Exists(targetPath);
        if (exists)
        {
            warnings.Add($"'{name.Trim()}' already exists here.");
            return new FileSystemEntryPlan(kind, targetPath, null, isDirectory, true, false, warnings);
        }

        return new FileSystemEntryPlan(kind, targetPath, null, isDirectory, false, true, warnings);
    }

    /// <summary>Plans a move; a plain rename is simply a move whose destination shares the source's
    /// parent directory.</summary>
    public static FileSystemEntryPlan PlanMove(string sourcePath, string destinationPath, string rootPath)
    {
        var warnings = new List<string>();
        string fullRoot = NormalizeRoot(rootPath);

        bool isDirectory = Directory.Exists(sourcePath);
        bool isFile = !isDirectory && File.Exists(sourcePath);
        if (!isDirectory && !isFile)
        {
            warnings.Add("Source no longer exists.");
            return new FileSystemEntryPlan(FileSystemEntryOperationKind.Move, sourcePath, destinationPath, false, false, false, warnings);
        }

        var sourceValidation = ValidateWithinRoot(sourcePath, fullRoot);
        if (!sourceValidation.IsValid)
        {
            warnings.Add($"Source: {sourceValidation.RejectionReason}");
            return new FileSystemEntryPlan(FileSystemEntryOperationKind.Move, sourcePath, destinationPath, isDirectory, true, false, warnings);
        }

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            warnings.Add("Enter a destination path.");
            return new FileSystemEntryPlan(FileSystemEntryOperationKind.Move, sourcePath, destinationPath, isDirectory, true, false, warnings);
        }

        string fullSource = NormalizePath(sourcePath);
        string fullDestination = NormalizePath(destinationPath);

        if (string.Equals(fullSource, fullDestination, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Choose a different name or location.");
            return new FileSystemEntryPlan(FileSystemEntryOperationKind.Move, sourcePath, destinationPath, isDirectory, true, false, warnings);
        }

        var destinationValidation = ValidateWithinRoot(destinationPath, fullRoot);
        if (!destinationValidation.IsValid)
        {
            warnings.Add($"Destination: {destinationValidation.RejectionReason}");
            return new FileSystemEntryPlan(FileSystemEntryOperationKind.Move, sourcePath, destinationPath, isDirectory, true, false, warnings);
        }

        string? destinationParent = Path.GetDirectoryName(fullDestination);
        if (string.IsNullOrEmpty(destinationParent) || !Directory.Exists(destinationParent))
        {
            warnings.Add("Destination folder does not exist.");
            return new FileSystemEntryPlan(FileSystemEntryOperationKind.Move, sourcePath, destinationPath, isDirectory, true, false, warnings);
        }

        if (Directory.Exists(fullDestination) || File.Exists(fullDestination))
        {
            warnings.Add("Something already exists at the destination.");
            return new FileSystemEntryPlan(FileSystemEntryOperationKind.Move, sourcePath, destinationPath, isDirectory, true, false, warnings);
        }

        if (isDirectory &&
            (fullDestination.StartsWith(fullSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
             fullDestination.StartsWith(fullSource + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add("Cannot move a folder into its own subfolder.");
            return new FileSystemEntryPlan(FileSystemEntryOperationKind.Move, sourcePath, destinationPath, isDirectory, true, false, warnings);
        }

        return new FileSystemEntryPlan(FileSystemEntryOperationKind.Move, sourcePath, destinationPath, isDirectory, true, true, warnings);
    }

    public static FileSystemEntryPlan PlanDelete(string targetPath, string rootPath)
    {
        var warnings = new List<string>();
        string fullRoot = NormalizeRoot(rootPath);

        bool isDirectory = Directory.Exists(targetPath);
        bool isFile = !isDirectory && File.Exists(targetPath);
        if (!isDirectory && !isFile)
        {
            warnings.Add("Target no longer exists.");
            return new FileSystemEntryPlan(FileSystemEntryOperationKind.Delete, targetPath, null, false, false, false, warnings);
        }

        var validation = ValidateWithinRoot(targetPath, fullRoot);
        if (!validation.IsValid)
        {
            warnings.Add(validation.RejectionReason ?? "Target path failed validation.");
            return new FileSystemEntryPlan(FileSystemEntryOperationKind.Delete, targetPath, null, isDirectory, true, false, warnings);
        }

        return new FileSystemEntryPlan(FileSystemEntryOperationKind.Delete, targetPath, null, isDirectory, true, true, warnings);
    }

    private static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter a name.";
        }

        string trimmed = name.Trim();
        if (trimmed is "." or "..")
        {
            return $"'{trimmed}' is not a valid name.";
        }

        foreach (char c in trimmed)
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0)
            {
                return $"'{trimmed}' contains an invalid character ('{c}').";
            }
        }

        return null;
    }

    private static string NormalizeRoot(string rootPath) =>
        NormalizePath(rootPath);

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// The safety gate every candidate path must pass: (1) resolves, under <see cref="Path.GetFullPath(string)"/>,
    /// to somewhere strictly inside <paramref name="fullRootPath"/> - rejects <c>..</c> segments and
    /// absolute paths smuggled in as a "relative" candidate; (2) no existing ancestor strictly between
    /// the candidate and <paramref name="fullRootPath"/> (exclusive of the root itself - see this
    /// class's remarks) is a reparse point.
    /// </summary>
    internal static FileSystemEntryValidation ValidateWithinRoot(string candidatePath, string fullRootPath)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            return new FileSystemEntryValidation(false, $"path could not be resolved ({ex.Message})");
        }

        bool insideRoot =
            string.Equals(fullPath, fullRootPath, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(fullRootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(fullRootPath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!insideRoot)
        {
            return new FileSystemEntryValidation(false, "target does not resolve to a location under the current folder root");
        }

        if (TryFindReparsePoint(fullRootPath, fullPath, out string? offendingPath))
        {
            return new FileSystemEntryValidation(false, $"'{offendingPath}' is a reparse point (symlink/junction/mount) - refusing to act through it");
        }

        return new FileSystemEntryValidation(true, null);
    }

    /// <summary>
    /// Walks from <paramref name="fullPath"/> up to, but <b>excluding</b>, <paramref name="fullRootPath"/>
    /// itself - the root is an arbitrary user-chosen project folder that may legitimately be a symlink
    /// (unlike <see cref="SessionRemover"/>'s fixed, always-real <c>.claude</c> home), so this
    /// deliberately does not require the root to be a plain directory. Only checks components that
    /// currently exist.
    /// </summary>
    private static bool TryFindReparsePoint(string fullRootPath, string fullPath, out string? offendingPath)
    {
        string? current = fullPath;

        while (current is not null && !string.Equals(current, fullRootPath, StringComparison.OrdinalIgnoreCase))
        {
            FileAttributes? attributes;
            try
            {
                if (Directory.Exists(current))
                {
                    attributes = new DirectoryInfo(current).Attributes;
                }
                else if (File.Exists(current))
                {
                    attributes = new FileInfo(current).Attributes;
                }
                else
                {
                    attributes = null;
                }
            }
            catch
            {
                offendingPath = current;
                return true;
            }

            if (attributes is { } a && (a & FileAttributes.ReparsePoint) != 0)
            {
                offendingPath = current;
                return true;
            }

            string? parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        offendingPath = null;
        return false;
    }
}

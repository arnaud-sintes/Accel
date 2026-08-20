namespace Accel.Orchestration;

using System;
using System.IO;

/// <summary>What happened when <see cref="FileSystemEntryExecutor.Execute"/> acted on a plan.</summary>
public enum FileSystemEntryOutcome
{
    Succeeded,

    /// <summary>Nothing was there to act on (a Move/Delete target vanished on its own between planning
    /// and execution) - not a failure, matches <see cref="SessionRemovalStepOutcome.NotPresent"/>'s
    /// role.</summary>
    NotPresent,

    Failed,
}

/// <summary>The result of one <see cref="FileSystemEntryExecutor.Execute"/> call.</summary>
public sealed record FileSystemEntryResult(
    FileSystemEntryOperationKind Kind,
    string TargetPath,
    string? DestinationPath,
    FileSystemEntryOutcome Outcome,
    string? Detail,
    Exception? Failure);

/// <summary>
/// Executor half of panel B's explorer operations - the only class allowed to mutate disk for
/// create/move/rename/delete, mirroring <see cref="SessionRemoverExecutor"/>'s posture: a plan is
/// never trusted blindly (re-validated right before acting), and every failure becomes a named outcome
/// rather than an unhandled exception.
/// </summary>
public static class FileSystemEntryExecutor
{
    /// <exception cref="ArgumentException"><paramref name="plan"/>.IsSafe is false.</exception>
    public static FileSystemEntryResult Execute(FileSystemEntryPlan plan, SessionRemovalMode deleteMode = SessionRemovalMode.RecycleBin)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsSafe)
        {
            throw new ArgumentException(
                "Refusing to execute an unsafe plan (FileSystemEntryPlan.IsSafe is false) - see its Warnings.",
                nameof(plan));
        }

        try
        {
            return plan.Kind switch
            {
                FileSystemEntryOperationKind.CreateFile => ExecuteCreateFile(plan),
                FileSystemEntryOperationKind.CreateFolder => ExecuteCreateFolder(plan),
                FileSystemEntryOperationKind.Move => ExecuteMove(plan),
                FileSystemEntryOperationKind.Delete => ExecuteDelete(plan, deleteMode),
                _ => throw new ArgumentOutOfRangeException(nameof(plan), plan.Kind, "Unknown operation kind."),
            };
        }
        catch (Exception ex)
        {
            return new FileSystemEntryResult(plan.Kind, plan.TargetPath, plan.DestinationPath, FileSystemEntryOutcome.Failed, ex.Message, ex);
        }
    }

    private static FileSystemEntryResult ExecuteCreateFile(FileSystemEntryPlan plan)
    {
        if (File.Exists(plan.TargetPath) || Directory.Exists(plan.TargetPath))
        {
            return new FileSystemEntryResult(plan.Kind, plan.TargetPath, null, FileSystemEntryOutcome.Failed,
                "Something already exists at that path.", null);
        }

        File.Create(plan.TargetPath).Dispose();
        return new FileSystemEntryResult(plan.Kind, plan.TargetPath, null, FileSystemEntryOutcome.Succeeded, null, null);
    }

    private static FileSystemEntryResult ExecuteCreateFolder(FileSystemEntryPlan plan)
    {
        if (File.Exists(plan.TargetPath) || Directory.Exists(plan.TargetPath))
        {
            return new FileSystemEntryResult(plan.Kind, plan.TargetPath, null, FileSystemEntryOutcome.Failed,
                "Something already exists at that path.", null);
        }

        Directory.CreateDirectory(plan.TargetPath);
        return new FileSystemEntryResult(plan.Kind, plan.TargetPath, null, FileSystemEntryOutcome.Succeeded, null, null);
    }

    private static FileSystemEntryResult ExecuteMove(FileSystemEntryPlan plan)
    {
        string destination = plan.DestinationPath ?? throw new ArgumentException("Move plan is missing its destination.", nameof(plan));

        bool sourceExists = plan.IsDirectory ? Directory.Exists(plan.TargetPath) : File.Exists(plan.TargetPath);
        if (!sourceExists)
        {
            return new FileSystemEntryResult(plan.Kind, plan.TargetPath, destination, FileSystemEntryOutcome.NotPresent, null, null);
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            return new FileSystemEntryResult(plan.Kind, plan.TargetPath, destination, FileSystemEntryOutcome.Failed,
                "Something already exists at the destination.", null);
        }

        if (plan.IsDirectory)
        {
            Directory.Move(plan.TargetPath, destination);
        }
        else
        {
            File.Move(plan.TargetPath, destination);
        }

        return new FileSystemEntryResult(plan.Kind, plan.TargetPath, destination, FileSystemEntryOutcome.Succeeded, null, null);
    }

    private static FileSystemEntryResult ExecuteDelete(FileSystemEntryPlan plan, SessionRemovalMode mode)
    {
        bool exists = plan.IsDirectory ? Directory.Exists(plan.TargetPath) : File.Exists(plan.TargetPath);
        if (!exists)
        {
            return new FileSystemEntryResult(plan.Kind, plan.TargetPath, null, FileSystemEntryOutcome.NotPresent, null, null);
        }

        if (mode == SessionRemovalMode.RecycleBin)
        {
            RecycleBin.Delete(plan.TargetPath);
        }
        else if (plan.IsDirectory)
        {
            Directory.Delete(plan.TargetPath, recursive: true);
        }
        else
        {
            File.Delete(plan.TargetPath);
        }

        return new FileSystemEntryResult(plan.Kind, plan.TargetPath, null, FileSystemEntryOutcome.Succeeded, null, null);
    }
}

namespace Accel.Tests;

using System;
using System.IO;
using Accel.Orchestration;
using Xunit;

/// <summary>
/// Unit tests for <see cref="FileSystemEntryExecutor.Execute"/> - the mutating half. Every test acts
/// only inside a fresh fixture directory. <see cref="SessionRemovalMode.PermanentDelete"/> is used for
/// delete assertions so tests don't have to reach into the real Windows recycle bin; a small dedicated
/// test exercises <see cref="SessionRemovalMode.RecycleBin"/> to prove the path still works, only
/// checking that the file left its original location (the OS recycle bin itself isn't inspectable in
/// CI).
/// </summary>
public sealed class FileSystemEntryExecutorTests : IDisposable
{
    private readonly string _root;

    public FileSystemEntryExecutorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "accel-files-entry-executor-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    [Fact]
    public void Execute_UnsafePlan_Throws()
    {
        var plan = FileSystemEntryPlanner.PlanDelete(Path.Combine(_root, "missing.txt"), _root);
        Assert.False(plan.IsSafe);

        Assert.Throws<ArgumentException>(() => FileSystemEntryExecutor.Execute(plan));
    }

    [Fact]
    public void Execute_CreateFile_CreatesEmptyFileOnDisk()
    {
        var plan = FileSystemEntryPlanner.PlanCreateFile(_root, "new.txt", _root);

        var result = FileSystemEntryExecutor.Execute(plan);

        Assert.Equal(FileSystemEntryOutcome.Succeeded, result.Outcome);
        Assert.True(File.Exists(Path.Combine(_root, "new.txt")));
    }

    [Fact]
    public void Execute_CreateFolder_CreatesDirectoryOnDisk()
    {
        var plan = FileSystemEntryPlanner.PlanCreateFolder(_root, "new-folder", _root);

        var result = FileSystemEntryExecutor.Execute(plan);

        Assert.Equal(FileSystemEntryOutcome.Succeeded, result.Outcome);
        Assert.True(Directory.Exists(Path.Combine(_root, "new-folder")));
    }

    [Fact]
    public void Execute_Move_RenamesFileOnDisk()
    {
        string source = Path.Combine(_root, "old.txt");
        File.WriteAllText(source, "hello");
        string destination = Path.Combine(_root, "new.txt");

        var plan = FileSystemEntryPlanner.PlanMove(source, destination, _root);
        var result = FileSystemEntryExecutor.Execute(plan);

        Assert.Equal(FileSystemEntryOutcome.Succeeded, result.Outcome);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(destination));
        Assert.Equal("hello", File.ReadAllText(destination));
    }

    [Fact]
    public void Execute_Move_MovesDirectoryOnDisk()
    {
        string source = Directory.CreateDirectory(Path.Combine(_root, "old-dir")).FullName;
        File.WriteAllText(Path.Combine(source, "inner.txt"), "content");
        string destination = Path.Combine(_root, "new-dir");

        var plan = FileSystemEntryPlanner.PlanMove(source, destination, _root);
        var result = FileSystemEntryExecutor.Execute(plan);

        Assert.Equal(FileSystemEntryOutcome.Succeeded, result.Outcome);
        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Combine(destination, "inner.txt")));
    }

    [Fact]
    public void Execute_PermanentDelete_RemovesFileFromDisk()
    {
        string target = Path.Combine(_root, "doomed.txt");
        File.WriteAllText(target, "bye");

        var plan = FileSystemEntryPlanner.PlanDelete(target, _root);
        var result = FileSystemEntryExecutor.Execute(plan, SessionRemovalMode.PermanentDelete);

        Assert.Equal(FileSystemEntryOutcome.Succeeded, result.Outcome);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Execute_PermanentDelete_RemovesDirectoryRecursively()
    {
        string target = Directory.CreateDirectory(Path.Combine(_root, "doomed-dir")).FullName;
        File.WriteAllText(Path.Combine(target, "inner.txt"), "bye");

        var plan = FileSystemEntryPlanner.PlanDelete(target, _root);
        var result = FileSystemEntryExecutor.Execute(plan, SessionRemovalMode.PermanentDelete);

        Assert.Equal(FileSystemEntryOutcome.Succeeded, result.Outcome);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void Execute_RecycleBinDelete_RemovesFileFromOriginalLocation()
    {
        string target = Path.Combine(_root, "recycled.txt");
        File.WriteAllText(target, "bye");

        var plan = FileSystemEntryPlanner.PlanDelete(target, _root);
        var result = FileSystemEntryExecutor.Execute(plan, SessionRemovalMode.RecycleBin);

        Assert.Equal(FileSystemEntryOutcome.Succeeded, result.Outcome);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Execute_MoveWhenSourceVanishedSincePlanning_ReportsNotPresent()
    {
        string source = Path.Combine(_root, "old.txt");
        File.WriteAllText(source, "hello");
        string destination = Path.Combine(_root, "new.txt");

        var plan = FileSystemEntryPlanner.PlanMove(source, destination, _root);
        File.Delete(source); // simulate the plan going stale between planning and execution

        var result = FileSystemEntryExecutor.Execute(plan);

        Assert.Equal(FileSystemEntryOutcome.NotPresent, result.Outcome);
    }

    [Fact]
    public void Execute_DeleteWhenTargetVanishedSincePlanning_ReportsNotPresent()
    {
        string target = Path.Combine(_root, "doomed.txt");
        File.WriteAllText(target, "bye");

        var plan = FileSystemEntryPlanner.PlanDelete(target, _root);
        File.Delete(target); // simulate the plan going stale between planning and execution

        var result = FileSystemEntryExecutor.Execute(plan);

        Assert.Equal(FileSystemEntryOutcome.NotPresent, result.Outcome);
    }
}

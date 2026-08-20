namespace Accel.Tests;

using System;
using System.IO;
using Accel.Orchestration;
using Xunit;

/// <summary>
/// Unit tests for <see cref="FileSystemEntryPlanner"/> - pure, read-only validation. Every test builds
/// a fixture root under a fresh temp directory; none of these ever touch the real user profile.
/// </summary>
public sealed class FileSystemEntryPlannerTests : IDisposable
{
    private readonly string _root;

    public FileSystemEntryPlannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "accel-files-entry-planner-test-" + Guid.NewGuid().ToString("N"));
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
    public void PlanCreateFile_InsideRoot_IsSafe()
    {
        var plan = FileSystemEntryPlanner.PlanCreateFile(_root, "new.txt", _root);

        Assert.True(plan.IsSafe);
        Assert.Equal(Path.Combine(_root, "new.txt"), plan.TargetPath);
        Assert.False(plan.Exists);
    }

    [Fact]
    public void PlanCreateFile_InvalidNameChar_IsUnsafe()
    {
        var plan = FileSystemEntryPlanner.PlanCreateFile(_root, "bad?name.txt", _root);
        Assert.False(plan.IsSafe);
        Assert.NotEmpty(plan.Warnings);
    }

    [Fact]
    public void PlanCreateFile_CollidesWithExisting_IsUnsafe()
    {
        File.WriteAllText(Path.Combine(_root, "existing.txt"), string.Empty);

        var plan = FileSystemEntryPlanner.PlanCreateFile(_root, "existing.txt", _root);

        Assert.False(plan.IsSafe);
        Assert.True(plan.Exists);
    }

    [Fact]
    public void PlanCreateFolder_ParentMissing_IsUnsafe()
    {
        string missingParent = Path.Combine(_root, "gone");

        var plan = FileSystemEntryPlanner.PlanCreateFolder(missingParent, "child", _root);

        Assert.False(plan.IsSafe);
    }

    [Fact]
    public void PlanCreateFile_ParentEscapesRootViaDotDot_IsUnsafe()
    {
        string outsideRoot = Path.Combine(_root, "..");

        var plan = FileSystemEntryPlanner.PlanCreateFile(outsideRoot, "evil.txt", _root);

        Assert.False(plan.IsSafe);
    }

    [Fact]
    public void PlanMove_PlainRename_IsSafe()
    {
        string source = Path.Combine(_root, "old.txt");
        File.WriteAllText(source, "hi");
        string destination = Path.Combine(_root, "new.txt");

        var plan = FileSystemEntryPlanner.PlanMove(source, destination, _root);

        Assert.True(plan.IsSafe);
        Assert.False(plan.IsDirectory);
    }

    [Fact]
    public void PlanMove_CrossDirectoryInsideRoot_IsSafe()
    {
        string source = Path.Combine(_root, "old.txt");
        File.WriteAllText(source, "hi");
        string subDir = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;
        string destination = Path.Combine(subDir, "old.txt");

        var plan = FileSystemEntryPlanner.PlanMove(source, destination, _root);

        Assert.True(plan.IsSafe);
    }

    [Fact]
    public void PlanMove_SourceMissing_IsUnsafe()
    {
        string source = Path.Combine(_root, "missing.txt");
        string destination = Path.Combine(_root, "new.txt");

        var plan = FileSystemEntryPlanner.PlanMove(source, destination, _root);

        Assert.False(plan.IsSafe);
    }

    [Fact]
    public void PlanMove_DestinationOutsideRoot_IsUnsafe()
    {
        string source = Path.Combine(_root, "old.txt");
        File.WriteAllText(source, "hi");
        string destination = Path.Combine(_root, "..", "escaped.txt");

        var plan = FileSystemEntryPlanner.PlanMove(source, destination, _root);

        Assert.False(plan.IsSafe);
    }

    [Fact]
    public void PlanMove_DestinationAlreadyExists_IsUnsafe()
    {
        string source = Path.Combine(_root, "old.txt");
        File.WriteAllText(source, "hi");
        string destination = Path.Combine(_root, "taken.txt");
        File.WriteAllText(destination, "already here");

        var plan = FileSystemEntryPlanner.PlanMove(source, destination, _root);

        Assert.False(plan.IsSafe);
    }

    [Fact]
    public void PlanMove_DirectoryIntoItsOwnSubfolder_IsUnsafe()
    {
        string source = Directory.CreateDirectory(Path.Combine(_root, "parent")).FullName;
        string destination = Path.Combine(source, "child", "parent");

        var plan = FileSystemEntryPlanner.PlanMove(source, destination, _root);

        Assert.False(plan.IsSafe);
    }

    [Fact]
    public void PlanMove_NoOpSamePath_IsUnsafe()
    {
        string source = Path.Combine(_root, "same.txt");
        File.WriteAllText(source, "hi");

        var plan = FileSystemEntryPlanner.PlanMove(source, source, _root);

        Assert.False(plan.IsSafe);
    }

    [Fact]
    public void PlanDelete_ExistingFileInsideRoot_IsSafe()
    {
        string target = Path.Combine(_root, "doomed.txt");
        File.WriteAllText(target, "bye");

        var plan = FileSystemEntryPlanner.PlanDelete(target, _root);

        Assert.True(plan.IsSafe);
        Assert.True(plan.Exists);
        Assert.False(plan.IsDirectory);
    }

    [Fact]
    public void PlanDelete_MissingTarget_IsUnsafe()
    {
        string target = Path.Combine(_root, "never-existed.txt");

        var plan = FileSystemEntryPlanner.PlanDelete(target, _root);

        Assert.False(plan.IsSafe);
        Assert.False(plan.Exists);
    }

    [Fact]
    public void PlanDelete_OutsideRoot_IsUnsafe()
    {
        string outside = Path.Combine(Path.GetTempPath(), "accel-files-entry-planner-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "hi");
        try
        {
            var plan = FileSystemEntryPlanner.PlanDelete(outside, _root);
            Assert.False(plan.IsSafe);
        }
        finally
        {
            File.Delete(outside);
        }
    }
}

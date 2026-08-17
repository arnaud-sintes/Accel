namespace Accel.Tests;

using System;
using System.IO;
using System.Linq;
using Accel.Cli;
using Xunit;

/// <summary>
/// Unit tests for panel B's pure filesystem-walking builder. Uses real temporary directories (genuine
/// filesystem I/O, not mockable telemetry) - same convention as other disk-touching pure-logic tests
/// in this project.
/// </summary>
public sealed class FilesTreeBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "accel-files-tree-tests-" + Guid.NewGuid().ToString("N"));

    public FilesTreeBuilderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup only - never fail a test run over a leftover temp folder.
        }
    }

    [Fact]
    public void BuildRootChildren_MissingPath_ReturnsNull()
    {
        Assert.Null(FilesTreeBuilder.BuildRootChildren(Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void BuildRootChildren_NullOrEmptyPath_ReturnsNull()
    {
        Assert.Null(FilesTreeBuilder.BuildRootChildren(null));
        Assert.Null(FilesTreeBuilder.BuildRootChildren(string.Empty));
    }

    [Fact]
    public void BuildRootChildren_EmptyDirectory_ReturnsEmpty()
    {
        var result = FilesTreeBuilder.BuildRootChildren(_root);

        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void BuildChildren_DirectoriesSortBeforeFiles_BothAlphabetical()
    {
        Directory.CreateDirectory(Path.Combine(_root, "zzz-folder"));
        Directory.CreateDirectory(Path.Combine(_root, "aaa-folder"));
        File.WriteAllText(Path.Combine(_root, "zzz-file.txt"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "aaa-file.txt"), string.Empty);

        var result = FilesTreeBuilder.BuildChildren(_root);

        Assert.Equal(new[] { "aaa-folder", "zzz-folder", "aaa-file.txt", "zzz-file.txt" }, result.Select(c => c.Name));
        Assert.True(result[0].IsDirectory);
        Assert.True(result[1].IsDirectory);
        Assert.False(result[2].IsDirectory);
        Assert.False(result[3].IsDirectory);
    }

    /// <summary>
    /// The bug this pins down: an earlier revision built the whole subtree in one call against a
    /// single shared node budget, walked depth-first - so a large/deep sibling earlier in
    /// alphabetical order could exhaust the budget before a later sibling was even enumerated,
    /// silently truncating the top-level listing itself. One <see cref="FilesTreeBuilder.BuildChildren"/>
    /// call now only ever enumerates ONE level, so a sibling's own size/depth can never affect
    /// another sibling's visibility - a large nested folder does not touch this call's result at all.
    /// </summary>
    [Fact]
    public void BuildChildren_DoesNotRecurse_AndLargeSiblingNeverHidesLaterOnes()
    {
        var big = Directory.CreateDirectory(Path.Combine(_root, "aaa-big"));
        for (int i = 0; i < 50; i++)
        {
            var nested = big.CreateSubdirectory($"nested-{i}");
            for (int j = 0; j < 5; j++)
            {
                File.WriteAllText(Path.Combine(nested.FullName, $"file-{j}.txt"), string.Empty);
            }
        }

        Directory.CreateDirectory(Path.Combine(_root, "zzz-late-sibling"));

        var result = FilesTreeBuilder.BuildChildren(_root);

        Assert.Equal(new[] { "aaa-big", "zzz-late-sibling" }, result.Select(c => c.Name));

        var bigNode = result.Single(n => n.Name == "aaa-big");
        Assert.True(bigNode.IsDirectory);
        Assert.True(bigNode.HasChildren);
    }

    [Fact]
    public void BuildChildren_FolderHasChildren_ReflectsWhetherItHasAnyEntries()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty-folder"));
        var nonEmpty = Directory.CreateDirectory(Path.Combine(_root, "non-empty-folder"));
        File.WriteAllText(Path.Combine(nonEmpty.FullName, "leaf.txt"), string.Empty);

        var result = FilesTreeBuilder.BuildChildren(_root);

        Assert.False(result.Single(n => n.Name == "empty-folder").HasChildren);
        Assert.True(result.Single(n => n.Name == "non-empty-folder").HasChildren);
    }

    [Fact]
    public void BuildChildren_FileNode_PathIsFull_AndHasChildrenIsFalse()
    {
        string filePath = Path.Combine(_root, "note.txt");
        File.WriteAllText(filePath, string.Empty);

        var result = FilesTreeBuilder.BuildChildren(_root);

        var file = Assert.Single(result);
        Assert.Equal(filePath, file.Path);
        Assert.False(file.HasChildren);
    }
}

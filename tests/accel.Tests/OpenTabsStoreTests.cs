namespace Accel.Tests;

using System;
using System.IO;
using Accel.App.Services;
using Xunit;

public sealed class OpenTabsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "accel-open-tabs-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "accel-open-tabs.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Temp cleanup only.
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTabsOrderAndSelection()
    {
        var store = new OpenTabsStore(FilePath);
        var set = new SavedTabSet(
            new[]
            {
                new SavedTab(SavedTabKind.Session, "11111111-1111-1111-1111-111111111111", "Refactor", @"C:\projects\app"),
                new SavedTab(SavedTabKind.Shell, "abc", "Terminal - app", @"C:\projects\app"),
                new SavedTab(SavedTabKind.File, @"C:\projects\app\README.md", "README.md", null),
            },
            SelectedIndex: 1);

        Assert.True(store.Save(set));

        var loaded = store.Load();
        Assert.Equal(set.Tabs, loaded.Tabs);
        Assert.Equal(1, loaded.SelectedIndex);
    }

    [Fact]
    public void Load_ReadsAMissingFileAsEmpty()
    {
        Assert.Empty(new OpenTabsStore(FilePath).Load().Tabs);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"version\": 99, \"tabs\": [{\"kind\":\"session\",\"id\":\"x\"}]}")]
    [InlineData("{\"version\": 1}")]
    public void Load_FailsClosedOnAnUnreadableOrForeignFile(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, content);

        Assert.Empty(new OpenTabsStore(FilePath).Load().Tabs);
    }

    [Fact]
    public void Load_DropsEntriesWithoutAnId_AndAnOutOfRangeSelection()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            FilePath,
            "{\"version\":1,\"selectedIndex\":5,\"tabs\":[{\"kind\":\"file\",\"id\":\"\"},{\"kind\":\"file\",\"id\":\"C:\\\\a.txt\"}]}");

        var loaded = new OpenTabsStore(FilePath).Load();

        Assert.Single(loaded.Tabs);
        Assert.Null(loaded.SelectedIndex);
    }

    [Fact]
    public void Save_ReplacesAnExistingFile_AndLeavesNoTempFilesBehind()
    {
        var store = new OpenTabsStore(FilePath);
        store.Save(new SavedTabSet(new[] { new SavedTab(SavedTabKind.File, @"C:\a.txt", null, null) }, null));
        store.Save(new SavedTabSet(Array.Empty<SavedTab>(), null));

        Assert.Empty(store.Load().Tabs);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void Save_ReportsFalse_InsteadOfThrowing_WhenTheLocationCannotBeWritten()
    {
        // A path whose "directory" is an existing file can never be created.
        Directory.CreateDirectory(_directory);
        string blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "x");

        Assert.False(new OpenTabsStore(Path.Combine(blocker, "tabs.json")).Save(SavedTabSet.Empty));
    }
}

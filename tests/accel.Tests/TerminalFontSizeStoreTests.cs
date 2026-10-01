namespace Accel.Tests;

using System;
using System.IO;
using System.Text.Json.Nodes;
using Accel.App.Controls;
using Accel.App.Services;
using Xunit;

/// <summary>
/// Pins the host side of the terminal zoom feature: the clamp rule both sides of the WebView2 bridge
/// mirror (<see cref="TerminalFontSize"/>), the <c>accel-ui.json</c> round-trip
/// (<see cref="TerminalFontSizeStore"/>, against real temp files - the only file-system use here), and
/// the two generated scripts (<see cref="TerminalView.BuildFontSizeInitScript"/> /
/// <see cref="TerminalView.BuildSetFontSizeScript"/>). The page-side half (<c>terminal.js</c>'s
/// <c>setFontSize</c>) is not unit-testable in this stack, same as the rest of that file.
/// </summary>
public class TerminalFontSizeStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"accel-ui-tests-{Guid.NewGuid():N}");

    public TerminalFontSizeStoreTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup only.
        }
    }

    private string StorePath => Path.Combine(_directory, TerminalFontSizeStore.DefaultFileName);

    [Theory]
    [InlineData(14, 14)]
    [InlineData(8, 8)]
    [InlineData(32, 32)]
    [InlineData(7, 8)]
    [InlineData(0, 8)]
    [InlineData(-5, 8)]
    [InlineData(33, 32)]
    [InlineData(int.MaxValue, 32)]
    public void Clamp_KeepsValuesInsideTheBoundsAndPinsOutliersToTheNearestBound(int input, int expected)
    {
        Assert.Equal(expected, TerminalFontSize.Clamp(input));
    }

    [Fact]
    public void Default_IsInsideTheBounds()
    {
        // A default outside its own bounds would be silently rewritten on the first Save, so the
        // "never touched the shortcuts" experience would differ from the shipped default.
        Assert.Equal(TerminalFontSize.Default, TerminalFontSize.Clamp(TerminalFontSize.Default));
    }

    [Fact]
    public void Load_MissingFile_ReturnsTheDefault()
    {
        var store = new TerminalFontSizeStore(StorePath);

        Assert.Equal(TerminalFontSize.Default, store.Load());
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTheValue()
    {
        var store = new TerminalFontSizeStore(StorePath);

        Assert.True(store.Save(18));

        Assert.Equal(18, store.Load());
        Assert.True(File.Exists(StorePath));
    }

    [Fact]
    public void Save_ClampsBeforeWriting_SoTheFileNeverHoldsAnOutOfRangeValue()
    {
        var store = new TerminalFontSizeStore(StorePath);

        store.Save(200);

        var root = JsonNode.Parse(File.ReadAllText(StorePath)) as JsonObject;
        Assert.NotNull(root);
        Assert.Equal(TerminalFontSize.Maximum, (int)root![TerminalFontSizeStore.FontSizeKey]!);
    }

    [Theory]
    [InlineData("""{ "terminalFontSize": 3 }""", TerminalFontSize.Minimum)]
    [InlineData("""{ "terminalFontSize": 999 }""", TerminalFontSize.Maximum)]
    public void Load_HandEditedOutOfRangeValue_IsClamped(string json, int expected)
    {
        File.WriteAllText(StorePath, json);
        var store = new TerminalFontSizeStore(StorePath);

        Assert.Equal(expected, store.Load());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{ "terminalFontSize": "sixteen" }""")]
    [InlineData("""{ "terminalFontSize": 15.5 }""")]
    [InlineData("""{ "terminalFontSize": null }""")]
    [InlineData("""{ "somethingElse": 1 }""")]
    public void Load_MalformedOrUnsetContent_FallsBackToTheDefaultWithoutThrowing(string json)
    {
        File.WriteAllText(StorePath, json);
        var store = new TerminalFontSizeStore(StorePath);

        Assert.Equal(TerminalFontSize.Default, store.Load());
    }

    [Fact]
    public void Save_PreservesUnrelatedKeysAlreadyInTheFile()
    {
        // accel-ui.json is meant to grow into the home for other UI preferences - a save from this
        // store must be a read-modify-write of the whole object, never a typed overwrite that drops
        // keys it does not know about (the same reason SettingsFile never uses a POCO).
        File.WriteAllText(StorePath, """{ "someFuturePreference": true, "terminalFontSize": 12 }""");
        var store = new TerminalFontSizeStore(StorePath);

        store.Save(20);

        var root = JsonNode.Parse(File.ReadAllText(StorePath)) as JsonObject;
        Assert.NotNull(root);
        Assert.True((bool)root!["someFuturePreference"]!);
        Assert.Equal(20, (int)root[TerminalFontSizeStore.FontSizeKey]!);
    }

    [Fact]
    public void Save_OverAMalformedFile_RewritesItCleanly()
    {
        File.WriteAllText(StorePath, "{ this is not json");
        var store = new TerminalFontSizeStore(StorePath);

        Assert.True(store.Save(16));

        Assert.Equal(16, store.Load());
    }

    [Fact]
    public void Save_CreatesAMissingParentDirectory()
    {
        var nested = Path.Combine(_directory, "nested", "deeper", TerminalFontSizeStore.DefaultFileName);
        var store = new TerminalFontSizeStore(nested);

        Assert.True(store.Save(10));

        Assert.Equal(10, new TerminalFontSizeStore(nested).Load());
    }

    [Fact]
    public void Save_LeavesNoTempFileBehind()
    {
        var store = new TerminalFontSizeStore(StorePath);

        store.Save(11);
        store.Save(12);

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void Constructor_RejectsNullOrEmptyPath()
    {
        Assert.Throws<ArgumentNullException>(() => new TerminalFontSizeStore(null!));
        Assert.Throws<ArgumentException>(() => new TerminalFontSizeStore(string.Empty));
    }

    [Fact]
    public void DefaultPath_LivesNextToTheOtherAccelStateFiles()
    {
        var expectedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

        Assert.Equal(Path.Combine(expectedDirectory, "accel-ui.json"), TerminalFontSizeStore.DefaultPath());
    }

    [Theory]
    [InlineData("""{ "terminalNativeSelection": false }""", false)]
    [InlineData("""{ "terminalNativeSelection": true }""", true)]
    [InlineData("""{ "terminalNativeSelection": "no" }""", true)]
    [InlineData("""{ "terminalFontSize": 12 }""", true)]
    [InlineData("not json", true)]
    public void LoadNativeSelection_DefaultsToOn_AndHonoursAnExplicitBoolean(string json, bool expected)
    {
        File.WriteAllText(StorePath, json);

        Assert.Equal(expected, new TerminalFontSizeStore(StorePath).LoadNativeSelection());
    }

    [Fact]
    public void LoadNativeSelection_MissingFile_IsOn()
    {
        Assert.True(new TerminalFontSizeStore(StorePath).LoadNativeSelection());
    }

    [Fact]
    public void Save_PreservesTheNativeSelectionSetting()
    {
        File.WriteAllText(StorePath, """{ "terminalNativeSelection": false }""");
        var store = new TerminalFontSizeStore(StorePath);

        store.Save(16);

        Assert.False(store.LoadNativeSelection());
    }

    [Fact]
    public void BuildNativeSelectionInitScript_ProducesTheExpectedAssignment()
    {
        Assert.Equal("window.accelNativeSelection = true;", TerminalView.BuildNativeSelectionInitScript(true));
        Assert.Equal("window.accelNativeSelection = false;", TerminalView.BuildNativeSelectionInitScript(false));
    }

    [Fact]
    public void BuildFontSizeInitScript_ProducesTheExpectedAssignment()
    {
        Assert.Equal("window.accelTerminalFontSize = 16;", TerminalView.BuildFontSizeInitScript(16));
    }

    [Fact]
    public void BuildFontSizeInitScript_ClampsSoAHandEditedFileCannotPushAnAbsurdValueIntoThePage()
    {
        Assert.Equal(
            $"window.accelTerminalFontSize = {TerminalFontSize.Maximum};",
            TerminalView.BuildFontSizeInitScript(10_000));
        Assert.Equal(
            $"window.accelTerminalFontSize = {TerminalFontSize.Minimum};",
            TerminalView.BuildFontSizeInitScript(-1));
    }

    [Fact]
    public void BuildSetFontSizeScript_ProducesTheExpectedCall_Clamped()
    {
        Assert.Equal("window.accelSetTerminalFontSize(20);", TerminalView.BuildSetFontSizeScript(20));
        Assert.Equal(
            $"window.accelSetTerminalFontSize({TerminalFontSize.Maximum});",
            TerminalView.BuildSetFontSizeScript(500));
    }
}

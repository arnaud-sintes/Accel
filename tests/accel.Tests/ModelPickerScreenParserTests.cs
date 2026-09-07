namespace Accel.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using Accel.Orchestration;
using Xunit;

/// <summary>
/// Unit tests for <see cref="ModelPickerScreenParser"/> against captured renderings of Claude Code's
/// real <c>/model</c> picker. This is the fragile half of catalog discovery - it parses a UI, whose
/// layout is not a contract - so it is tested against a byte-accurate transcription of what the TUI
/// actually painted (160x60, taken from the <c>model-picker-probe</c> dev verb) rather than against
/// an idealized shape the parser would trivially pass.
/// </summary>
public class ModelPickerScreenParserTests
{
    /// <summary>
    /// The real picker as rendered. Note the two "&lt;level&gt; effort" lines: the status header at
    /// the top and the slider near the bottom. Telling them apart is the parser's one genuinely
    /// load-bearing behaviour.
    /// </summary>
    private static IReadOnlyList<string> RealPickerScreen() => new[]
    {
        " ✻          Claude Code v2.1.263",
        " ▐▛███▜▌    Opus with medium effort · Claude Enterprise",
        "  ▝▜█████▛▘  C:\\projects\\Accel",
        "  ────────────────────────────────────────────────────────────────────────",
        "   Select model",
        "   Switch between Claude models. Your pick becomes the default for new sessions. For other/previous model names, specify with --model.",
        "     1. Default (recommended)  Sonnet 5 · Org default",
        "   ❯ 2. Opus (1M context) ✦    Opus 5 with 1M context · Best for everyday, complex tasks",
        "     3. Sonnet                 Sonnet 5 · Efficient for routine tasks · Org default",
        "     4. Haiku                  Haiku 4.5 · Fastest for quick answers",
        "   ❯ Medium effort / to adjust",
        "   Enter to set as default · s to use this session only · Esc to cancel",
    };

    [Fact]
    public void Parse_RealPickerScreen_RecoversEveryRowInOrder()
    {
        var parsed = ModelPickerScreenParser.Parse(RealPickerScreen());

        Assert.True(parsed.IsUsable);
        Assert.Equal(4, parsed.Rows.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, Array.ConvertAll(parsed.Rows.ToArray(), row => row.Number));
        Assert.Equal("Default (recommended)", parsed.Rows[0].Label);
        Assert.Equal("Opus (1M context) ✦", parsed.Rows[1].Label);
        Assert.Equal("Sonnet", parsed.Rows[2].Label);
        Assert.Equal("Haiku", parsed.Rows[3].Label);
    }

    [Fact]
    public void Parse_RealPickerScreen_SplitsTheDescriptionColumnOffTheLabel()
    {
        var parsed = ModelPickerScreenParser.Parse(RealPickerScreen());

        Assert.Equal("Sonnet 5 · Efficient for routine tasks · Org default", parsed.Rows[2].Description);
        Assert.Equal("Haiku 4.5 · Fastest for quick answers", parsed.Rows[3].Description);
    }

    [Fact]
    public void Parse_RealPickerScreen_MarksTheCursorRowSelected()
    {
        var parsed = ModelPickerScreenParser.Parse(RealPickerScreen());

        Assert.False(parsed.Rows[0].Selected);
        Assert.True(parsed.Rows[1].Selected);
        Assert.False(parsed.Rows[2].Selected);
    }

    /// <summary>
    /// The regression this parser was rewritten for. Scanning top-down finds the status header's
    /// "Opus with medium effort", which never changes while the slider moves - so the effort walk read
    /// the same tier for every model and reported a plausible, entirely wrong ladder.
    /// </summary>
    [Fact]
    public void Parse_ReadsTheSliderNotTheStatusHeader()
    {
        var screen = new List<string>(RealPickerScreen());

        // Header says medium; slider says max. A correct parse reports the slider.
        screen[10] = "   ❯ Max effort / to adjust";

        Assert.Equal("max", ModelPickerScreenParser.Parse(screen).CurrentEffort);
    }

    [Fact]
    public void Parse_NoSliderLine_ReportsNoEffort_NotAnUnknownOne()
    {
        var screen = new List<string>(RealPickerScreen());

        // Haiku's shape: the slider line is simply absent. "No effort control" must not be conflated
        // with "effort unknown" - only the header's mention of effort remains, and it must be ignored.
        screen.RemoveAt(10);

        Assert.Null(ModelPickerScreenParser.Parse(screen).CurrentEffort);
    }

    [Fact]
    public void Parse_ReadsTheCliVersionBanner()
    {
        Assert.Equal("2.1.263", ModelPickerScreenParser.Parse(RealPickerScreen()).ClaudeCliVersion);
    }

    /// <summary>
    /// A permission prompt and a <c>/resume</c> list also paint numbered rows, so rows alone must
    /// never be treated as a picker - <see cref="ModelPickerScreen.IsUsable"/> is what callers gate on.
    /// </summary>
    [Fact]
    public void Parse_NumberedRowsWithoutPickerChrome_IsNotUsable()
    {
        var parsed = ModelPickerScreenParser.Parse(new[]
        {
            "   Do you want to proceed?",
            "     1. Yes",
            "     2. No, and tell Claude what to do differently",
        });

        Assert.False(parsed.SawPickerChrome);
        Assert.False(parsed.IsUsable);
    }

    [Fact]
    public void Parse_EmptyScreen_IsNotUsable_AndDoesNotThrow()
    {
        var parsed = ModelPickerScreenParser.Parse(Array.Empty<string>());

        Assert.Empty(parsed.Rows);
        Assert.Null(parsed.CurrentEffort);
        Assert.False(parsed.IsUsable);
    }

    [Fact]
    public void Parse_RowWithNoDescriptionColumn_YieldsAnEmptyDescription()
    {
        var parsed = ModelPickerScreenParser.Parse(new[]
        {
            "   Select model",
            "     1. Sonnet",
        });

        Assert.True(parsed.IsUsable);
        Assert.Equal("Sonnet", parsed.Rows[0].Label);
        Assert.Equal(string.Empty, parsed.Rows[0].Description);
    }
}

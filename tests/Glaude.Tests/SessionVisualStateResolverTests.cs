using Glaude.App.ViewModels;
using Xunit;

namespace Glaude.Tests;

/// <summary>
/// P1-T4 / locked-in decision 9: the four IsRunning x IsFocused combinations. Pins down that
/// glyph shape, font weight, and colour each independently carry state (never color-only), and
/// that all four combinations produce distinct automation text for screen readers.
/// </summary>
public class SessionVisualStateResolverTests
{
    [Fact]
    public void Running_Focused_IsFilledGlyph_Bold_WithRunningFocusedColor()
    {
        var state = SessionVisualStateResolver.Resolve(isRunning: true, isFocused: true);
        Assert.Equal(SessionVisualStateResolver.RunningGlyph, state.Glyph);
        Assert.True(state.IsBold);
        Assert.Equal(SessionVisualStateResolver.RunningFocusedColorHex, state.ColorHex);
    }

    [Fact]
    public void Running_NotFocused_IsFilledGlyph_NotBold_WithRunningColor()
    {
        var state = SessionVisualStateResolver.Resolve(isRunning: true, isFocused: false);
        Assert.Equal(SessionVisualStateResolver.RunningGlyph, state.Glyph);
        Assert.False(state.IsBold);
        Assert.Equal(SessionVisualStateResolver.RunningColorHex, state.ColorHex);
    }

    [Fact]
    public void Idle_Focused_IsHollowGlyph_Bold_WithIdleFocusedColor()
    {
        var state = SessionVisualStateResolver.Resolve(isRunning: false, isFocused: true);
        Assert.Equal(SessionVisualStateResolver.IdleGlyph, state.Glyph);
        Assert.True(state.IsBold);
        Assert.Equal(SessionVisualStateResolver.IdleFocusedColorHex, state.ColorHex);
    }

    [Fact]
    public void Idle_NotFocused_IsHollowGlyph_NotBold_WithIdleColor()
    {
        var state = SessionVisualStateResolver.Resolve(isRunning: false, isFocused: false);
        Assert.Equal(SessionVisualStateResolver.IdleGlyph, state.Glyph);
        Assert.False(state.IsBold);
        Assert.Equal(SessionVisualStateResolver.IdleColorHex, state.ColorHex);
    }

    [Fact]
    public void GlyphAloneDistinguishesRunningFromIdle_RegardlessOfFocus()
    {
        // Never color-only: strip color out of the picture and the glyph alone must still
        // distinguish the two IsRunning values, for both IsFocused values.
        Assert.NotEqual(
            SessionVisualStateResolver.Resolve(true, true).Glyph,
            SessionVisualStateResolver.Resolve(false, true).Glyph);
        Assert.NotEqual(
            SessionVisualStateResolver.Resolve(true, false).Glyph,
            SessionVisualStateResolver.Resolve(false, false).Glyph);
    }

    [Fact]
    public void BoldAloneDistinguishesFocusedFromNotFocused_RegardlessOfRunning()
    {
        Assert.NotEqual(
            SessionVisualStateResolver.Resolve(true, true).IsBold,
            SessionVisualStateResolver.Resolve(true, false).IsBold);
        Assert.NotEqual(
            SessionVisualStateResolver.Resolve(false, true).IsBold,
            SessionVisualStateResolver.Resolve(false, false).IsBold);
    }

    [Fact]
    public void AllFourCombinations_HaveDistinctColorsAndAutomationNames()
    {
        var combos = new[]
        {
            SessionVisualStateResolver.Resolve(true, true),
            SessionVisualStateResolver.Resolve(true, false),
            SessionVisualStateResolver.Resolve(false, true),
            SessionVisualStateResolver.Resolve(false, false),
        };

        Assert.Equal(4, new HashSet<string>(Array.ConvertAll(combos, c => c.ColorHex)).Count);
        Assert.Equal(4, new HashSet<string>(Array.ConvertAll(combos, c => c.AutomationName)).Count);
    }

    [Fact]
    public void AutomationName_MentionsRunningOrIdle_AndFocusedWhenApplicable()
    {
        Assert.Contains("Running", SessionVisualStateResolver.Resolve(true, false).AutomationName);
        Assert.Contains("Running", SessionVisualStateResolver.Resolve(true, true).AutomationName);
        Assert.Contains("focused", SessionVisualStateResolver.Resolve(true, true).AutomationName);
        Assert.Contains("Idle", SessionVisualStateResolver.Resolve(false, false).AutomationName);
        Assert.Contains("Idle", SessionVisualStateResolver.Resolve(false, true).AutomationName);
        Assert.Contains("focused", SessionVisualStateResolver.Resolve(false, true).AutomationName);
        Assert.DoesNotContain("focused", SessionVisualStateResolver.Resolve(true, false).AutomationName);
        Assert.DoesNotContain("focused", SessionVisualStateResolver.Resolve(false, false).AutomationName);
    }
}

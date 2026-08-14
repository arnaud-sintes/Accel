namespace Glaude.Tests;

using System.Linq;
using Glaude.Cli;
using Xunit;

/// <summary>
/// Unit tests for the pure <see cref="MonitorColumnLayout.Compute"/> - the single source of truth
/// <c>MonitorForm</c>'s header strip and owner-drawn rows both use for the six-plus-one column
/// X-offsets/widths. No WinForms control is ever instantiated here; only the layout math is
/// exercised.
/// </summary>
public class MonitorColumnLayoutTests
{
    [Fact]
    public void Compute_WideWidth_GivesFlexibleColumnsGenerousSpaceInDesignedProportions()
    {
        var slots = MonitorColumnLayout.Compute(2000);

        var name = slots.Single(s => s.Header == "Name");
        var model = slots.Single(s => s.Header == "Model");
        var context = slots.Single(s => s.Header == "Context");

        // Design: Name gets the most room, Model the least, Context a bit more than Model.
        Assert.True(name.Width > model.Width);
        Assert.True(context.Width > model.Width);
        Assert.True(name.Width > context.Width);
    }

    [Fact]
    public void Compute_NarrowWidth_ShrinksColumnsButNeverBelowClampedMinimum()
    {
        var wideSlots = MonitorColumnLayout.Compute(2000);
        var narrowSlots = MonitorColumnLayout.Compute(500);

        var wideName = wideSlots.Single(s => s.Header == "Name");
        var narrowName = narrowSlots.Single(s => s.Header == "Name");

        Assert.True(narrowName.Width < wideName.Width);

        // The leading state-glyph slot is a fixed 20px column by design (it only ever holds a
        // single glyph character), never a flexible/clamped one - every other column is clamped
        // to the shared minimum.
        foreach (var slot in narrowSlots.Where(s => s.Header != string.Empty))
        {
            Assert.True(slot.Width >= 30, $"{slot.Header} width {slot.Width} fell below the clamped minimum.");
        }
    }

    [Theory]
    [InlineData(50)]
    [InlineData(0)]
    [InlineData(-100)]
    public void Compute_ExtremelyNarrowOrNonPositiveWidth_DoesNotThrowAndStaysValid(int availableWidth)
    {
        var slots = MonitorColumnLayout.Compute(availableWidth);

        Assert.Equal(7, slots.Length);

        foreach (var slot in slots)
        {
            Assert.True(slot.X >= 0, $"{slot.Header} X {slot.X} was negative.");
        }

        // The leading state-glyph slot is a fixed 20px column by design; every other (fixed or
        // flexible) column is clamped to the shared minimum.
        foreach (var slot in slots.Where(s => s.Header != string.Empty))
        {
            Assert.True(slot.Width >= 30, $"{slot.Header} width {slot.Width} fell below the clamped minimum.");
        }
    }

    [Fact]
    public void Compute_ColumnsAreAlwaysMonotonicallyIncreasingWithNoOverlap()
    {
        foreach (int width in new[] { -100, 0, 50, 300, 990, 5000 })
        {
            var slots = MonitorColumnLayout.Compute(width);

            for (int i = 1; i < slots.Length; i++)
            {
                var previous = slots[i - 1];
                var current = slots[i];

                Assert.True(
                    current.X >= previous.X + previous.Width,
                    $"At width {width}, column '{current.Header}' (X={current.X}) overlaps the previous column '{previous.Header}' (X={previous.X}, Width={previous.Width}).");
            }
        }
    }

    [Fact]
    public void Compute_AlwaysReturnsHeadersInFixedColumnOrder()
    {
        var slots = MonitorColumnLayout.Compute(990);

        Assert.Equal(
            new[] { string.Empty, "ID", "Name", "Type", "Model", "Effort", "Context" },
            slots.Select(s => s.Header).ToArray());
    }
}

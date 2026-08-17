using Accel.Metrics;
using Xunit;

namespace Accel.Tests;

public class ModelWindowTableTests
{
    [Fact]
    public void ExactMatch_ReturnsTableValue()
    {
        Assert.Equal(1_000_000, ModelWindowTable.Resolve("claude-opus-4-1m"));
    }

    [Fact]
    public void PrefixMatch_DatedModelId_ReturnsPrefixValue()
    {
        // "claude-haiku-4-5-20251001" is not an exact table entry, but its prefix
        // "claude-haiku" is - must resolve via longest-prefix match, not exact-only.
        Assert.Equal(200_000, ModelWindowTable.Resolve("claude-haiku-4-5-20251001"));
    }

    [Fact]
    public void PrefixMatch_PicksLongestMatchingPrefix()
    {
        // "claude-opus-4-1m-preview" matches both "claude-opus" (shorter) and
        // "claude-opus-4-1m" (longer) - longest must win.
        Assert.Equal(1_000_000, ModelWindowTable.Resolve("claude-opus-4-1m-preview"));
    }

    [Fact]
    public void UnrecognizedString_ReturnsDefault200000()
    {
        Assert.Equal(200_000, ModelWindowTable.Resolve("totally-unknown-model-xyz"));
    }

    [Fact]
    public void EmptyOrNull_ReturnsDefault_NoThrow()
    {
        Assert.Equal(200_000, ModelWindowTable.Resolve(string.Empty));
        Assert.Equal(200_000, ModelWindowTable.Resolve(null));
    }

    [Fact]
    public void DefaultWindowConstant_Is200000()
    {
        Assert.Equal(200_000, ModelWindowTable.DefaultWindow);
    }

    // ---- Bug-fix pass (UI-H): real model ids, verified against real on-disk transcript
    // evidence on this machine (see ModelWindowTable.cs's class-summary comment for the
    // scan methodology and exact numbers/files) rather than assumed. ----

    [Fact]
    public void ClaudeSonnet5_ResolvesTo1Million_Matched()
    {
        int window = ModelWindowTable.Resolve("claude-sonnet-5", out bool matched);
        Assert.Equal(1_000_000, window);
        Assert.True(matched);
    }

    [Fact]
    public void ClaudeOpus48_ResolvesTo1Million_Matched()
    {
        // Strongest real evidence found this pass: observed usage clusters just under
        // 1,000,000 and never exceeds it across ~2600 real assistant entries.
        int window = ModelWindowTable.Resolve("claude-opus-4-8", out bool matched);
        Assert.Equal(1_000_000, window);
        Assert.True(matched);
    }

    [Fact]
    public void ClaudeOpus5_ResolvesTo1Million_Matched()
    {
        Assert.Equal(1_000_000, ModelWindowTable.Resolve("claude-opus-5"));
    }

    [Fact]
    public void ClaudeFable5_ResolvesTo1Million_Matched()
    {
        Assert.Equal(1_000_000, ModelWindowTable.Resolve("claude-fable-5"));
    }

    [Fact]
    public void ClaudeSonnet5_DatedId_StillMatchesViaPrefix()
    {
        Assert.Equal(1_000_000, ModelWindowTable.Resolve("claude-sonnet-5-20260101"));
    }

    [Fact]
    public void ClaudeHaiku45Dated_StaysAtDefault_HonestlyUnmatched()
    {
        // No real on-disk evidence found this pass contradicts 200,000 for haiku-4-5 (max
        // observed usage on this machine: 181,577), so it is deliberately left out of the
        // table - falls through to the default, and matched must be false so downstream
        // "assumed" flags reflect that this is genuinely unverified, not confirmed.
        int window = ModelWindowTable.Resolve("claude-haiku-4-5-20251001", out bool matched);
        Assert.Equal(200_000, window);
        Assert.False(matched);
    }
}

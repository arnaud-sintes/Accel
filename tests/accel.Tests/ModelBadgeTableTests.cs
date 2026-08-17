using Accel.Metrics;
using Xunit;

namespace Accel.Tests;

/// <summary>
/// P1-T4: mirrors <c>ModelWindowTableTests</c>'s exact prefix-string cases (same underlying model
/// family list, see <see cref="ModelWindowTable"/>'s own tests/comments), plus badge letter/colour
/// assertions per family and the "?" fallback.
/// </summary>
public class ModelBadgeTableTests
{
    [Fact]
    public void ExactMatch_ReturnsOpusBadge()
    {
        var badge = ModelBadgeTable.Resolve("claude-opus-4-1m");
        Assert.Equal("O", badge.Letter);
        Assert.True(badge.Matched);
    }

    [Fact]
    public void PrefixMatch_DatedModelId_ReturnsPrefixFamily()
    {
        // "claude-haiku-4-5-20251001" is not an exact table entry, but its prefix
        // "claude-haiku" is - must resolve via longest-prefix match, not exact-only.
        var badge = ModelBadgeTable.Resolve("claude-haiku-4-5-20251001");
        Assert.Equal("H", badge.Letter);
        Assert.True(badge.Matched);
    }

    [Fact]
    public void PrefixMatch_PicksLongestMatchingPrefix()
    {
        // "claude-opus-4-1m-preview" matches "claude-opus" - only one Opus-family entry exists,
        // so this also proves prefix matching (not exact-only) resolves it.
        var badge = ModelBadgeTable.Resolve("claude-opus-4-1m-preview");
        Assert.Equal("O", badge.Letter);
        Assert.True(badge.Matched);
    }

    [Fact]
    public void UnrecognizedString_ReturnsUnmatchedFallback()
    {
        var badge = ModelBadgeTable.Resolve("totally-unknown-model-xyz");
        Assert.Equal("?", badge.Letter);
        Assert.False(badge.Matched);
        Assert.Equal(ModelBadge.UnmatchedColorHex, badge.ColorHex);
    }

    [Fact]
    public void EmptyOrNull_ReturnsUnmatchedFallback_NoThrow()
    {
        Assert.Equal("?", ModelBadgeTable.Resolve(string.Empty).Letter);
        Assert.Equal("?", ModelBadgeTable.Resolve(null).Letter);
        Assert.False(ModelBadgeTable.Resolve(string.Empty).Matched);
        Assert.False(ModelBadgeTable.Resolve(null).Matched);
    }

    // ---- Real model ids from ModelWindowTableTests's own bug-fix-pass cases ----

    [Fact]
    public void ClaudeSonnet5_ResolvesToSonnetBadge_Matched()
    {
        var badge = ModelBadgeTable.Resolve("claude-sonnet-5");
        Assert.Equal("S", badge.Letter);
        Assert.True(badge.Matched);
    }

    [Fact]
    public void ClaudeOpus48_ResolvesToOpusBadge_Matched()
    {
        var badge = ModelBadgeTable.Resolve("claude-opus-4-8");
        Assert.Equal("O", badge.Letter);
        Assert.True(badge.Matched);
    }

    [Fact]
    public void ClaudeOpus5_ResolvesToOpusBadge_Matched()
    {
        Assert.Equal("O", ModelBadgeTable.Resolve("claude-opus-5").Letter);
    }

    [Fact]
    public void ClaudeFable5_ResolvesToFableBadge_Matched()
    {
        var badge = ModelBadgeTable.Resolve("claude-fable-5");
        Assert.Equal("F", badge.Letter);
        Assert.True(badge.Matched);
    }

    [Fact]
    public void ClaudeSonnet5_DatedId_StillMatchesViaPrefix()
    {
        Assert.Equal("S", ModelBadgeTable.Resolve("claude-sonnet-5-20260101").Letter);
    }

    [Fact]
    public void ClaudeHaiku45Dated_MatchesHaikuFamily()
    {
        // Unlike ModelWindowTableTests's equivalent case (window size genuinely unverified for
        // this dated id), the badge only needs the model *family*, which "claude-haiku" always
        // resolves regardless of the dated suffix - so this one IS matched, on purpose.
        var badge = ModelBadgeTable.Resolve("claude-haiku-4-5-20251001");
        Assert.Equal("H", badge.Letter);
        Assert.True(badge.Matched);
    }

    // ---- Rendered ModelDisplayName fallback (live sessions carry "Sonnet 5" etc. in the same
    // column historical sessions carry the raw "claude-sonnet-5" id in - see
    // MonitorTreeBuilder.BuildSessionNode) ----

    [Fact]
    public void DisplayNameForm_StillResolvesToTheRightFamily()
    {
        Assert.Equal("S", ModelBadgeTable.Resolve("Sonnet 5").Letter);
        Assert.Equal("O", ModelBadgeTable.Resolve("Opus 4.5").Letter);
        Assert.Equal("H", ModelBadgeTable.Resolve("Haiku 4.5").Letter);
        Assert.Equal("F", ModelBadgeTable.Resolve("Fable 5").Letter);
        Assert.True(ModelBadgeTable.Resolve("Sonnet 5").Matched);
    }

    // ---- Colour-per-family assertions (colour is never the only signal - the letter always
    // differs too - but each family must still have a distinct, stable colour). ----

    [Fact]
    public void EachFamily_HasADistinctColor()
    {
        var opus = ModelBadgeTable.Resolve("claude-opus-4-8");
        var sonnet = ModelBadgeTable.Resolve("claude-sonnet-5");
        var haiku = ModelBadgeTable.Resolve("claude-haiku-4-5-20251001");
        var fable = ModelBadgeTable.Resolve("claude-fable-5");

        var colors = new[] { opus.ColorHex, sonnet.ColorHex, haiku.ColorHex, fable.ColorHex };
        Assert.Equal(colors.Length, new HashSet<string>(colors).Count);
    }

    [Fact]
    public void UnmatchedColor_IsNeutralGray_DistinctFromEveryFamily()
    {
        var opus = ModelBadgeTable.Resolve("claude-opus-4-8");
        var sonnet = ModelBadgeTable.Resolve("claude-sonnet-5");
        var haiku = ModelBadgeTable.Resolve("claude-haiku-4-5-20251001");
        var fable = ModelBadgeTable.Resolve("claude-fable-5");
        var unmatched = ModelBadgeTable.Resolve("totally-unknown-model-xyz");

        Assert.NotEqual(opus.ColorHex, unmatched.ColorHex);
        Assert.NotEqual(sonnet.ColorHex, unmatched.ColorHex);
        Assert.NotEqual(haiku.ColorHex, unmatched.ColorHex);
        Assert.NotEqual(fable.ColorHex, unmatched.ColorHex);
    }
}

using NovaTerminal.Rendering;
using SkiaSharp;

namespace NovaTerminal.Rendering.Tests;

/// <summary>
/// #172 item 3: colour-glyph detection was two codepoint ranges — <c>1F300-1FAFF</c> and
/// <c>2600-27BF</c> — which missed whole classes of emoji. Those fell into the Alpha8 atlas, where
/// glyphs are tinted with the cell's foreground colour, and so rendered as flat monochrome
/// silhouettes.
///
/// These tests assert the *routing decision* rather than pixels, which makes them independent of
/// which emoji fonts happen to be installed: the atlas a grapheme is sent to is a property of the
/// text, not of the font.
///
/// The negative cases matter as much as the positive ones. Over-routing to the colour atlas is not a
/// safe failure: colour glyphs are blitted as-is, so ordinary text sent there would ignore the
/// foreground colour and always paint as rasterized.
///
/// **Scope, established during review of #233.** This predicate only governs graphemes that actually
/// reach the atlas. `TerminalDrawOperation.ContainsRunesRequiringComplexShaping` diverts emoji in
/// `1F300-1FAFF`, `2600-27BF`, regional indicators, ZWJ sequences and (as of this change) VS16 and
/// keycap sequences to the shaper, bypassing the cache entirely. So the cases this predicate really
/// decides are the **single-rune, emoji-default** ones that fall through: the star, the squares, the
/// watch, the mahjong/joker tiles and the enclosed-ideographic set. The multi-codepoint entries below
/// are kept as defence in depth — the predicate should be correct on its own terms whether or not the
/// draw path happens to divert them today.
/// </summary>
public class ColorGlyphRoutingTests
{
    [Theory]
    // Regional-indicator flags: 1F1E6-1F1FF sits *below* the old lower bound of 1F300.
    [InlineData("\U0001F1EC\U0001F1E7", "flag (regional indicator pair)")]
    [InlineData("\U0001F1FA\U0001F1F8", "flag (regional indicator pair)")]
    [InlineData("\U0001F1E6", "lone regional indicator")]
    // Keycap sequence: ASCII digit + FE0F + 20E3. Not one codepoint of it is in either old range.
    [InlineData("1️⃣", "keycap digit")]
    [InlineData("#️⃣", "keycap hash")]
    // Singletons that fall between the two old ranges.
    [InlineData("⭐", "white medium star")]
    [InlineData("⭕", "heavy large circle")]
    [InlineData("⬛", "black large square")]
    // Emoji presentation forced by VS16 on a text-default base.
    [InlineData("❤️", "heavy black heart + VS16")]
    [InlineData("✈️", "airplane + VS16")]
    // Watch and hourglass: emoji-default while their block neighbours are text.
    [InlineData("⌚", "watch")]
    [InlineData("⌛", "hourglass")]
    // Mahjong red dragon and the black joker.
    [InlineData("\U0001F004", "mahjong tile red dragon")]
    [InlineData("\U0001F0CF", "playing card black joker")]
    // Enclosed Ideographic Supplement members that are Emoji_Presentation=Yes.
    [InlineData("\U0001F201", "squared katakana koko")]
    [InlineData("\U0001F21A", "squared CJK unified ideograph 7121")]
    [InlineData("\U0001F232", "squared CJK unified ideograph 7981")]
    [InlineData("\U0001F250", "circled ideograph advantage")]
    // Still covered: the range the old test got right.
    [InlineData("\U0001F600", "grinning face")]
    [InlineData("\U0001F44D", "thumbs up")]
    [InlineData("☕", "hot beverage")]
    public void EmojiGraphemes_RouteToTheColorAtlas(string text, string description)
    {
        Assert.True(
            GlyphCache.WantsColorGlyph(text),
            $"{description} ({Describe(text)}) should use the colour atlas; in Alpha8 it renders as a "
            + "monochrome silhouette tinted with the foreground colour.");
    }

    [Theory]
    // Plain text must stay on Alpha8 so it keeps foreground tinting.
    [InlineData("A", "latin letter")]
    [InlineData("z", "latin letter")]
    [InlineData("7", "digit")]
    [InlineData(" ", "space")]
    [InlineData("#", "hash without a keycap combiner")]
    [InlineData("1", "digit without a keycap combiner")]
    // Box drawing and block elements: the terminal draws these constantly and they must be tintable.
    [InlineData("─", "box drawing light horizontal")]
    [InlineData("│", "box drawing light vertical")]
    [InlineData("┌", "box drawing light down and right")]
    [InlineData("█", "full block")]
    [InlineData("░", "light shade")]
    // Powerline separators, ubiquitous in prompts.
    [InlineData("", "powerline right arrow")]
    [InlineData("", "powerline left arrow")]
    // CJK and accented Latin: text, however wide.
    [InlineData("中", "CJK ideograph")]
    [InlineData("あ", "hiragana")]
    [InlineData("é", "e + combining acute")]
    // Arrows and math operators are text-default; without VS16 they stay tintable.
    [InlineData("→", "rightwards arrow")]
    [InlineData("≠", "not equal to")]
    // Enclosed Ideographic Supplement members that are Emoji_Presentation=**No** - text by default,
    // and the reason 1F200-1F2FF is enumerated rather than matched as a block. A block-wide match
    // sent these to the colour atlas and cost them their foreground tint.
    [InlineData("\U0001F202", "squared katakana sa (text-default)")]
    [InlineData("\U0001F237", "squared month (text-default)")]
    // Unassigned / non-emoji codepoints inside the same block.
    [InlineData("\U0001F200", "square hiragana hoka (not emoji)")]
    [InlineData("\U0001F260", "unassigned in the enclosed ideographic block")]
    public void TextGraphemes_StayOnTheAlpha8Atlas(string text, string description)
    {
        Assert.False(
            GlyphCache.WantsColorGlyph(text),
            $"{description} ({Describe(text)}) must stay on Alpha8; on the colour atlas it would "
            + "ignore the cell's foreground colour and always paint as rasterized.");
    }

    [Fact]
    public void TheRoutingDecisionReachesTheAtlas()
    {
        // The predicate above is the unit under test, but it is only useful if GetOrAdd actually
        // honours it - so pin the end-to-end path for one case from each side.
        Assert.SkipUnless(SkiaAvailable(), "SkiaSharp native library not available on this platform.");

        using var cache = new GlyphCache();
        using var typeface = SKTypeface.Default;
        using var font = new SKFont(typeface, 16);

        var flag = cache.GetOrAdd("\U0001F1EC\U0001F1E7", font, 1.0f);
        var letter = cache.GetOrAdd("A", font, 1.0f);

        Assert.NotNull(flag);
        Assert.NotNull(letter);
        Assert.Equal(AtlasType.Color, flag!.Value.Type);
        Assert.Equal(AtlasType.Alpha8, letter!.Value.Type);
    }

    /// Codepoints of <paramref name="text"/> as U+ notation, so a failure message is readable when
    /// the grapheme itself is unprintable in a console.
    private static string Describe(string text)
    {
        var parts = new List<string>();
        foreach (var rune in text.EnumerateRunes())
        {
            parts.Add($"U+{rune.Value:X4}");
        }
        return string.Join(" ", parts);
    }

    private static bool SkiaAvailable()
    {
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(1, 1, SKColorType.Rgba8888));
            return surface != null;
        }
        catch
        {
            return false;
        }
    }
}

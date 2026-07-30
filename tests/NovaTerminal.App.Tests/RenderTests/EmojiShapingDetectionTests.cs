using NovaTerminal.Shell;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using System.Reflection;
using Xunit;

namespace NovaTerminal.Tests.RenderTests
{
    /// <summary>
    /// `ContainsRunesRequiringComplexShaping` decides whether a run goes to the text shaper or falls
    /// through to the per-glyph path. That matters for more than shaping quality: the fallback path
    /// enumerates **runes**, not grapheme clusters, and calls the glyph cache once per rune. So any
    /// multi-codepoint sequence that is not diverted here gets rasterized in pieces and its composed
    /// glyph is never produced.
    ///
    /// Flags were already diverted (via <c>IsRegionalIndicatorRune</c>). Keycaps were not, which is
    /// why they rendered wrong regardless of which atlas their pieces were routed to — found while
    /// reviewing #233 for #172.
    /// </summary>
    public class EmojiShapingDetectionTests
    {
        private static bool RequiresComplexShaping(string text)
        {
            MethodInfo? method = typeof(TerminalDrawOperation).GetMethod(
                "ContainsRunesRequiringComplexShaping",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            return (bool)(method!.Invoke(null, new object[] { text }) ?? false);
        }

        [Fact]
        public void RegionalIndicatorFlagSequence_RequiresComplexShaping()
        {
            Assert.True(RequiresComplexShaping("🇺🇸"));
        }

        [Theory]
        [InlineData("1️⃣", "keycap digit one")]
        [InlineData("#️⃣", "keycap number sign")]
        [InlineData("*️⃣", "keycap asterisk")]
        public void KeycapSequence_RequiresComplexShaping(string text, string description)
        {
            // Every codepoint of a keycap - ASCII base, FE0F, 20E3 - sits outside the emoji ranges
            // this method used to check, so the whole sequence fell through to the per-rune path and
            // was drawn as three unrelated glyphs.
            Assert.True(
                RequiresComplexShaping(text),
                $"{description} must reach the shaper as one unit; per-rune rendering draws the base, "
                + "the selector and the enclosing keycap separately and never composes the keycap.");
        }

        [Theory]
        // These two are already diverted by their base alone (2764 and 2708 both sit in 2600-27BF),
        // so they pass with or without the VS16 clause. Kept as documentation of intent.
        [InlineData("❤️", "heart + VS16 (base already in range)")]
        [InlineData("✈️", "airplane + VS16 (base already in range)")]
        // These are the cases the VS16 clause actually carries: the base is outside every range this
        // method checks, so before the clause existed the pair split into base + selector.
        [InlineData("↩️", "leftwards arrow with hook + VS16 (base U+21A9, out of range)")]
        [InlineData("™️", "trade mark sign + VS16 (base U+2122, out of range)")]
        [InlineData("ℹ️", "information source + VS16 (base U+2139, out of range)")]
        public void Vs16PromotedEmoji_RequiresComplexShaping(string text, string description)
        {
            // VS16 means "render the preceding character as emoji". The base is often a text-default
            // character outside every emoji range, so without this the pair splits.
            Assert.True(RequiresComplexShaping(text), description);
        }

        [Theory]
        [InlineData("plain ascii")]
        [InlineData("─│┌└")]          // box drawing
        [InlineData("█░▒▓")]          // block elements
        [InlineData("→ ≠ ± ∞")]       // text-default symbols
        [InlineData("中文 かな")]      // CJK / kana
        public void PlainAndTextDefaultRuns_DoNotRequireComplexShaping(string text)
        {
            // The shaper path flushes the sprite batch and bypasses the glyph atlas, so diverting
            // ordinary text into it would cost the atlas its purpose. Box drawing especially: it has
            // its own snapped-primitive path that must keep running.
            Assert.False(RequiresComplexShaping(text));
        }
    }
}

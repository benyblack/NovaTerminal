using System;
using Xunit;
using NovaTerminal.Core;
using System.Text;

namespace NovaTerminal.Tests
{
    public class WidthTests
    {
        [Fact]
        public void TestEmojiWidths()
        {
            var buffer = new TerminalBuffer(80, 24);

            // Thumbs Up (U+1F44D)
            string thumbsUp = "\U0001F44D";
            int w1 = buffer.GetGraphemeWidth(thumbsUp);
            Assert.Equal(2, w1);

            // Skin Tone (U+1F3FB) - should be 0 or handled inside sequence
            // But GetGraphemeWidth on just modifier might be undefined or 1?
            // Usually modifiers are width 0 if combining.

            // Sequence: Thumbs Up + Skin Tone
            string sequence = "\U0001F44D\U0001F3FB";
            int w2 = buffer.GetGraphemeWidth(sequence);
            Assert.Equal(2, w2);

            // Rocket (U+1F680)
            string rocket = "\U0001F680";
            int w3 = buffer.GetGraphemeWidth(rocket);
            Assert.Equal(2, w3);
        }

        [Fact]
        public void AmbiguousSymbols_AreSingleWidth_ByDefault()
        {
            var buffer = new TerminalBuffer(80, 24);

            // Music note used by TUIs/themes; should not shift the rest of the row.
            Assert.Equal(1, buffer.GetGraphemeWidth("\u266B"));

            // Dingbat pencil stays single-width unless emoji presentation is requested.
            Assert.Equal(1, buffer.GetGraphemeWidth("\u270F"));
        }

        [Fact]
        public void EmojiPresentationSelector_MakesAmbiguousSymbolWide()
        {
            var buffer = new TerminalBuffer(80, 24);

            // Red heart + VS16 emoji presentation should consume 2 cells.
            Assert.Equal(2, buffer.GetGraphemeWidth("\u2764\uFE0F"));
        }

        [Fact]
        public void RegionalIndicatorFlagSequence_IsTwoCells()
        {
            var buffer = new TerminalBuffer(80, 24);

            // US flag: should render as one emoji cluster with width 2, not two wide letters.
            Assert.Equal(2, buffer.GetGraphemeWidth("🇺🇸"));
        }
    }
}

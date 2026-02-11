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
    }
}

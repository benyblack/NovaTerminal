using Xunit;
using NovaTerminal.Core;
using System.Linq;
using System.Reflection;

namespace NovaTerminal.Tests
{
    public class GraphemeAttachmentTests
    {
        private void WriteToBuffer(TerminalBuffer buffer, string text)
        {
            foreach (char c in text) buffer.WriteChar(c);
        }

        [Fact]
        public void ThumbsUp_SkinTone_ShouldAttach_And_HaveWidth2()
        {
            var buffer = new TerminalBuffer(80, 24);

            // 1. Write Thumbs Up (U+1F44D)
            WriteToBuffer(buffer, "\U0001F44D");

            // Verify emoji is wide
            buffer.Lock.EnterReadLock();
            try
            {
                var cell1 = buffer.GetCellAbsolute(0, 0);
                string text = buffer.GetGraphemeAbsolute(0, 0);
                Assert.Equal("\U0001F44D", text);
                Assert.True(cell1.IsWide);
                Assert.Equal(2, buffer.GetGraphemeWidth(text));
            }
            finally { buffer.Lock.ExitReadLock(); }

            // 2. Write Skin Tone Modifier (U+1F3FB)
            WriteToBuffer(buffer, "\U0001F3FB");

            // Verify attachment
            buffer.Lock.EnterReadLock();
            try
            {
                var attachedCell = buffer.GetCellAbsolute(0, 0);
                string? finalContent = buffer.GetGraphemeAbsolute(0, 0);

                Assert.Equal("\U0001F44D\U0001F3FB", finalContent);

                // CRITICAL VERIFICATION: Width must be 2
                Assert.Equal(2, buffer.GetGraphemeWidth(finalContent!));
                Assert.True(attachedCell.IsWide);

                // Cursor should be at 2
                int actualCursorCol = buffer.CursorCol;
                Assert.Equal(2, actualCursorCol);
            }
            finally { buffer.Lock.ExitReadLock(); }
        }

        [Fact]
        public void Family_ZWJ_ShouldAttach_And_HaveWidth2()
        {
            var buffer = new TerminalBuffer(80, 24);

            // 👨‍👩‍👧 sequence: 
            // Man(1F468) ZWJ(200D) Woman(1F469) ZWJ(200D) Girl(1F467)
            string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";
            WriteToBuffer(buffer, family);

            buffer.Lock.EnterReadLock();
            try
            {
                var cell = buffer.GetCellAbsolute(0, 0);
                string text = buffer.GetGraphemeAbsolute(0, 0);
                Assert.Equal(family, text);

                // Width must be 2 for the whole cluster! (Man 2 + ZWJ 0 + Woman 2 + ZWJ 0 + Girl 2 -> unified 2)
                Assert.Equal(2, buffer.GetGraphemeWidth(text));
                Assert.True(cell.IsWide);
                int actualCursorCol = buffer.CursorCol;
                Assert.Equal(2, actualCursorCol);
            }
            finally { buffer.Lock.ExitReadLock(); }
        }

        [Fact]
        public void ThumbsUp_SkinTone_ShouldAttach_WithLookBack_WhenMarkerReset()
        {
            var buffer = new TerminalBuffer(80, 24);

            // 1. Write Thumbs Up
            WriteToBuffer(buffer, "\U0001F44D");
            Assert.Equal(2, buffer.CursorCol);

            // 2. Simulate a control code that resets _lastCharCol but NOT cursor position
            // \a (BEL) is a good choice as it usually does nothing but reset markers in some terminal code
            buffer.WriteChar('\a');

            // 3. Write Skin Tone Modifier
            WriteToBuffer(buffer, "\U0001F3FB");

            // Verify attachment via look-back at (CursorCol - 2)
            buffer.Lock.EnterReadLock();
            try
            {
                var attachedCell = buffer.GetCellAbsolute(0, 0);
                string text = buffer.GetGraphemeAbsolute(0, 0);
                Assert.Equal("\U0001F44D\U0001F3FB", text);
                Assert.Equal(2, buffer.GetGraphemeWidth(text));
                int actualCursorCol = buffer.CursorCol;
                Assert.Equal(2, actualCursorCol);
            }
            finally { buffer.Lock.ExitReadLock(); }
        }
    }
}

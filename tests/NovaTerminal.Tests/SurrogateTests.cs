using System;
using Xunit;
using NovaTerminal.Core;
using Avalonia.Media;

namespace NovaTerminal.Tests
{
    public class SurrogateTests
    {
        [Fact]
        public void TestGenericSurrogateReconstruction()
        {
            var buffer = new TerminalBuffer(80, 24);

            // Write 👍 as two chars
            // U+1F44D = D83D DC4D
            buffer.WriteChar('\uD83D'); // High
            buffer.WriteChar('\uDC4D'); // Low

            buffer.Lock.EnterReadLock();
            try
            {
                var cell0 = buffer.GetCell(0, 0);
                Assert.Equal("\U0001F44D", buffer.GetGrapheme(0, 0));
            }
            finally { buffer.Lock.ExitReadLock(); }
        }

        [Fact]
        public void TestSkinToneAttachment_ViaWriteChar()
        {
            var buffer = new TerminalBuffer(80, 24);

            // 1. Write Base Emoji (Thumbs Up) U+1F44D
            buffer.WriteChar('\uD83D');
            buffer.WriteChar('\uDC4D');

            // 2. Write Modifier (Medium Skin Tone) U+1F3FD
            // U+1F3FD = D83C DFFD
            buffer.WriteChar('\uD83C');
            buffer.WriteChar('\uDFFD');

            // Verify Attachment
            buffer.Lock.EnterReadLock();
            try
            {
                var attachedCell = buffer.GetCell(0, 0);
                string expectedCombined = "\U0001F44D\U0001F3FD";

                Assert.Equal(expectedCombined, buffer.GetGrapheme(0, 0));

                // Cell 1 should be continuation
                var cell1 = buffer.GetCell(1, 0);
                Assert.True(cell1.IsWideContinuation);

                // Cell 2 should be empty
                var cell2 = buffer.GetCell(2, 0);
                Assert.Equal(' ', cell2.Character);
                Assert.Equal(" ", buffer.GetGrapheme(2, 0));
            }
            finally { buffer.Lock.ExitReadLock(); }
        }
    }
}

using System;
using Xunit;
using NovaTerminal.Core;
using Avalonia.Media;

namespace NovaTerminal.Tests
{
    public class ReproductionTests
    {
        [Fact]
        public void TestSkinToneAttachment_Debug()
        {
            // Setup
            var buffer = new TerminalBuffer(80, 24);

            // 1. Write Base Emoji (Thumbs Up)
            string baseEmoji = "\U0001F44D"; // 👍
            buffer.WriteContent(baseEmoji);

            // Verify Base State
            buffer.Lock.EnterReadLock();
            try
            {
                var cell0 = buffer.GetCell(0, 0);
                var cell1 = buffer.GetCell(1, 0); // Should be wide continuation or empty depending on impl

                Assert.Equal(baseEmoji, buffer.GetGrapheme(0, 0));
                // Assert.True(cell0.IsWide, "Base emoji should be wide"); // Actually it might be 1 or 2 depending on font fallback, currently simulated as 2 in GetRuneWidth
            }
            finally { buffer.Lock.ExitReadLock(); }

            // 2. Write Modifier (Medium Skin Tone)
            string modifier = "\U0001F3FD"; // 🏽
            buffer.WriteContent(modifier);

            // Verify Attachment
            buffer.Lock.EnterReadLock();
            try
            {
                var attachedCell = buffer.GetCell(0, 0);
                string expectedCombined = baseEmoji + modifier;

                // This is the check that fails in the live app
                Assert.Equal(expectedCombined, buffer.GetGrapheme(0, 0));

                // Verify no ghost character at index 1 or 2
                // If attachment works, the cursor should be at 2 (since width is 2).
                // But cell[1] should be a continuation of cell[0].
                var continuation = buffer.GetCell(1, 0);
                Assert.True(continuation.IsWideContinuation, "Cell 1 should be a continuation");

                // Cell 2 should be empty
                var cell2 = buffer.GetCell(2, 0);
                Assert.Equal(' ', cell2.Character);
                Assert.Equal(" ", buffer.GetGrapheme(2, 0));
            }
            finally { buffer.Lock.ExitReadLock(); }
        }
    }
}

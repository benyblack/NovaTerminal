using System;
using Xunit;
using NovaTerminal.Core;

namespace NovaTerminal.Tests.BufferTests
{
    public class ReflowTests
    {
        [Fact]
        public void Write_SetsWrappedFlag_WhenLineWraps()
        {
            // Setup: 10 columns, 5 rows
            var buffer = new TerminalBuffer(10, 5);

            // Act: Write 15 characters "0123456789ABCDE"
            // Should fill row 0 with "0123456789" (Wrapped=True)
            // And row 1 with "ABCDE" (Wrapped=False)
            foreach (char c in "0123456789ABCDE")
            {
                buffer.WriteChar(c);
            }

            // Assert
            // Row 0 should be wrapped because we wrote past column 10
            Assert.True(buffer.ViewportRows[0].IsWrapped, "Row 0 should have IsWrapped=true");

            // Row 1 should NOT be wrapped (stopped at column 5)
            Assert.False(buffer.ViewportRows[1].IsWrapped, "Row 1 should have IsWrapped=false");
        }


        [Fact]
        public void Resize_UnwrapsLine_WhenExpanded()
        {
            // Setup: 10 columns
            var buffer = new TerminalBuffer(10, 5);
            // Write "0123456789ABCDE" -> Wraps at index 10
            foreach (char c in "0123456789ABCDE") buffer.WriteChar(c);

            Assert.True(buffer.ViewportRows[0].IsWrapped);

            // Act: Resize to 20 columns
            buffer.Resize(20, 5);

            // Assert
            // Should now be one line: "0123456789ABCDE"
            // Row 0 should NOT be wrapped (it fits)
            // Row 1 should be empty

            string line0 = GetRowText(buffer.ViewportRows[0]);
            Assert.StartsWith("0123456789ABCDE", line0);
            Assert.False(buffer.ViewportRows[0].IsWrapped, "Row should unwrap when resized larger");

            // Check Row 1 is empty/default
            string line1 = GetRowText(buffer.ViewportRows[1]);
            Assert.True(string.IsNullOrWhiteSpace(line1.Trim()), "Row 1 should be empty after reflow");
        }

        [Fact]
        public void Resize_WrapsLine_WhenShrunk()
        {
            // Setup: 20 columns
            var buffer = new TerminalBuffer(20, 5);
            // Write "0123456789ABCDE" -> No wrap (length 15 < 20)
            foreach (char c in "0123456789ABCDE") buffer.WriteChar(c);

            Assert.False(buffer.ViewportRows[0].IsWrapped);

            // Act: Resize to 10 columns
            buffer.Resize(10, 5);

            // Assert
            // Should now be two lines: "0123456789" (Wrapped) and "ABCDE" (Not Wrapped)

            string line0 = GetRowText(buffer.ViewportRows[0]);
            Assert.StartsWith("0123456789", line0);
            Assert.True(buffer.ViewportRows[0].IsWrapped, "Row 0 should wrap when resized smaller");

            string line1 = GetRowText(buffer.ViewportRows[1]);
            Assert.StartsWith("ABCDE", line1); // Spaces after E
            Assert.False(buffer.ViewportRows[1].IsWrapped, "Row 1 should be end of line");
        }

        [Fact]
        public void Resize_PreservesHardLineBreaks()
        {
            // Setup: 10 columns
            var buffer = new TerminalBuffer(10, 5);

            // Write "Line1\nLine2"
            foreach (char c in "Line1\n") buffer.WriteChar(c);
            foreach (char c in "Line2") buffer.WriteChar(c);

            Assert.False(buffer.ViewportRows[0].IsWrapped, "Row 0 should NOT be wrapped (explicit newline)");
            Assert.False(buffer.ViewportRows[1].IsWrapped);

            // Act: Resize to 20 columns (wide enough to hold both on one line if we were buggy)
            buffer.Resize(20, 5);

            // Assert
            // Should STILL be two lines.

            string line0 = GetRowText(buffer.ViewportRows[0]);
            Assert.StartsWith("Line1", line0);
            Assert.False(buffer.ViewportRows[0].IsWrapped);

            string line1 = GetRowText(buffer.ViewportRows[1]);
            Assert.StartsWith("Line2", line1);
        }

        private string GetRowText(TerminalRow row)
        {
            char[] chars = new char[row.Cells.Length];
            for (int i = 0; i < row.Cells.Length; i++) chars[i] = row.Cells[i].Character;
            return new string(chars);
        }
    }
}

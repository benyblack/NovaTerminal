using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using NovaTerminal.Core;
using Xunit.Abstractions;

namespace NovaTerminal.Tests
{
    public class ReflowRegressionTests
    {
        private readonly ITestOutputHelper _output;

        public ReflowRegressionTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // Helper to check standard prompt preservation
        private bool RowContainsText(TerminalBuffer buffer, int rowIndex, string text)
        {
            var row = GetRow(buffer, rowIndex);
            var rowText = GetRowText(row);
            return rowText.Contains(text);
        }

        private TerminalRow GetRow(TerminalBuffer buffer, int rowIndex)
        {
            // Handle extended indices (Virtual interface simulation)
            // Just use the internal method logic via reflection or simple mapping
            if (rowIndex < 0) return null;

            // Map to Scrollback + Viewport
            // We need a helper that exposes the *whole* simulated linear buffer
            // But for simple tests, we can peek private methods or just use what we have.
            // Let's rely on TerminalBuffer's GetCellAbsolute or similar? No, that's complex.
            // Let's use the Reflection helpers we established in ReflowScenariosTests
            // Copied locally for isolation
            return InternalGetRow(buffer, rowIndex);
        }

        // --- Helper Reflection Methods (Copied/Adapted) ---
        private TerminalRow InternalGetRow(TerminalBuffer buffer, int absoluteIndex)
        {
            var sb = GetScrollback(buffer);
            var vp = GetViewport(buffer);
            int sbCount = sb.Count;

            if (absoluteIndex < sbCount) return sb[absoluteIndex];
            int vpIndex = absoluteIndex - sbCount;
            if (vpIndex >= 0 && vpIndex < vp.Length) return vp[vpIndex];
            return null;
        }

        private string GetRowText(TerminalRow row)
        {
            if (row == null || row.Cells == null) return "";
            var chars = row.Cells.Select(c => c.Character == '\0' ? ' ' : c.Character).ToArray();
            return new string(chars).TrimEnd();
        }

        private NovaTerminal.Core.CircularBuffer<TerminalRow> GetScrollback(TerminalBuffer buffer)
        {
            var field = typeof(TerminalBuffer).GetField("_scrollback", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (NovaTerminal.Core.CircularBuffer<TerminalRow>)field!.GetValue(buffer)!;
        }

        private TerminalRow[] GetViewport(TerminalBuffer buffer)
        {
            var field = typeof(TerminalBuffer).GetField("_viewport", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (TerminalRow[])field!.GetValue(buffer)!;
        }

        // ====================================================================
        // Scenario 1: PowerShell Horizontal Resize - Prompt Disappearance
        // ====================================================================
        [Fact]
        public void PowerShell_HorizontalShrink_ShouldPreservePrompt()
        {
            // Setup: Standard terminal size
            var buffer = new TerminalBuffer(80, 24);

            // Write some history
            buffer.Write("User History 1\n");
            buffer.Write("User History 2\n");

            // Write "PowerShell" style prompt (no hard newline at end usually)
            string prompt = "PS C:\\Users\\Dev> ";
            buffer.Write(prompt); // Cursor is now at end of line 2 (index 2)

            int initialCursorRow = buffer.CursorRow;
            int initialCursorCol = buffer.CursorCol; // Should be prompt.Length

            Assert.Equal(2, initialCursorRow);
            Assert.Equal(prompt.Length, initialCursorCol);

            // Act: Shrink Width Horizontal (80 -> 60)
            // New behavior: cursor row is PRESERVED (PowerShell doesn't redraw on horizontal resize)
            buffer.Resize(60, 24);

            // Assert: Prompt should be preserved (not cleared)
            var row = GetRow(buffer, buffer.ScrollbackRows.Count + buffer.CursorRow);
            string text = GetRowText(row);

            _output.WriteLine($"Cursor Row Text after resize: '{text}'");

            // The prompt MUST be preserved for PowerShell
            Assert.Contains("PS C:\\Users\\Dev>", text);
            Assert.NotEqual("", text.Trim());
        }

        // ====================================================================
        // Scenario 2: PowerShell Vertical Shrink - Cursor Jump to 0
        // ====================================================================
        [Fact]
        public void PowerShell_VerticalShrink_CursorAtBottom_ShouldPreserveCursorX()
        {
            // Setup: Cursor at the bottom of the screen
            int rows = 24;
            var buffer = new TerminalBuffer(80, rows);

            // Fill buffer to push cursor to bottom
            for (int i = 0; i < rows - 1; i++) buffer.Write($"Line {i}\n");

            string prompt = "PS Bottom> ";
            buffer.Write(prompt);

            // Verify we are at the bottom
            Assert.Equal(rows - 1, buffer.CursorRow);
            Assert.Equal(prompt.Length, buffer.CursorCol);
            int expectedCol = buffer.CursorCol;

            // Act: Vertical Shrink (24 -> 20)
            // This forces lines into scrollback.
            buffer.Resize(80, 20);

            // Assert
            // 1. Cursor Row should be at new bottom (19)
            Assert.Equal(19, buffer.CursorRow);

            // 2. Cursor Col should still be at the end of the prompt
            // Issue reported: Sends cursor to 0.
            Assert.Equal(expectedCol, buffer.CursorCol);

            // 3. Prompt text should be at the cursor row
            var row = GetRow(buffer, buffer.ScrollbackRows.Count + buffer.CursorRow);
            Assert.Contains("PS Bottom>", GetRowText(row));
        }

        // ====================================================================
        // Scenario 3: WSL Horizontal Duplication (Width < Prompt Width)
        // ====================================================================
        [Fact]
        public void WSL_ShrinkWidth_SmallerThanPrompt_ShouldNotDuplicate()
        {
            // Setup
            var buffer = new TerminalBuffer(80, 24);
            buffer.Write("History\n");

            // Long Prompt
            string longPrompt = "user@very-long-hostname-machine:/var/www/html/project/deep/nested/directory$ ";
            // length approx 75 chars
            buffer.Write(longPrompt);

            // Act: Shrink to 40 (Smaller than prompt)
            // This forces wrapping of the prompt line.
            // A naive "Active Line" detection might fail if it spans multiple physical lines.
            buffer.Resize(40, 24);

            // Assert
            // We should NOT see the prompt pushed into history AND kept in viewport.
            // The logic should treat the *entire* wrapped prompt as the "Active Logical Line"
            // and keep it in the viewport (or as the active interaction point).

            // Check History for duplication
            // If duplicated, history will contain the prompt text.
            var sb = GetScrollback(buffer);
            if (sb.Count > 0)
            {
                var lastHistory = GetRowText(sb.Last());
                // The history might validly contain the specific *previous* command output ("History")
                // but should NOT contain the Active Prompt we just reflowed.
                Assert.DoesNotContain("long-hostname", lastHistory);
            }
        }

        // ====================================================================
        // Scenario 4: WSL Vertical Duplication (Prompt at Bottom)
        // ====================================================================
        [Fact]
        public void WSL_VerticalResize_PromptAtBottom_ShouldNotDuplicate()
        {
            // This test checks if we accidentally push the "Active Prompt" into history
            // during a vertical resize, which would cause the shell to effectively "duplicate" it
            // when it redraws the cursor line.

            // Ideally, the "Active Logical Line" (where cursor is) should stay in the Viewport
            // as much as possible, or if it moves to scrollback, the cursor should follow it exactly?
            // Actually, usually on vertical resize, if we just grow/shrink height, 
            // the *relative* view changes.

            // If we shrink height, top lines go to scrollback.
            // Bottom line (cursor) stays at bottom (n-1).
            // It should NOT go to scrollback.

            var buffer = new TerminalBuffer(80, 24);
            for (int i = 0; i < 23; i++) buffer.Write($"Line {i}\n");
            buffer.Write("ActivePrompt$ ");

            // Act: Shrink Height 24 -> 20
            buffer.Resize(80, 20);

            // Assert
            // The "ActivePrompt$ " should be at the bottom of the viewport.
            // It should NOT be in the Scrollback.

            // Check Viewport Bottom
            var vp = GetViewport(buffer);
            var bottomRow = vp[buffer.CursorRow]; // Should be last row (19)
            Assert.Contains("ActivePrompt", GetRowText(bottomRow));

            // Check Scrollback Last
            var sb = GetScrollback(buffer);
            var lastSb = sb.Last(); // Should be "Line X"
            Assert.DoesNotContain("ActivePrompt", GetRowText(lastSb));
        }
    }
}

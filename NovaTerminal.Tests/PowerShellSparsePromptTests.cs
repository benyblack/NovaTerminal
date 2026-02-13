using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using NovaTerminal.Core;
using Xunit.Abstractions;

namespace NovaTerminal.Tests
{
    /// <summary>
    /// Tests for PowerShell prompts that use cursor positioning (sparse rows)
    /// PowerShell sometimes writes prompts in parts using cursor positioning,
    /// creating sparse rows with gaps between left and right content.
    /// </summary>
    public class PowerShellSparsePromptTests
    {
        private readonly ITestOutputHelper _output;

        public PowerShellSparsePromptTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private TerminalRow[] GetViewport(TerminalBuffer buffer)
        {
            var field = typeof(TerminalBuffer).GetField("_viewport", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (TerminalRow[])field!.GetValue(buffer)!;
        }

        private string GetRowText(TerminalRow row)
        {
            if (row == null || row.Cells == null) return "";
            var chars = row.Cells.Select(c => c.Character == '\0' ? ' ' : c.Character).ToArray();
            return new string(chars);
        }

        [Fact]
        public void PowerShell_SparsePrompt_HorizontalShrink_ShouldPreserveRightSide()
        {
            // Simulate PowerShell writing prompt with cursor positioning
            // This creates a SPARSE row (not wrapped)
            
            var buffer = new TerminalBuffer(80, 24);
            
            // Write left part
            buffer.Write("PS ");
            
            // Simulate cursor positioning to column 20 (PowerShell might do this)
            int currentRow = buffer.CursorRow;
            var viewport = GetViewport(buffer);
            var row = viewport[currentRow];
            
            // Manually place right side content at column 20
            string rightPart = "C:\\Users\\Dev>";
            int rightStartCol = 20;
            for (int i = 0; i < rightPart.Length && (rightStartCol + i) < 80; i++)
            {
                row.Cells[rightStartCol + i] = new TerminalCell(
                    rightPart[i],
                    row.Cells[rightStartCol + i].Foreground,
                    row.Cells[rightStartCol + i].Background,
                    false, false, false, false
                );
            }
            
            // Move cursor to end of right part
            buffer.CursorCol = rightStartCol + rightPart.Length + 1;
            
            _output.WriteLine("=== BEFORE SHRINK (80 cols) ===");
            _output.WriteLine($"Row 0: '{GetRowText(viewport[0])}'");
            _output.WriteLine($"IsWrapped: {viewport[0].IsWrapped}");
            
            // Shrink to 15 columns
            buffer.Resize(15, 24);
            
            viewport = GetViewport(buffer);
            _output.WriteLine("\n=== AFTER SHRINK (15 cols) ===");
            for (int i = 0; i < 5; i++)
            {
                string text = GetRowText(viewport[i]).TrimEnd();
                if (text.Length > 0 || i <= buffer.CursorRow + 1)
                {
                    _output.WriteLine($"Row {i}: '{text}' [IsWrapped={viewport[i].IsWrapped}]");
                }
            }
            
            // Collect all content
            string fullContent = "";
            for (int i = 0; i <= buffer.CursorRow + 1 && i < viewport.Length; i++)
            {
                fullContent += GetRowText(viewport[i]).TrimEnd();
            }
            
            _output.WriteLine($"\nFull content: '{fullContent}'");
            
            // Assert: Both left and right parts should be present
            Assert.Contains("PS", fullContent);
            Assert.Contains("Dev>", fullContent);  // Right side must be preserved!
        }
    }
}

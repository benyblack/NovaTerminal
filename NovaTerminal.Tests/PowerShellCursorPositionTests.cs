using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using NovaTerminal.Core;
using Xunit.Abstractions;

namespace NovaTerminal.Tests
{
    /// <summary>
    /// Tests cursor position tracking during horizontal resize with sparse prompts
    /// </summary>
    public class PowerShellCursorPositionTests
    {
        private readonly ITestOutputHelper _output;

        public PowerShellCursorPositionTests(ITestOutputHelper output)
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

        private void DumpBuffer(TerminalBuffer buffer, string label)
        {
            _output.WriteLine($"\n=== {label} ===");
            _output.WriteLine($"Cursor: Row={buffer.CursorRow}, Col={buffer.CursorCol}");
            var viewport = GetViewport(buffer);
            for (int i = 0; i <= Math.Min(buffer.CursorRow + 2, viewport.Length - 1); i++)
            {
                string text = GetRowText(viewport[i]);
                _output.WriteLine($"  Row {i}: '{text.TrimEnd()}' [Wrapped={viewport[i].IsWrapped}]");
            }
        }

        [Fact]
        public void PowerShell_OhMyPosh_HorizontalShrink_CursorPosition()
        {
            // Simulate oh-my-posh style prompt:
            // Left part: "PS ~/project " at column 0
            // Right part: "16:30:45" at column 70 (right-aligned)
            // Cursor after prompt at column ~13

            var buffer = new TerminalBuffer(80, 24);

            // Write left part
            string leftPart = "PS ~/project ";
            buffer.Write(leftPart);

            int initialCursorCol = buffer.CursorCol;
            _output.WriteLine($"After writing left part, cursor at col {initialCursorCol}");

            // Manually place right-aligned part (oh-my-posh does this via cursor positioning)
            var viewport = GetViewport(buffer);
            var row = viewport[0];
            string rightPart = "16:30:45";
            int rightStartCol = 72; // Right-aligned at column 72

            for (int i = 0; i < rightPart.Length && (rightStartCol + i) < 80; i++)
            {
                row.Cells[rightStartCol + i] = new TerminalCell(
                    rightPart[i],
                    row.Cells[rightStartCol + i].Foreground,
                    row.Cells[rightStartCol + i].Background,
                    false, false, true, true
                );
            }

            // Cursor should be at end of left part
            DumpBuffer(buffer, "BEFORE RESIZE (80 cols)");

            int expectedCursorCol = leftPart.Length;
            Assert.Equal(0, buffer.CursorRow);
            Assert.Equal(expectedCursorCol, buffer.CursorCol);

            // Now shrink horizontally (80 -> 50)
            // Expected behavior:
            // - Left part stays on row 0
            // - Right part wraps to row 1 (or is lost - that's the bug)
            // - Cursor should stay at column 13 on row 0
            buffer.Resize(50, 24);

            DumpBuffer(buffer, "AFTER RESIZE (50 cols)");

            // Check cursor position
            _output.WriteLine($"\nExpected cursor: Row=0, Col={expectedCursorCol}");
            _output.WriteLine($"Actual cursor: Row={buffer.CursorRow}, Col={buffer.CursorCol}");

            // The cursor should still be at the end of the left part
            Assert.Equal(0, buffer.CursorRow);
            Assert.Equal(expectedCursorCol, buffer.CursorCol);

            // Check content preservation
            viewport = GetViewport(buffer);
            string row0Text = GetRowText(viewport[0]);

            _output.WriteLine($"Row 0 content: '{row0Text.TrimEnd()}'");

            // Left part must be preserved
            Assert.Contains("PS ~/project", row0Text);

            // Right part should be preserved somewhere (ideally wrapped to next row)
            // This is currently the bug - it gets lost
            bool foundRightPart = false;
            for (int i = 0; i < Math.Min(5, viewport.Length); i++)
            {
                if (GetRowText(viewport[i]).Contains("16:30:45"))
                {
                    foundRightPart = true;
                    _output.WriteLine($"Found right part on row {i}");
                    break;
                }
            }

            Assert.True(foundRightPart, "Right-aligned part of prompt should be preserved after horizontal shrink!");
        }

        [Fact]
        public void PowerShell_SimpleSparsePrompt_CursorTracking()
        {
            // Simpler test: Just verify cursor column is tracked correctly through sparse content
            var buffer = new TerminalBuffer(80, 24);

            // Write "PS " then manually place content at column 50
            buffer.Write("PS ");

            var viewport = GetViewport(buffer);
            var row = viewport[0];

            // Place "far" at column 50
            row.Cells[50] = new TerminalCell('f', row.Cells[50].Foreground, row.Cells[50].Background, false, false, true, true);
            row.Cells[51] = new TerminalCell('a', row.Cells[51].Foreground, row.Cells[51].Background, false, false, true, true);
            row.Cells[52] = new TerminalCell('r', row.Cells[52].Foreground, row.Cells[52].Background, false, false, true, true);

            int cursorBefore = buffer.CursorCol; // Should be 3
            _output.WriteLine($"Cursor before resize: {cursorBefore}");
            _output.WriteLine($"Row content: '{GetRowText(row)}'");

            // Shrink to 40 columns
            buffer.Resize(40, 24);

            viewport = GetViewport(buffer);
            _output.WriteLine($"\nAfter resize to 40:");
            _output.WriteLine($"Cursor: Row={buffer.CursorRow}, Col={buffer.CursorCol}");
            for (int i = 0; i < 3; i++)
            {
                _output.WriteLine($"Row {i}: '{GetRowText(viewport[i]).TrimEnd()}' [Wrapped={viewport[i].IsWrapped}]");
            }

            // Cursor should still be at column 3 (or adjusted if wrapped)
            // The key is that it should be AFTER "PS " in logical terms

            // Reconstruct full content from ALL viewport rows (not just up to cursor)
            string fullContent = "";
            for (int i = 0; i < Math.Min(5, viewport.Length); i++)
            {
                string rowText = GetRowText(viewport[i]).TrimEnd();
                if (rowText.Length > 0)
                {
                    fullContent += rowText + " ";
                }
            }

            _output.WriteLine($"Full content from all rows: '{fullContent.Trim()}'");

            // The full content should contain all parts
            Assert.Contains("PS", fullContent);
            Assert.Contains("far", fullContent);
        }
    }
}

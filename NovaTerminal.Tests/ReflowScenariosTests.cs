using Xunit;
using NovaTerminal.Core;
using System.Text;
using Xunit.Abstractions;
using System.Collections.Generic;
using System;

namespace NovaTerminal.Tests
{
    public class ReflowScenariosTests
    {
        private readonly ITestOutputHelper _output;

        public ReflowScenariosTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private string GetRowText(TerminalBuffer buffer, int physIdx)
        {
            var sbCount = GetScrollbackCount(buffer);
            TerminalRow row;
            if (physIdx < sbCount)
            {
                row = GetScrollbackRow(buffer, physIdx);
            }
            else
            {
                row = GetViewportRow(buffer, physIdx - sbCount);
            }

            StringBuilder sb = new StringBuilder();
            foreach (var cell in row.Cells)
            {
                sb.Append(cell.Character == '\0' ? ' ' : cell.Character);
            }
            return sb.ToString();
        }

        private void WriteLines(TerminalBuffer buffer, int count)
        {
            for (int i = 0; i < count; i++)
            {
                buffer.Write($"Line {i}\n");
            }
        }

        private void DumpBuffer(TerminalBuffer buffer)
        {
            int sbCount = GetScrollbackCount(buffer);
            _output.WriteLine($"Buffer Dump: SB={sbCount} VP={buffer.Rows} Cursor={buffer.CursorRow},{buffer.CursorCol}");
            for (int i = 0; i < sbCount + buffer.Rows; i++)
            {
                string status = i < sbCount ? "[SB]" : "[VP]";
                _output.WriteLine($"{status} L{i:D2}: |{GetRowText(buffer, i)}|");
            }
        }

        private int GetScrollbackCount(TerminalBuffer buffer)
        {
            var field = typeof(TerminalBuffer).GetField("_scrollback", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var list = (System.Collections.IList)field!.GetValue(buffer)!;
            return list.Count;
        }

        private TerminalRow GetScrollbackRow(TerminalBuffer buffer, int idx)
        {
            var field = typeof(TerminalBuffer).GetField("_scrollback", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var list = (System.Collections.IList)field!.GetValue(buffer)!;
            return (TerminalRow)list[idx]!;
        }

        private TerminalRow GetViewportRow(TerminalBuffer buffer, int idx)
        {
            var field = typeof(TerminalBuffer).GetField("_viewport", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var array = (TerminalRow[])field!.GetValue(buffer)!;
            return array[idx];
        }

        [Fact]
        public void VerticalShrink_PromptShouldStayAtBottom_AndHistoryPreserved()
        {
            var buffer = new TerminalBuffer(20, 10);
            for (int i = 1; i <= 15; i++)
            {
                buffer.Write($"Line {i:D2}\n");
            }

            _output.WriteLine("Before Resize:");
            DumpBuffer(buffer);

            buffer.Resize(20, 5);

            _output.WriteLine("After Resize (20x5):");
            DumpBuffer(buffer);

            Assert.Equal(4, buffer.CursorRow);

            // With Bottom-Pinning, Line 15 should be in the last content row.
            // Which is row 3 of viewport (total 5 rows: row 3 is line 15, row 4 is prompt/placeholder).
            string row3 = GetRowText(buffer, 3 + GetScrollbackCount(buffer));
            Assert.Contains("15", row3);
        }

        [Fact]
        public void HorizontalShrink_WordWrap_PreserveHistory()
        {
            var buffer = new TerminalBuffer(20, 10);

            buffer.Write("This is a long line that will wrap when resized.\n");
            buffer.Write("Line 2\n");
            buffer.Write("PROMPT");

            _output.WriteLine("Before Resize:");
            DumpBuffer(buffer);

            buffer.Resize(10, 10);

            _output.WriteLine("After Resize (10x10):");
            DumpBuffer(buffer);

            // Find where Line 2 ended up
            bool foundLine2 = false;
            int totalPhys = GetScrollbackCount(buffer) + buffer.Rows;
            for (int i = 0; i < totalPhys; i++)
            {
                if (GetRowText(buffer, i).Contains("Line 2")) { foundLine2 = true; break; }
            }
            Assert.True(foundLine2, "Line 2 should be preserved");
        }
        [Fact]
        public void VerticalGrow_ShouldNotPullHistory_AndAddPadding()
        {
            // Initial 80x10, write 30 lines (20 in SB, 10 in VP)
            var buffer = new TerminalBuffer(80, 10);
            for (int i = 1; i <= 30; i++)
            {
                buffer.Write($"History Line {i:D3}\n");
            }
            buffer.Write("PROMPT");

            _output.WriteLine("Before Grow (80x10):");
            DumpBuffer(buffer);

            int initialSbCount = GetScrollbackCount(buffer);

            // Grow to 80x20. 
            // Standard ConPTY behavior: Grow adds blank space at bottom. History stays pushed up.
            // So SB count should unchanged.
            buffer.Resize(80, 20);

            _output.WriteLine("After Grow (80x20):");
            DumpBuffer(buffer);

            int finalSbCount = GetScrollbackCount(buffer);
            Assert.Equal(initialSbCount, finalSbCount);

            // VP[0] should STILL be Line 22 (first line of old viewport)
            // Because we Anchor-to-Top.
            string row0_vp = GetRowText(buffer, finalSbCount);
            Assert.Contains("022", row0_vp);

            // VP[10] should be "PROMPT" (it was at index 10 of old logical content approx)
            // Wait, we had 30 lines. 1-20 in SB. 21-30 in VP.
            // Prompt at bottom? "PROMPT" written after loop.
            // 31 lines total.
            // SB: 1-21? 
            // VP: 22-30 + Prompt?
            // Let's verify strictly.
            // With 80x10.
            // Line 1..20 -> SB. (20 lines)
            // Line 21..30 -> VP (10 lines).
            // Prompt -> VP (11th line).
            // So SB should have 21 lines (1..21). VP has 10 lines (22..30 + Prompt). 
            // Total 31? 30 write calls + Prompt.
            // Loop 1..30.
            // After loop: 30 lines.
            // Write Prompt.
            // If Prompt writes without newline, it appends to line 30? No, previous had \n.
            // So 31 lines total.
            // 80x10 -> Shows last 10. (Lines 22..31).
            // SB has 21 lines (1..21).

            // New Logic: 
            // SB stays 21 lines.
            // VP has Lines 22..31 (10 lines).
            // VP has 10 blank lines padding.

            // PROMPT was at index 9 of old viewport (last line).
            // So it should be at index 9 of new viewport too.
            string promptRow = GetRowText(buffer, finalSbCount + 9);
            Assert.Contains("PROMPT", promptRow);

            // VP[10]..[19] should be padding.
            Assert.Equal("", GetRowText(buffer, finalSbCount + 10).Trim());
        }

        [Fact]
        public void MatrixResize_LargeData_ComplexFlow()
        {
            // Start 80x24 (Standard). Write 100 lines.
            var buffer = new TerminalBuffer(80, 24);
            for (int i = 1; i <= 100; i++)
            {
                buffer.Write($"Line {i:D3} - " + new string('X', 60) + "\n");
            }
            buffer.Write("PROMPT> ");

            // 1. Shrink Width (80 -> 40). Height stays 24.
            // This will cause almost every line to wrap. 
            // Logical lines stay 101. Physical lines should double.
            _output.WriteLine("Shrink Width (40x24):");
            buffer.Resize(40, 24);
            // DumpBuffer(buffer); // Too large for logs usually but helpful if it fails

            // 2. Shrink Height (40x24 -> 40x10). 
            // Most content moves to SB.
            _output.WriteLine("Shrink Height (40x10):");
            buffer.Resize(40, 10);

            // 3. Grow Both (40x10 -> 100x40). 
            // Content should unwrap and reveal more history.
            _output.WriteLine("Grow Both (100x40):");
            buffer.Resize(100, 40);
            DumpBuffer(buffer);

            // Verification: Ensure Line 001 is still there.
            string firstRow = GetRowText(buffer, 0);
            Assert.Contains("001", firstRow);

            // Ensure Line 100 is there
            bool found100 = false;
            int totalPhys = GetScrollbackCount(buffer) + buffer.Rows;
            for (int i = 0; i < totalPhys; i++)
            {
                if (GetRowText(buffer, i).Contains("100")) { found100 = true; break; }
            }
            Assert.True(found100, "Line 100 should be preserved after complex resize");
        }

        [Fact]
        public void ShrinkWidth_ThenGrowWidth_PromptIntegrity()
        {
            var buffer = new TerminalBuffer(80, 10);
            WriteLines(buffer, 5); // Lines 0-4
            buffer.Write("Prompt: ");
            // 1. Shrink Width (80 -> 20) - Should wrap and TRIGGER WIPE
            buffer.Resize(20, 10);
            _output.WriteLine("After Shrink (20x10):");
            DumpBuffer(buffer);

            // After RESIZE, cursor row is PRESERVED (for PowerShell compatibility).
            string rowText = GetRowText(buffer, buffer.CursorRow + GetScrollbackCount(buffer));
            Assert.Contains("Prompt:", rowText);

            // 2. Grow back (20 -> 80)
            buffer.Resize(80, 10);
            _output.WriteLine("After Grow Back (80x10):");
            DumpBuffer(buffer);

            // Again, preserved.
            rowText = GetRowText(buffer, buffer.CursorRow + GetScrollbackCount(buffer));
            Assert.Contains("Prompt:", rowText);

            // Verify preceding history integrity (Line 4 should be preserved)
            // It should be one row above the cursor row.
            int absolutePromptRow = buffer.CursorRow + GetScrollbackCount(buffer);
            Assert.Contains("Line 4", GetRowText(buffer, absolutePromptRow - 1));
        }

        [Fact]
        public void GrowHeight_ShouldRevealHistoryCorrectly()
        {
            var buffer = new TerminalBuffer(80, 5);
            WriteLines(buffer, 20); // 20 lines, viewport is 5. 15 in sb, 5 in vp.
            buffer.Write("Active Prompt");

            // 1. Grow Height (5 -> 15)
            buffer.Resize(80, 15);
            _output.WriteLine("After Grow (80x15):");
            DumpBuffer(buffer);

            // Anchor-to-Top: SB count stays the same (16).
            // Viewport adds 10 rows of padding at the bottom.
            Assert.Equal(16, GetScrollbackCount(buffer));

            // Prompt stays at its original viewport relative position (row 5 of logical, so row 4 of old viewport?)
            // row 4 is index 20 (abs).
            Assert.Contains("Active Prompt", GetRowText(buffer, 20));
            Assert.Contains("Line 19", GetRowText(buffer, 19));
        }

        [Fact]
        public void MatrixResize_FullIntegrityCheck_1000Lines()
        {
            // Existing test preserved...
            // Start 80x24. Write 1000 lines.
            var buffer = new TerminalBuffer(80, 24);
            var expectedLines = new List<string>();
            for (int i = 1; i <= 1000; i++)
            {
                string text = $"[LINE-{i:D4}] " + new string((char)('A' + (i % 26)), 50);
                buffer.Write(text + "\n");
                expectedLines.Add(text);
            }
            buffer.Write("PROMPT>");

            // Random sequence of extreme resizes
            var sizes = new (int, int)[] {
                (40, 10),  // Deep shrink
                (120, 50), // Large grow
                (20, 100), // Narrow vertical
                (200, 5),  // Wide horizontal shallow
                (80, 24)   // Back to standard
            };

            foreach (var size in sizes)
            {
                buffer.Resize(size.Item1, size.Item2);
            }

            // Verify all 1000 lines are present in order
            int sbCount = GetScrollbackCount(buffer);
            int totalPhys = sbCount + buffer.Rows;

            _output.WriteLine($"Final Buffer Size: SB={sbCount} VP={buffer.Rows}. Verifying 1000 lines...");

            int expectedIdx = 0;
            StringBuilder currentLogical = new StringBuilder();

            for (int i = 0; i < totalPhys; i++)
            {
                var row = (i < sbCount) ? GetScrollbackRow(buffer, i) : GetViewportRow(buffer, i - sbCount);

                StringBuilder rowText = new StringBuilder();
                foreach (var c in row.Cells) rowText.Append(c.Character == '\0' ? ' ' : c.Character);

                currentLogical.Append(rowText.ToString().TrimEnd());

                if (!row.IsWrapped)
                {
                    string logical = currentLogical.ToString().Trim();
                    if (logical.StartsWith("[LINE-"))
                    {
                        Assert.Equal(expectedLines[expectedIdx], logical);
                        expectedIdx++;
                    }
                    currentLogical.Clear();
                }

                if (expectedIdx == 1000) break;
            }

            Assert.Equal(1000, expectedIdx);
        }
        private bool GetRowWrapped(TerminalBuffer buffer, int physIdx)
        {
            var sbCount = GetScrollbackCount(buffer);
            TerminalRow row;
            if (physIdx < sbCount)
                row = GetScrollbackRow(buffer, physIdx);
            else
                row = GetViewportRow(buffer, physIdx - sbCount);
            return row.IsWrapped;
        }

        [Fact]
        public void HorizontalResize_WrapAndUnwrap_ShouldMergeLinesCorrectly()
        {
            var buffer = new TerminalBuffer(20, 5);
            buffer.Write("Prompt> ");

            _output.WriteLine("Initial State (20x5):");
            DumpBuffer(buffer);

            // 1. Shrink to Width 4. (WRAP EXPECTED)
            buffer.Resize(4, 5);
            _output.WriteLine("After Shrink (4x5):");
            DumpBuffer(buffer);

            // Verify Wrap
            int sbCount = GetScrollbackCount(buffer);
            int promIdx = -1;
            for (int i = 0; i < sbCount + 5; i++)
            {
                if (GetRowText(buffer, i).Trim() == "Prom") promIdx = i;
            }
            Assert.True(promIdx != -1, "Could not find 'Prom' line");
            Assert.True(GetRowWrapped(buffer, promIdx), "'Prom' line should be wrapped");

            // 2. Grow back to 20. (UNWRAP EXPECTED)
            buffer.Resize(20, 5);
            _output.WriteLine("After Grow (20x5):");
            DumpBuffer(buffer);

            // After Grow, cursor row is PRESERVED.
            string rowText = GetRowText(buffer, buffer.CursorRow + GetScrollbackCount(buffer));
            Assert.Equal("Prompt>", rowText.Trim());

            // Verify NO 'Prom' line remains
            for (int i = 0; i < sbCount + 5; i++)
            {
                string text = GetRowText(buffer, i).Trim();
                if (text == "Prom") Assert.Fail("Found ghost 'Prom' line - merge failed!");
                if (text == "pt>") Assert.Fail("Found ghost 'pt>' line - merge failed!");
            }

            Assert.Equal(8, buffer.CursorCol);
        }

        [Fact]
        public void HorizontalResize_PromptDuplication_Repro()
        {
            // Scenario: Prompt "LongPrompt> " (12 chars).
            // Initial Width: 20. Fits widely.
            // Shrink to 5. Wraps to 3 lines: "LongP", "rompt", "> ".
            // Grow back to 20. Should merge to "LongPrompt> ".

            var buffer = new TerminalBuffer(20, 10);
            buffer.Write("LongPrompt> ");

            // Initial state
            Assert.Equal(0, buffer.CursorRow);
            Assert.Equal(12, buffer.CursorCol);

            _output.WriteLine("Before Shrink (20x10):");
            DumpBuffer(buffer);

            // Shrink to 5
            buffer.Resize(5, 10);

            _output.WriteLine("After Shrink (5x10):");
            DumpBuffer(buffer);

            // Verify wrapping
            // Line 0: "LongP" (Wrapped)
            // Line 1: "rompt" (Wrapped)
            // Line 2: "> "    (Unwrapped)
            Assert.Equal("LongP", GetRowText(buffer, 0).Trim());
            Assert.True(GetRowWrapped(buffer, 0), "Line 0 should be wrapped");
            Assert.Equal("rompt", GetRowText(buffer, 1).Trim());
            Assert.True(GetRowWrapped(buffer, 1), "Line 1 should be wrapped");

            string line2 = GetRowText(buffer, 2);
            // With new behavior: cursor row is PRESERVED (not wiped)
            // So line 2 should contain "> " (the unwrapped continuation)
            Assert.Contains(">", line2);

            // Cursor should be at Line 2, Col 2
            Assert.Equal(2, buffer.CursorRow);
            Assert.Equal(2, buffer.CursorCol);

            // Grow back to 20
            buffer.Resize(20, 10);

            _output.WriteLine("After Grow (20x10):");
            DumpBuffer(buffer);

            // Verify Merge - with new behavior cursor row is PRESERVED
            // Line 0: Should contain the merged prompt "LongPrompt>"
            Assert.Contains("LongPrompt>", GetRowText(buffer, 0));
            // Line 1 should be empty (the next row gets cleared for oh-my-posh)
            Assert.Equal("", GetRowText(buffer, 1).Trim());

            // Cursor should be at Line 0, Col 12
            Assert.Equal(0, buffer.CursorRow);
            Assert.Equal(12, buffer.CursorCol);

            // Check no ghost wrapped segments remain (old "rompt" or "LongP" fragments)
            for (int i = 2; i < 5; i++)
            {
                string rowText = GetRowText(buffer, i).Trim();
                if (rowText.Length > 0 && (rowText.Contains("LongP") || rowText.Contains("rompt")))
                {
                    Assert.Fail($"Found ghost wrapped segment on Line {i}: '{rowText}'");
                }
            }
        }

        [Fact]
        public void HorizontalResize_PromptAtBottom_ShouldKeepCursorVisible()
        {
            // Scenario: 20x5. Filled with 4 lines of text. Prompt on Line 4 (Bottom).
            // Shrink to 10x5. Text wraps.
            // Should force scroll (content pushes to SB).
            // Cursor should remain at Bottom (Line 4).
            // Old content should be in SB.

            var buffer = new TerminalBuffer(20, 5);
            buffer.Write("Line 1\n");
            buffer.Write("Line 2\n");
            buffer.Write("Line 3\n");
            buffer.Write("Line 4\n");
            buffer.Write("Prompt> ");

            _output.WriteLine("Before Shrink (20x5):");
            DumpBuffer(buffer);

            // Cursor at (4, 8)
            Assert.Equal(4, buffer.CursorRow);

            // Shrink width to 10.
            // "Line X" (6 chars) -> No wrap.
            // "Prompt> " (8 chars) -> No wrap.
            // Wait, this doesn't force wrap. I need longer lines.
            // Let's use 20 chars lines.
            // "12345678901234567890" (20 chars).
            // Shrink to 10. Becomes 2 lines each.

            buffer.Resize(20, 5); // Reset size just in case (no op)
            // Clear and Refill
            // We can't clear easily without helper, just make new buffer
            buffer = new TerminalBuffer(20, 5);
            buffer.Write("12345678901234567890"); // Line 0
            buffer.Write("12345678901234567890"); // Line 1
            buffer.Write("12345678901234567890"); // Line 2
            buffer.Write("12345678901234567890"); // Line 3
            buffer.Write("Prompt> ");             // Line 4 (8 chars)

            // Cursor at (4, 8).

            // Shrink to 10.
            // Line 0 -> 2 lines.
            // Line 1 -> 2 lines.
            // Line 2 -> 2 lines.
            // Line 3 -> 2 lines.
            // Prompt -> 1 line.
            // Total 9 lines.
            // Buffer Height 5.
            // We expect 4 lines to go to SB.
            // Viewport shows last 5 lines.
            // VP[0] = Line 2b.
            // VP[1] = Line 3a.
            // VP[2] = Line 3b.
            // VP[3] = Prompt.
            // VP[4] = Empty/Padding?

            // Wait, "Top Anchoring" logic for Shrink:
            // "activeContentSize" = 9. "newRows" = 5.
            // "sbCount" increases by 4.
            // "vpCount" = 5.
            // We fill viewport with last 5 rows.
            // Prompt (Row 8) should be at VP[3] or VP[4]?
            // Rows are 0..8.
            // SB gets 0..3.
            // VP gets 4..8.
            // Row 4 is "1234..." (Line 2 part 1).
            // Row 8 is "Prompt> ".
            // So VP contains: Line 2a, Line 2b, Line 3a, Line 3b, Prompt.
            // So Prompt is at VP[4] (Bottom).

            buffer.Resize(10, 5);

            _output.WriteLine("After Shrink (10x5):");
            DumpBuffer(buffer);

            // Verify Cursor is at Row 4 (Bottom)
            Assert.Equal(4, buffer.CursorRow);
            // ROW IS PRESERVED (for PowerShell).
            int sbCount = GetScrollbackCount(buffer);
            Assert.Equal(4, sbCount);
            int cursorAbsRow = sbCount + buffer.CursorRow;
            // Cursor row should have content
            Assert.NotEqual("", GetRowText(buffer, cursorAbsRow).Trim());

            // Verify Scrollback count
            Assert.Equal(4, GetScrollbackCount(buffer));
        }

        [Fact]
        public void VerticalGrow_ShouldNotWipeCursorRow()
        {
            var buffer = new TerminalBuffer(20, 5);
            buffer.Write("Prompt: ");
            buffer.CursorCol = 8;
            buffer.CursorRow = 0;

            // Vertical Grow: 20x5 -> 20x10
            buffer.Resize(20, 10);

            _output.WriteLine("After Vertical Grow (20x10):");
            DumpBuffer(buffer);

            // Verify Row 0 still has content (Not wiped)
            Assert.Equal(0, buffer.CursorRow);
            Assert.Contains("Prompt: ", GetRowText(buffer, 0));
        }

        [Fact]
        public void HorizontalGrow_ShouldNotLeakBackground()
        {
            var buffer = new TerminalBuffer(10, 5);
            // Simulating a colored block at the end of a line
            // Row 0: "Hello" (Indices 0-4)
            buffer.Write("Hello");

            // Manaully set a background color on index 4
            var row = GetViewportRow(buffer, 0);
            var lastChar = row.Cells[4];
            row.Cells[4] = new TerminalCell(lastChar.Character, lastChar.Foreground, TermColor.FromRgb(255, 0, 0), false, false, true, false);

            _output.WriteLine("Before Horizontal Grow (10x5):");
            DumpBuffer(buffer);

            // Horizontal Grow: 10x5 -> 20x5
            buffer.Resize(20, 5);

            _output.WriteLine("After Horizontal Grow (20x5):");
            DumpBuffer(buffer);

            // Verify cells 10-19 are default background (NOT #FF0000)
            var newRow = GetViewportRow(buffer, 0);
            for (int i = 10; i < 20; i++)
            {
                Assert.True(newRow.Cells[i].IsDefaultBackground, $"Cell {i} should have default background");
            }
        }
    }
}

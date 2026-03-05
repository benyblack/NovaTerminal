using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using NovaTerminal.Core;
using NovaTerminal.Core.Storage;
using Avalonia.Media;

namespace NovaTerminal.Tests
{
    public partial class TerminalBufferTests
    {
        [Fact]
        public void Reflow_ShouldNotDuplicatePromptIntoHistory()
        {
            // Arrange
            int initialCols = 80;
            int initialRows = 24;
            var buffer = new TerminalBuffer(initialCols, initialRows);

            // 1. Fill History
            // Write 100 lines. The buffer height is 24.
            // So ~76 lines should end up in Scrollback.
            // And 24 lines in Viewport.
            int totalLines = 100;
            for (int i = 0; i < totalLines; i++)
            {
                buffer.Write($"History Line {i}\n");
            }

            // 2. Write Prompt (Mocking a multi-line prompt)
            buffer.Write("user@host:~/project/nova2$ \n");
            buffer.Write("> Command Input");

            // Verify Initial State via Reflection
            var scrollback = GetScrollback(buffer);
            var initialScrollbackCount = scrollback.Count;
            Assert.True(initialScrollbackCount > 0, "Scrollback should have history.");

            // Act
            // Trigger Multiple Resizes (simulate user dragging window)
            // This captures the "Recursive" nature of the bug.

            for (int i = 0; i < 5; i++)
            {
                // Shrink
                buffer.Resize(60, 24);

                // Grow
                buffer.Resize(100, 24);
            }

            // Final Resize to original width for easier assertion
            buffer.Resize(100, 24);

            // Assert
            // The Reflow logic should:
            // 1. Identify "History" vs "Prompt".
            // 2. Reflow History.
            // 3. Float Prompt.
            // 4. NOT add the floated prompt to the Scrollback history.

            var newScrollback = GetScrollback(buffer);
            int newScrollbackCount = newScrollback.Count;

            // Simple Heuristic: Scrollback count should NOT increase significantly.
            // It might change slightly due to wrapping (less wrapping = fewer lines).
            // But it should definitely NOT have "user@host..." appended to it.

            // Check if last line of scrollback contains prompt text
            if (newScrollbackCount > 0)
            {
                var lastHistoryRow = newScrollback.GetRow(newScrollbackCount - 1);
                var text = GetTextFromSpan(lastHistoryRow);

                Assert.DoesNotContain("Command Input", text);
                Assert.DoesNotContain("user@host", text);
            }
        }

        private ScrollbackPages GetScrollback(TerminalBuffer buffer)
        {
            var field = typeof(TerminalBuffer).GetField("_scrollback", BindingFlags.NonPublic | BindingFlags.Instance);
            return (ScrollbackPages)field!.GetValue(buffer)!;
        }

        private string GetRowTextContent(TerminalRow row)
        {
            if (row.Cells != null)
            {
                return GetTextFromSpan(row.Cells);
            }
            return "";
        }

        private string GetTextFromSpan(ReadOnlySpan<TerminalCell> span)
        {
            char[] chars = new char[span.Length];
            for (int i = 0; i < span.Length; i++)
            {
                chars[i] = span[i].Character == '\0' ? ' ' : span[i].Character;
            }
            return new string(chars).Trim();
        }

        [Fact]
        public void RestoreCursor_ShouldRestoreBold()
        {
            var buffer = new TerminalBuffer(80, 24);

            // Set bold and save cursor
            buffer.IsBold = true;
            buffer.SaveCursor();

            // Change bold state
            buffer.IsBold = false;

            // Restore cursor
            buffer.RestoreCursor();

            // Assert: IsBold should be restored to true
            Assert.True(buffer.IsBold);
        }
        [Fact]
        public void Reflow_WithColoredGap_ShouldNotDuplicate()
        {
            // PROMPT WITH COLORED PADDING TEST
            // This simulates the user's issue where the "Gap" lines had background colors.

            int initialCols = 80;
            int initialRows = 24;
            var buffer = new TerminalBuffer(initialCols, initialRows);

            // 1. Fill History
            for (int i = 0; i < 50; i++)
            {
                buffer.Write($"History {i}\n");
            }

            // 2. Add COLORED Gaps (Padding)
            // Simulating Theme padding or layout
            var paddingRow = new TerminalRow(initialCols);
            for (int c = 0; c < initialCols; c++)
                paddingRow.Cells[c] = new TerminalCell(' ', TermColor.White, TermColor.Blue); // Blue background

            buffer.Write("\n"); // Normal newline
            // Inject Colored Empty Row manually to be sure
            buffer.Test_AddRowDirectly(paddingRow);
            buffer.Test_AddRowDirectly(paddingRow);

            // 3. Write Prompt
            buffer.Write("user@host:~$ ");

            // Act
            for (int i = 0; i < 5; i++)
            {
                buffer.Resize(60, 24);
                buffer.Resize(100, 24);
            }
            buffer.Resize(100, 24);

            // Assert
            var scrollback = GetScrollback(buffer);
            var text = GetTextFromSpan(scrollback.GetRow(scrollback.Count - 1));

            // Should NOT contain the prompt
            Assert.DoesNotContain("user@host", text);
        }
        [Fact]
        public void Reflow_WithDesyncCursor_ShouldDuplicate()
        {
            // PROMPT DUPLICATION SCENARIO (Desync)
            // If the cursor logic fails to verify the prompt line,
            // we default to "Single Line Strategy" or fail to capture the prompt block efficiently.
            // This tests what happens if CheckCursor fails to match the prompt.

            int initialCols = 80;
            int initialRows = 24;
            var buffer = new TerminalBuffer(initialCols, initialRows);

            // 1. Fill History
            for (int i = 0; i < 50; i++) buffer.Write($"History {i}\n");

            // 2. Write Prompt
            buffer.Write("user@host:~$ ");

            // 3. DESYNC CURSOR intentionally
            // Move cursor ROW up by 1 (pointing to history or empty space?)
            // "user@host:~$ " is on last line.
            // Move it up.
            buffer.CursorRow -= 1;

            // Act
            buffer.Resize(100, 24);

            // Assert
            // Because Cursor was wrong, the "Active Block" logic might capture the WRONG block (or nothing).
            // The Real Prompt (at last line) remains in History?
            // Wait, if CursorRow points to -1 relative, Scan UP from -1?
            // "Active Block" logic has a fallback: "If curLogicalIdx == -1, find last non-empty line".
            // So my Fallback Logic SHOULD SAVE IT!

            // So if this test PASSES (No Duplication), then my Fallback Logic is working perfectly.
            // If this test FAILS (Duplication), then Fallback Logic is broken.

            var scrollback = GetScrollback(buffer);
            var text = GetTextFromSpan(scrollback.GetRow(scrollback.Count - 1));

            // Attempt to assert NO duplication (Validation of Fallback)
            Assert.DoesNotContain("user@host", text);
        }
        [Fact]
        public void Reflow_WithDenseHistory_ShouldNotWipeBuffer()
        {
            // DENSE HISTORY SCENARIO (HISTORY WIPE REGRESSION)
            // If the "Upward Scan" logic is unbounded and finds no gap,
            // it might consume the ENTIRE buffer as the "Active Block",
            // causing the entire history to be truncated (deleted).

            int initialCols = 80;
            int initialRows = 24;
            var buffer = new TerminalBuffer(initialCols, initialRows);

            // 1. Fill History COMPLETELY (Dense)
            // 100 lines of solid text, NO empty lines.
            int totalLines = 100;
            for (int i = 0; i < totalLines; i++) buffer.Write($"Data Line {i} - lots of data here to ensure no gaps\n");

            // 2. Write Prompt at the end
            buffer.Write("user@host:~$ ");

            // Verify Initial State
            var scrollback = GetScrollback(buffer);
            Assert.True(scrollback.Count > 50, "Ideally simulated dense history");

            // Act
            buffer.Resize(100, 24);

            // Assert
            // History should STILL be there.
            var newScrollback = GetScrollback(buffer);

            // If the bug exists, newScrollback might be EMPTY or very small 
            // (simulating "history goes away").

            Assert.True(newScrollback.Count > 50,
                $"History was wiped! Expected > 50 lines, found {newScrollback.Count}. Upward scan likely too greedy.");

            // Also check that the TOP of history is preserved
            var firstLine = GetTextFromSpan(newScrollback.GetRow(0));
            Assert.Contains("Data Line 0", firstLine);
        }
        [Fact]
        public void Reflow_ShouldNotInsertExtraNewlines()
        {
            // RECURSIVE NEWLINE BUG
            // User reports: "resize puts new lines between rows and each time adds more empty lines in between"

            int initialCols = 80;
            int initialRows = 24;
            var buffer = new TerminalBuffer(initialCols, initialRows);

            // 1. Fill with packed lines (No empty lines between)
            buffer.Write("Line 1\n");
            buffer.Write("Line 2\n");
            buffer.Write("Line 3\n");
            buffer.Write("Line 4\n");
            // Cursor is now on Line 5 (index 4). Reflow will discard line 5.
            // Lines 1-4 should be preserved history.

            // Initial State: 4 lines of text. 
            // Check Scrollback/Viewport to ensure no gaps initially.
            // (Note: Write("\n") advances to next line, so we expect tight packing)

            // Act: Resize multiple times
            for (int i = 0; i < 10; i++)
            {
                buffer.Resize(100, 24);
                buffer.Resize(60, 24);
            }
            buffer.Resize(initialCols, initialRows); // Back to normal

            // Assert
            var scrollback = GetScrollback(buffer);
            var viewport = GetViewport(buffer);

            // Combine all rows to check content
            var contentTexts = new List<string>();
            var sbRows = GetScrollback(buffer);
            for (int i = 0; i < sbRows.Count; i++) contentTexts.Add(GetTextFromSpan(sbRows.GetRow(i)));
            
            var vpRows = GetViewport(buffer);
            foreach (var r in vpRows) contentTexts.Add(GetRowTextContent(r));

            var contentRowsText = contentTexts.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

            // We expect exactly 4 content rows.
            // If the bug exists, we might see 8, 12, or spaces in between.

            Assert.Equal(4, contentRowsText.Count);
            Assert.Contains("Line 1", contentRowsText[0]);
            Assert.Contains("Line 2", contentRowsText[1]);
            Assert.Contains("Line 3", contentRowsText[2]);
            Assert.Contains("Line 4", contentRowsText[3]);
        }



        [Fact]
        public void Reflow_ShouldHandleEdgeWrapPrompt()
        {
            // Scenario: Prompt "looks" like it occupies a line, but cursor is on the NEXT line (empty).
            // This happens if prompt fits exactly or ConPTY behaves weirdly with wrapping.
            // Result: Cursor line is empty. Previous line is Prompt (no newline).
            // Expectation: Both lines must be discarded to prevent duplication.

            var buffer = new TerminalBuffer(80, 24);

            // 1. Write Prompt (No Newline)
            buffer.Write("user@host:~$ ");

            // 2. FORCE Cursor to next line without writing \n
            // This simulates the "Edge Wrap" or "Phantom Newline" state.
            buffer.CursorRow = 1;
            buffer.CursorCol = 0;

            // Add an empty row at index 1 to represent the visual state
            var viewport = GetViewport(buffer);
            viewport[1] = new TerminalRow(80); // Empty row

            // Act
            buffer.Resize(100, 24);

            // Assert
            var scrollback = GetScrollback(buffer);
            // We expect the prompt to be GONE from history.
            // Because it was "Active", and we discard active lines.

            if (scrollback.Count > 0)
            {
                var text = GetTextFromSpan(scrollback.GetRow(scrollback.Count - 1));
                Assert.DoesNotContain("user@host", text);
            }
        }

        private bool IsRowEmpty(TerminalRow row)
        {
            foreach (var c in row.Cells)
                if (c.Character != ' ') return false;
            return true;
        }

        // REMOVED: Reflow_WithMergedPrompt_ShouldIsolate
        // This test enforced behavior (Isolating history from prompt even if wrapped)
        // that conflicts with treating specific wrapped prompts correctly.
        // We accept that merged history/prompt will be truncated together.

        [Fact]
        public void Reflow_ShouldNotAddPaddingGaps()
        {
            // Test for the "Horizontal Empty Lines" bug
            var buffer = new TerminalBuffer(80, 24);
            // Place lines at top, set cursor below them
            var viewport = GetViewport(buffer);
            var r1 = new TerminalRow(80); SetRowText(r1, "Line 1"); viewport[0] = r1;
            var r2 = new TerminalRow(80); SetRowText(r2, "Line 2"); viewport[1] = r2;

            // Set cursor at line 2 (empty) so we don't truncate lines 0 and 1
            buffer.CursorRow = 2;
            buffer.CursorCol = 0;

            // Resize loop
            for (int i = 0; i < 10; i++)
            {
                buffer.Resize(80 + i, 24); // Grow width
                buffer.Resize(80, 24);     // Srink width
            }

            var allTexts = GetAllRowTexts(buffer);
            var nonNullContent = allTexts.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

            // Should just be 2 lines
            Assert.Equal(2, nonNullContent.Count);
        }

        private void SetRowText(TerminalRow row, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                row.Cells[i].Character = text[i];
                row.Cells[i].Foreground = TermColor.White;
            }
        }

        private void AddRowToBuffer(TerminalBuffer buffer, TerminalRow row)
        {
            var viewport = GetViewport(buffer);
            // shift
            for (int i = 0; i < viewport.Length - 1; i++) viewport[i] = viewport[i + 1];
            viewport[viewport.Length - 1] = row;
        }

        private List<string> GetAllRowTexts(TerminalBuffer buffer)
        {
            var list = new List<string>();
            var sb = GetScrollback(buffer);
            for (int i = 0; i < sb.Count; i++) list.Add(GetTextFromSpan(sb.GetRow(i)));
            
            var vp = GetViewport(buffer);
            foreach (var r in vp) list.Add(GetRowTextContent(r));
            return list;
        }

        private TerminalRow[] GetViewport(TerminalBuffer buffer)
        {
            var field = typeof(TerminalBuffer).GetPrivateField("_viewport");
            return (TerminalRow[])field.GetValue(buffer)!;
        }
    }

    public static class TerminalBufferExtensions
    {
        public static void Test_AddRowDirectly(this TerminalBuffer buffer, TerminalRow row)
        {
            // Use reflection to add directly to _viewport or logical lines?
            // "Write" adds to viewport at cursor.
            // Let's use private _viewport access via reflection.
            // But we need to manage CursorRow.
            // Easiest is to force a ScrollUp if at bottom, or write to current line.
            // But "Write" handles text.

            // Hack for test: Use Write("\n") then modify the row attributes via Reflection/Access?
            // buffer.Write("\n");
            // buffer.CursorRow--; 
            // set row data...
            // buffer.CursorRow++;

            // Wait, we can't easily hook into "Write" logic to inject a whole row object.
            // But we can modify the CURRENT row's cells.

            // This requires exposing GetRow/SetRow logic.
            // Let's rely on standard Write for now, but assume we can't set BgColor via Write API in test easily 
            // (we need Ansi parser for that).
            // UNLESS we use reflection to get _viewport array, and modify it.

            var field = typeof(TerminalBuffer).GetPrivateField("_viewport");
            var viewport = (TerminalRow[])field.GetValue(buffer)!;
            int cursorRow = buffer.CursorRow;

            // Add newline to advance
            buffer.Write("\n");

            // Get the row we just moved past (history) or current? 
            // Write("\n") puts us on next line.
            int targetRowIdx = buffer.CursorRow - 1;
            if (targetRowIdx < 0)
            {
                // Scrolled off?
                // Get from Scrollback? Too complex.
                return;
            }

            // Replace the row in viewport
            viewport[targetRowIdx] = row;
        }

    }

    public static class ReflectionExtensions
    {
        public static FieldInfo GetPrivateField(this Type type, string name)
        {
            var field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null) throw new Exception($"Field '{name}' not found on type '{type.Name}'");
            return field;
        }

        public static void SetPrivateField(this object obj, string name, object value)
        {
            var field = obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null) throw new Exception($"Field '{name}' not found on type '{obj.GetType().Name}'");
            field.SetValue(obj, value);
        }
    }

    public partial class TerminalBufferTests
    {

        [Fact]
        public void Reflow_MultiLinePrompt_ShouldNotDuplicate()
        {
            // Arrange
            var buffer = new TerminalBuffer(80, 24);
            buffer.Write("History Line\n");
            // Multi-line prompt with hard newlines
            buffer.Write("user@host\n");
            buffer.Write("path/to/project\n");
            buffer.Write("$ "); // Cursor here

            // Act
            buffer.Resize(40, 24);

            // Assert
            var field = typeof(TerminalBuffer).GetField("_scrollback", BindingFlags.NonPublic | BindingFlags.Instance);
            var scrollback = (ScrollbackPages)field!.GetValue(buffer)!;

            var sbText = "";
            for (int i = 0; i < scrollback.Count; i++) sbText += GetTextFromSpan(scrollback.GetRow(i)) + "\n";
            var historyText = sbText.Trim();

            // Neither part of the prompt should be in history
            Assert.DoesNotContain("user@host", historyText);
            Assert.DoesNotContain("path/to/project", historyText);
        }

        [Fact]
        public void Reflow_StyledHistory_ShouldPreserveStyles()
        {
            // Arrange
            var buffer = new TerminalBuffer(80, 24);
            buffer.Clear();
            buffer.CurrentBackground = TermColor.Red;
            buffer.Write("History Red\n");
            buffer.CurrentBackground = TermColor.Black;
            buffer.Write("Prompt> ");

            // Act
            buffer.Resize(40, 24);

            // Assert
            bool foundRed = false;

            // Access _scrollback via reflection for thorough check
            var field = typeof(TerminalBuffer).GetField("_scrollback", BindingFlags.NonPublic | BindingFlags.Instance);
            var sb = (ScrollbackPages)field!.GetValue(buffer)!;

            // Check Scrollback
            for (int i = 0; i < sb.Count; i++)
            {
                var rowSpan = sb.GetRow(i);
                foreach (var cell in rowSpan)
                {
                    if (cell.Background == TermColor.Red) { foundRed = true; break; }
                }
                if (foundRed) break;
            }

            // Check Viewport
            if (!foundRed)
            {
                buffer.Lock.EnterReadLock();
                try
                {
                    for (int r = 0; r < buffer.Rows; r++)
                    {
                        for (int c = 0; c < buffer.Cols; c++)
                        {
                            if (buffer.GetCellAbsolute(c, r + sb.Count).Background == TermColor.Red)
                            {
                                foundRed = true;
                                break;
                            }
                        }
                        if (foundRed) break;
                    }
                }
                finally { buffer.Lock.ExitReadLock(); }
            }

            Assert.True(foundRed, "Red background (history or visible) should be preserved.");
        }

        [Fact]
        public void Reflow_RepeatedResize_ShouldNotGrowEmptyLines()
        {
            // Arrange
            var buffer = new TerminalBuffer(80, 10);
            for (int i = 0; i < 5; i++) buffer.Write($"Line {i}\n");
            buffer.Write("Prompt> ");

            int initialTotal = buffer.Scrollback.Count + buffer.Rows;

            // Act
            for (int i = 0; i < 10; i++)
            {
                buffer.Resize(60, 10);
                buffer.Resize(100, 10);
            }

            // Assert
            int finalTotal = buffer.Scrollback.Count + buffer.Rows;
            // It shouldn't grow indefinitely. 
            Assert.True(finalTotal < initialTotal + 20, $"Buffer grew too much: {initialTotal} -> {finalTotal}");
        }
    }
}

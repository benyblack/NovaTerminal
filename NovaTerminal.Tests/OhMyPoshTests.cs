using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using NovaTerminal.Core;
using Xunit.Abstractions;

namespace NovaTerminal.Tests
{
    public class OhMyPoshTests
    {
        private readonly ITestOutputHelper _output;

        public OhMyPoshTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private List<TerminalRow> GetScrollback(TerminalBuffer buffer)
        {
            var field = typeof(TerminalBuffer).GetField("_scrollback", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (List<TerminalRow>)field!.GetValue(buffer)!;
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
            return new string(chars).TrimEnd();
        }

        private void DumpBuffer(TerminalBuffer buffer)
        {
            var sb = GetScrollback(buffer);
            var vp = GetViewport(buffer);

            _output.WriteLine($"=== BUFFER DUMP (Scrollback: {sb.Count}, Viewport: {vp.Length}) ===");
            _output.WriteLine("SCROLLBACK:");
            for (int i = 0; i < sb.Count; i++)
            {
                _output.WriteLine($"  [{i}] '{GetRowText(sb[i])}'");
            }
            _output.WriteLine("VIEWPORT:");
            for (int i = 0; i < vp.Length; i++)
            {
                string marker = (i == buffer.CursorRow) ? " <-- CURSOR" : "";
                _output.WriteLine($"  [{i}] '{GetRowText(vp[i])}'{marker}");
            }
            _output.WriteLine($"Cursor: ({buffer.CursorRow}, {buffer.CursorCol})");
        }

        [Fact]
        public void OhMyPosh_RightAlignedText_HorizontalShrink_ShouldClearAllPromptLines()
        {
            // Simulate oh-my-posh prompt with right-aligned datetime:
            // "PS> " on the left, "12:34:56" on the right at column 73
            
            var buffer = new TerminalBuffer(80, 24);
            
            // Write some history
            buffer.Write("Previous command output\n");
            
            // Simulate oh-my-posh prompt with cursor positioning
            // Left part: "PS C:\\Users\\Dev> "
            buffer.Write("PS C:\\Users\\Dev> ");
            int promptEndCol = buffer.CursorCol;
            
            // Simulate absolute cursor positioning to far right (like oh-my-posh does)
            // We'll manually set a cell at position 73 for the datetime
            var cursorRow = buffer.CursorRow;
            var scrollbackCount = buffer.ScrollbackRows.Count;
            var viewport = GetViewport(buffer);
            var row = viewport[cursorRow];
            
            // Place datetime at far right: "12:34:56" starting at column 73
            string datetime = "12:34:56";
            int rightPos = 73;
            for (int i = 0; i < datetime.Length && (rightPos + i) < 80; i++)
            {
                row.Cells[rightPos + i] = new TerminalCell(
                    datetime[i],
                    row.Cells[rightPos + i].Foreground,
                    row.Cells[rightPos + i].Background,
                    false, false, false, false
                );
            }

            _output.WriteLine("=== BEFORE RESIZE ===");
            DumpBuffer(buffer);

            // Act: Horizontal shrink to 60 columns
            // The datetime at column 73 is now beyond the new width
            // It should wrap to the next line during reflow
            buffer.Resize(60, 24);

            _output.WriteLine("\n=== AFTER RESIZE ===");
            DumpBuffer(buffer);

            // Assert: Check that prompt area is clean
            // The cursor row should be cleared
            var vp = GetViewport(buffer);
            var cursorRowText = GetRowText(vp[buffer.CursorRow]);
            _output.WriteLine($"Cursor row text: '{cursorRowText}'");
            Assert.Equal("", cursorRowText.Trim());

            // Check the next row too - if datetime wrapped, it should also be cleared
            if (buffer.CursorRow + 1 < vp.Length)
            {
                var nextRowText = GetRowText(vp[buffer.CursorRow + 1]);
                _output.WriteLine($"Next row text: '{nextRowText}'");
                // If there's datetime remnants, this would contain "12:34:56"
                Assert.DoesNotContain("12:34", nextRowText);
            }
        }

        [Fact]
        public void OhMyPosh_RightAlignedText_ShouldNotLeakToPreviousLine()
        {
            // Test that right-aligned text doesn't leak into history during reflow
            
            var buffer = new TerminalBuffer(80, 24);
            buffer.Write("Command 1\n");
            buffer.Write("Command 2\n");
            
            // Prompt with right-side text
            buffer.Write("PS> ");
            var viewport = GetViewport(buffer);
            var row = viewport[buffer.CursorRow];
            
            // Add datetime on far right
            string datetime = "10:30:00";
            int rightPos = 72;
            for (int i = 0; i < datetime.Length; i++)
            {
                row.Cells[rightPos + i] = new TerminalCell(
                    datetime[i],
                    row.Cells[rightPos + i].Foreground,
                    row.Cells[rightPos + i].Background,
                    false, false, false, false
                );
            }

            // Shrink to 50 columns
            buffer.Resize(50, 24);

            // Check that history lines don't have datetime text
            var sb = GetScrollback(buffer);
            foreach (var histRow in sb)
            {
                string text = GetRowText(histRow);
                Assert.DoesNotContain("10:30:00", text);
            }
        }
    }
}

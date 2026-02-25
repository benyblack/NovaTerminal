using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using NovaTerminal.Core;
using Xunit.Abstractions;

namespace NovaTerminal.Tests
{
    public class CMDDuplicationTests
    {
        private readonly ITestOutputHelper _output;

        public CMDDuplicationTests(ITestOutputHelper output)
        {
            _output = output;
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
        public void CMD_HorizontalShrink_WithPromptRedraw_ShouldNotDuplicate()
        {
            // Simulate CMD behavior:
            // 1. Prompt exists at cursor position
            // 2. User resizes window (width changes)
            // 3. Our Reflow runs
            // 4. CMD redraws prompt at new cursor position
            // 5. Check for duplication

            var buffer = new TerminalBuffer(80, 24);

            // Write some history
            buffer.Write("Command output line 1\r\n");
            buffer.Write("Command output line 2\r\n");

            // Write CMD prompt
            string prompt = "C:\\Users\\Dev> ";
            buffer.Write(prompt);

            int cursorRowBefore = buffer.CursorRow;
            int cursorColBefore = buffer.CursorCol;

            _output.WriteLine("=== BEFORE RESIZE ===");
            DumpBuffer(buffer);

            // Act: Horizontal shrink (80 -> 60)
            buffer.Resize(60, 24);

            _output.WriteLine("\n=== AFTER RESIZE (before shell redraw) ===");
            DumpBuffer(buffer);

            // Simulate shell redrawing the prompt at cursor position
            // The shell would write the prompt again
            buffer.Write(prompt);

            _output.WriteLine("\n=== AFTER SHELL REDRAW ===");
            DumpBuffer(buffer);

            // Assert: Check for duplication
            // Count how many times the prompt appears
            var allRows = new List<TerminalRow>();
            allRows.AddRange(GetScrollback(buffer));
            allRows.AddRange(GetViewport(buffer));

            int promptCount = 0;
            foreach (var row in allRows)
            {
                string text = GetRowText(row);
                if (text.Contains("C:\\Users\\Dev>"))
                {
                    promptCount++;
                    _output.WriteLine($"Found prompt in row: '{text}'");
                }
            }

            Assert.Equal(1, promptCount);
        }

        [Fact]
        public void CMD_HorizontalGrow_WithPromptRedraw_ShouldNotDuplicate()
        {
            var buffer = new TerminalBuffer(60, 24);

            buffer.Write("Output 1\r\n");
            buffer.Write("Output 2\r\n");

            string prompt = "C:\\Users\\Dev> ";
            buffer.Write(prompt);

            _output.WriteLine("=== BEFORE RESIZE ===");
            DumpBuffer(buffer);

            // Act: Horizontal grow (60 -> 100)
            buffer.Resize(100, 24);

            _output.WriteLine("\n=== AFTER RESIZE ===");
            DumpBuffer(buffer);

            // Shell redraws
            buffer.Write(prompt);

            _output.WriteLine("\n=== AFTER SHELL REDRAW ===");
            DumpBuffer(buffer);

            // Check for duplication
            var allRows = new List<TerminalRow>();
            allRows.AddRange(GetScrollback(buffer));
            allRows.AddRange(GetViewport(buffer));

            int promptCount = 0;
            foreach (var row in allRows)
            {
                string text = GetRowText(row);
                if (text.Contains("C:\\Users\\Dev>"))
                {
                    promptCount++;
                }
            }

            Assert.Equal(1, promptCount);
        }
    }
}

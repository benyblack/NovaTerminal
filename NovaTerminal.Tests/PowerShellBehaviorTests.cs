using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using NovaTerminal.Core;
using Xunit.Abstractions;

namespace NovaTerminal.Tests
{
    /// <summary>
    /// Tests for realistic PowerShell behavior during resize.
    /// PowerShell often does NOT send redraw commands after horizontal resize,
    /// so prompts must be preserved by the terminal buffer itself.
    /// </summary>
    public class PowerShellBehaviorTests
    {
        private readonly ITestOutputHelper _output;

        public PowerShellBehaviorTests(ITestOutputHelper output)
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
        public void PowerShell_HorizontalResize_ShouldPreservePrompt_NoShellRedraw()
        {
            // This test reflects ACTUAL PowerShell behavior:
            // PowerShell does NOT redraw the prompt after horizontal resize.
            // The terminal buffer must preserve the prompt itself.
            
            var buffer = new TerminalBuffer(80, 24);
            
            // History
            buffer.Write("Get-ChildItem\n");
            buffer.Write("cd Documents\n");
            
            // PowerShell prompt
            string prompt = "PS C:\\Users\\Dev> ";
            buffer.Write(prompt);
            
            int promptRow = buffer.CursorRow;
            int promptCol = buffer.CursorCol;
            
            _output.WriteLine("=== BEFORE RESIZE ===");
            DumpBuffer(buffer);

            // Act: Horizontal shrink (80 -> 60)
            // PowerShell does NOT send redraw command
            buffer.Resize(60, 24);

            _output.WriteLine("\n=== AFTER RESIZE (no shell redraw) ===");
            DumpBuffer(buffer);

            // Assert: Prompt should still be visible
            // Cursor row should contain the prompt text
            var vp = GetViewport(buffer);
            var cursorRowText = GetRowText(vp[buffer.CursorRow]);
            
            _output.WriteLine($"Cursor row text: '{cursorRowText}'");
            _output.WriteLine($"Expected prompt: '{prompt}'");
            
            // The prompt MUST be preserved
            Assert.Contains("PS C:\\Users\\Dev>", cursorRowText);
            Assert.NotEqual("", cursorRowText.Trim());
        }

        [Fact]
        public void PowerShell_HorizontalGrow_ShouldPreservePrompt_NoShellRedraw()
        {
            var buffer = new TerminalBuffer(60, 24);
            
            buffer.Write("History 1\n");
            string prompt = "PS> ";
            buffer.Write(prompt);

            _output.WriteLine("=== BEFORE RESIZE ===");
            DumpBuffer(buffer);

            // Act: Horizontal grow (60 -> 100)
            buffer.Resize(100, 24);

            _output.WriteLine("\n=== AFTER RESIZE ===");
            DumpBuffer(buffer);

            // Assert
            var vp = GetViewport(buffer);
            var cursorRowText = GetRowText(vp[buffer.CursorRow]);
            
            Assert.Contains("PS>", cursorRowText);
        }
    }
}

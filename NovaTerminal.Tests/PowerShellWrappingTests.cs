using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using NovaTerminal.Core;
using Xunit.Abstractions;

namespace NovaTerminal.Tests
{
    /// <summary>
    /// Tests for PowerShell prompt wrapping behavior during horizontal shrink.
    /// When prompts wrap, we must NOT clear the wrapped continuation.
    /// </summary>
    public class PowerShellWrappingTests
    {
        private readonly ITestOutputHelper _output;

        public PowerShellWrappingTests(ITestOutputHelper output)
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
            return new string(chars).TrimEnd();
        }

        private void DumpBuffer(TerminalBuffer buffer)
        {
            var vp = GetViewport(buffer);
            _output.WriteLine($"=== Buffer Dump (Cursor: {buffer.CursorRow},{buffer.CursorCol}) ===");
            for (int i = 0; i < vp.Length; i++)
            {
                string marker = (i == buffer.CursorRow) ? " <-- CURSOR" : "";
                string wrapped = vp[i].IsWrapped ? " [WRAPPED]" : "";
                _output.WriteLine($"  [{i}] '{GetRowText(vp[i])}'{wrapped}{marker}");
            }
        }

        [Fact]
        public void PowerShell_HorizontalShrink_ShouldPreserveWrappedPrompt()
        {
            // Test case: Prompt that wraps when shrunk
            // Prompt: "PS C:\Users\Dev> " (17 chars)
            // Shrink to width 10 -> wraps to 2 lines
            // Both lines should be preserved (not cleared)
            
            var buffer = new TerminalBuffer(80, 24);
            
            string prompt = "PS C:\\Users\\Dev> ";
            buffer.Write(prompt);
            
            _output.WriteLine("=== BEFORE SHRINK (80 cols) ===");
            DumpBuffer(buffer);
            
            // Act: Shrink to 10 columns (prompt will wrap)
            buffer.Resize(10, 24);
            
            _output.WriteLine("\n=== AFTER SHRINK (10 cols) ===");
            DumpBuffer(buffer);
            
            // Assert: The full prompt should be visible across wrapped lines
            var vp = GetViewport(buffer);
            
            // Collect all text from cursor row and wrapped continuations
            string fullPromptText = "";
            int row = buffer.CursorRow;
            
            // Walk backwards to find start of prompt
            while (row > 0 && vp[row - 1].IsWrapped)
            {
                row--;
            }
            
            // Now collect forward from the start
            while (row < vp.Length)
            {
                fullPromptText += GetRowText(vp[row]);
                if (!vp[row].IsWrapped || row >= buffer.CursorRow)
                    break;
                row++;
            }
            
            _output.WriteLine($"Reconstructed prompt: '{fullPromptText.Trim()}'");
            
            // The prompt should be preserved (both left and right parts)
            Assert.Contains("PS C:\\Users\\Dev>", fullPromptText);
            // Specifically check for right-side content
            Assert.Contains("Dev>", fullPromptText);
        }

        [Fact]
        public void PowerShell_LongPrompt_HorizontalShrink_PreservesAllParts()
        {
            var buffer = new TerminalBuffer(60, 24);
            
            // Longer prompt that will definitely wrap
            string prompt = "PowerShell 7.4.0 C:\\Users\\Developer\\Documents> ";
            buffer.Write(prompt);
            
            _output.WriteLine("=== BEFORE SHRINK ===");
            DumpBuffer(buffer);
            
            // Shrink to 15 columns (will wrap to multiple lines)
            buffer.Resize(15, 24);
            
            _output.WriteLine("\n=== AFTER SHRINK (15 cols) ===");
            DumpBuffer(buffer);
            
            // Collect wrapped prompt
            var vp = GetViewport(buffer);
            string fullText = "";
            for (int i = 0; i <= buffer.CursorRow; i++)
            {
                fullText += GetRowText(vp[i]);
            }
            
            _output.WriteLine($"Full text: '{fullText.Trim()}'");
            
            // Assert all parts are present
            Assert.Contains("PowerShell", fullText);
            Assert.Contains("Documents>", fullText);  // Right side must be preserved
        }
    }
}

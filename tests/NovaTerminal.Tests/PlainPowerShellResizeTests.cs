using System;
using Xunit;
using NovaTerminal.Core;
using Xunit.Abstractions;
using System.Linq;

namespace NovaTerminal.Tests
{
    /// <summary>
    /// Tests for plain PowerShell prompt cursor position during horizontal resize
    /// </summary>
    public class PlainPowerShellResizeTests
    {
        private readonly ITestOutputHelper _output;

        public PlainPowerShellResizeTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private string GetRowText(TerminalBuffer buffer, int row)
        {
            var viewport = typeof(TerminalBuffer).GetField("_viewport", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(buffer) as TerminalRow[];
            if (viewport == null || row >= viewport.Length) return "";
            return new string(viewport[row].Cells.Select(c => c.Character == '\0' ? ' ' : c.Character).ToArray()).TrimEnd();
        }

        [Fact]
        public void PlainPowerShell_HorizontalShrinkGrow_CursorShouldStayAfterPrompt()
        {
            // Simulate plain PowerShell: "PS C:\> " prompt (8 chars)
            var buffer = new TerminalBuffer(80, 24);
            
            string prompt = "PS C:\\> ";
            buffer.Write(prompt);
            
            _output.WriteLine("=== INITIAL (80 cols) ===");
            _output.WriteLine($"Cursor: Row={buffer.CursorRow}, Col={buffer.CursorCol}");
            _output.WriteLine($"Row 0: '{GetRowText(buffer, 0)}'");
            
            Assert.Equal(0, buffer.CursorRow);
            Assert.Equal(8, buffer.CursorCol);  // Right after "PS C:\> "
            
            // Shrink to 40 columns
            buffer.Resize(40, 24);
            
            _output.WriteLine("\n=== AFTER SHRINK (40 cols) ===");
            _output.WriteLine($"Cursor: Row={buffer.CursorRow}, Col={buffer.CursorCol}");
            _output.WriteLine($"Row 0: '{GetRowText(buffer, 0)}'");
            
            Assert.Equal(0, buffer.CursorRow);
            Assert.Equal(8, buffer.CursorCol);  // Should still be at column 8
            
            // Grow back to 80 columns
            buffer.Resize(80, 24);
            
            _output.WriteLine("\n=== AFTER GROW (80 cols) ===");
            _output.WriteLine($"Cursor: Row={buffer.CursorRow}, Col={buffer.CursorCol}");
            _output.WriteLine($"Row 0: '{GetRowText(buffer, 0)}'");
            
            // CRITICAL: Cursor should be back at column 8, right after the prompt
            Assert.Equal(0, buffer.CursorRow);
            Assert.Equal(8, buffer.CursorCol);
            
            // Prompt should be intact
            Assert.Contains("PS C:\\>", GetRowText(buffer, 0));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using NovaTerminal.Core;
using Xunit.Abstractions;

namespace NovaTerminal.Tests
{
    /// <summary>
    /// Diagnostic test to understand exact buffer state during PowerShell horizontal shrink
    /// </summary>
    public class PowerShellDiagnosticTests
    {
        private readonly ITestOutputHelper _output;

        public PowerShellDiagnosticTests(ITestOutputHelper output)
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
        public void DiagnosePromptDisappearance()
        {
            // Use a realistic PowerShell prompt
            var buffer = new TerminalBuffer(80, 24);

            string prompt = "PS C:\\Users\\Developer> ";
            buffer.Write(prompt);

            _output.WriteLine($"Initial prompt: '{prompt}' (length: {prompt.Length})");
            _output.WriteLine($"Cursor before resize: Row={buffer.CursorRow}, Col={buffer.CursorCol}");

            var vp = GetViewport(buffer);
            _output.WriteLine("\n=== BEFORE SHRINK (80 cols) ===");
            for (int i = 0; i < 5; i++)
            {
                _output.WriteLine($"  Row {i}: '{GetRowText(vp[i])}' [IsWrapped={vp[i].IsWrapped}]");
            }

            // Shrink to 15 columns (prompt will wrap)
            buffer.Resize(15, 24);

            _output.WriteLine($"\nCursor after resize: Row={buffer.CursorRow}, Col={buffer.CursorCol}");

            vp = GetViewport(buffer);
            _output.WriteLine("\n=== AFTER SHRINK (15 cols) ===");
            for (int i = 0; i < 5; i++)
            {
                string text = GetRowText(vp[i]);
                _output.WriteLine($"  Row {i}: '{text}' [IsWrapped={vp[i].IsWrapped}] [Length={text.TrimEnd().Length}]");
            }

            // Reconstruct the full prompt from all rows
            string reconstructed = "";
            for (int i = 0; i <= buffer.CursorRow; i++)
            {
                reconstructed += GetRowText(vp[i]).TrimEnd();
            }

            _output.WriteLine($"\nReconstructed prompt: '{reconstructed}'");
            _output.WriteLine($"Expected: '{prompt.TrimEnd()}'");

            // Check if right side is present
            bool hasRightSide = reconstructed.Contains("Developer>");
            _output.WriteLine($"\nHas right side ('Developer>'): {hasRightSide}");

            if (!hasRightSide)
            {
                _output.WriteLine("\n*** RIGHT SIDE MISSING! ***");
                _output.WriteLine("Checking if it's in rows after cursor...");
                for (int i = buffer.CursorRow + 1; i < Math.Min(buffer.CursorRow + 5, vp.Length); i++)
                {
                    string text = GetRowText(vp[i]).TrimEnd();
                    if (text.Length > 0)
                    {
                        _output.WriteLine($"  Row {i}: '{text}' [IsWrapped={vp[i].IsWrapped}]");
                    }
                }
            }

            Assert.True(hasRightSide, "Right side of prompt should be preserved!");
        }
    }
}

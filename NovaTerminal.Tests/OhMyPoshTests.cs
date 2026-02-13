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

        [Fact]
        public void OhMyPosh_ShrinkThenGrow_ShouldReposition()
        {
            // REPRO: Shrink until gap is small (e.g. 2 spaces), then Grow.
            // Current logic (gap >= 10) fails to re-expand the gap.

            var buffer = new TerminalBuffer(50, 24);
            buffer.Write("Left> ");

            // Right content: "Right" (5 chars)
            // Positioned at col 45
            string right = "Right";
            int rightPos = 45;

            var row = GetViewport(buffer)[buffer.CursorRow];
            for (int i = 0; i < right.Length; i++)
                row.Cells[rightPos + i] = new TerminalCell(right[i], TermColor.White, TermColor.Black, false, false, false, false);

            _output.WriteLine("=== INITIAL (50) ===");
            _output.WriteLine(GetRowText(row));

            // 1. Shrink to force gap to be minimal
            // Left (6) + Gap + Right (5) = 11 + Gap.
            // Shrink to 13 cols -> Gap = 2.
            buffer.Resize(13, 24);

            var shrunkRow = GetViewport(buffer)[buffer.CursorRow];
            string shrunkText = GetRowText(shrunkRow);
            _output.WriteLine("=== SHRUNK (13) ===");
            _output.WriteLine(shrunkText);

            // Verify shrink worked (Right content preserved)
            Assert.Contains("Right", shrunkText);
            // Verify gap is small (total len 13, Left 6, right 5 -> Gap 2)

            // 2. Grow back to 50
            buffer.Resize(50, 24);

            var grownRow = GetViewport(buffer)[buffer.CursorRow];
            string grownText = GetRowText(grownRow);
            _output.WriteLine("=== GROWN (50) ===");
            _output.WriteLine(grownText);

            // Assert: Right content should be at the far right (col 45)
            // If it stuck, it would be at col ~8 (after existing gap of 2)

            int rightIndex = grownText.IndexOf("Right");
            Assert.True(rightIndex > 30, $"Right prompt did not move back! Found at index {rightIndex}, expected > 30");
        }
    }
}

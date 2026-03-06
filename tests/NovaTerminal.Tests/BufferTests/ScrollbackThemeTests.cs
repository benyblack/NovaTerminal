using System;
using Xunit;
using NovaTerminal.Core;
using NovaTerminal.Core.Storage;

namespace NovaTerminal.Tests.BufferTests
{
    /// <summary>
    /// Tests for Category 1: Theme-change correctness for paged scrollback.
    /// Verifies that UpdateThemeDefaults correctly updates default-colored cells
    /// while leaving explicitly-colored cells alone.
    /// </summary>
    public class ScrollbackThemeTests
    {
        [Fact]
        public void UpdateThemeDefaults_UpdatesDefaultColoredCells()
        {
            // Arrange: build a scrollback with two types of cells
            var pool = new TerminalPagePool();
            int cols = 10;
            var scrollback = new ScrollbackPages(cols, pool, maxScrollbackBytes: 16L * 1024 * 1024);

            uint oldFg = TermColor.White.ToUint();
            uint newFg = TermColor.Cyan.ToUint();
            uint newBg = TermColor.Red.ToUint();

            // Row 0: default-colored cells (IsDefaultForeground + IsDefaultBackground)
            var defaultRow = new TerminalCell[cols];
            for (int i = 0; i < cols; i++)
            {
                defaultRow[i] = new TerminalCell(' ', TermColor.White, TermColor.Black,
                    isDefaultFg: true, isDefaultBg: true);
            }
            scrollback.AppendRow(defaultRow.AsSpan());

            // Row 1: explicitly-colored cells (IsDefaultForeground = false)
            var explicitRow = new TerminalCell[cols];
            for (int i = 0; i < cols; i++)
            {
                explicitRow[i] = new TerminalCell('X', TermColor.Yellow, TermColor.Green,
                    isDefaultFg: false, isDefaultBg: false);
            }
            scrollback.AppendRow(explicitRow.AsSpan());

            Assert.Equal(2, scrollback.Count);

            // Capture initial explicit values
            uint explicitFg = scrollback.GetRow(1)[0].Fg;
            uint explicitBg = scrollback.GetRow(1)[0].Bg;

            // Act: simulate a theme change from White/Black -> Cyan/Red
            var oldTheme = new TerminalTheme { Foreground = TermColor.White, Background = TermColor.Black };
            var newTheme = new TerminalTheme { Foreground = TermColor.Cyan, Background = TermColor.Red };
            scrollback.UpdateThemeDefaults(oldTheme, newTheme);

            // Assert: default row should be updated to new theme colors
            var updatedDefaultRow = scrollback.GetRow(0);
            Assert.Equal(newFg, updatedDefaultRow[0].Fg);
            Assert.Equal(newBg, updatedDefaultRow[0].Bg);

            // Assert: explicitly colored row should NOT be changed
            var updatedExplicitRow = scrollback.GetRow(1);
            Assert.Equal(explicitFg, updatedExplicitRow[0].Fg); // Yellow preserved
            Assert.Equal(explicitBg, updatedExplicitRow[0].Bg); // Green preserved

            pool.Clear();
        }
    }
}

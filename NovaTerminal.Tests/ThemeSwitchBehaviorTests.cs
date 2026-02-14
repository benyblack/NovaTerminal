using NovaTerminal.Core;
using Xunit;

namespace NovaTerminal.Tests
{
    public class ThemeSwitchBehaviorTests
    {
        [Fact]
        public void UpdateThemeColors_DefaultFlagsRemainAcrossMultipleThemeSwitches()
        {
            var themeA = new TerminalTheme { Name = "A", Foreground = TermColor.FromRgb(10, 10, 10), Background = TermColor.FromRgb(20, 20, 20) };
            var themeB = new TerminalTheme { Name = "B", Foreground = TermColor.FromRgb(200, 200, 200), Background = TermColor.FromRgb(30, 30, 30) };
            var themeC = new TerminalTheme { Name = "C", Foreground = TermColor.FromRgb(120, 220, 120), Background = TermColor.FromRgb(15, 15, 15) };

            var buffer = new TerminalBuffer(20, 5) { Theme = themeA };
            buffer.Write("hello");

            var before = ReadCell(buffer, 0, 0);
            Assert.True(before.IsDefaultForeground);
            Assert.True(before.IsDefaultBackground);

            buffer.Theme = themeB;
            buffer.UpdateThemeColors(themeA);
            var afterFirst = ReadCell(buffer, 0, 0);
            Assert.True(afterFirst.IsDefaultForeground);
            Assert.True(afterFirst.IsDefaultBackground);

            buffer.Theme = themeC;
            buffer.UpdateThemeColors(themeB);
            var afterSecond = ReadCell(buffer, 0, 0);
            Assert.True(afterSecond.IsDefaultForeground);
            Assert.True(afterSecond.IsDefaultBackground);
        }

        [Fact]
        public void UpdateThemeColors_PaletteIndexesRemainAcrossMultipleThemeSwitches()
        {
            var themeA = new TerminalTheme { Name = "A" };
            var themeB = new TerminalTheme { Name = "B" };
            var themeC = new TerminalTheme { Name = "C" };

            var buffer = new TerminalBuffer(20, 5) { Theme = themeA };
            var parser = new AnsiParser(buffer);
            parser.Process("\x1b[31mR\x1b[0m");

            var before = ReadCell(buffer, 0, 0);
            Assert.Equal(1, before.FgIndex);
            Assert.True(before.IsPaletteForeground);

            buffer.Theme = themeB;
            buffer.UpdateThemeColors(themeA);
            var afterFirst = ReadCell(buffer, 0, 0);
            Assert.Equal(1, afterFirst.FgIndex);
            Assert.True(afterFirst.IsPaletteForeground);

            buffer.Theme = themeC;
            buffer.UpdateThemeColors(themeB);
            var afterSecond = ReadCell(buffer, 0, 0);
            Assert.Equal(1, afterSecond.FgIndex);
            Assert.True(afterSecond.IsPaletteForeground);
        }

        private static TerminalCell ReadCell(TerminalBuffer buffer, int col, int row)
        {
            buffer.Lock.EnterReadLock();
            try
            {
                return buffer.GetCellAbsolute(col, row);
            }
            finally
            {
                buffer.Lock.ExitReadLock();
            }
        }
    }
}

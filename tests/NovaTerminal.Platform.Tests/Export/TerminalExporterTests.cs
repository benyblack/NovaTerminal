using System.Threading.Tasks;
using Xunit;
using NovaTerminal.Platform;
using NovaTerminal.VT.Export;
using NovaTerminal.VT;

namespace NovaTerminal.Platform.Tests.Export
{
    public class TerminalExporterTests
    {
        [Fact]
        public void ExportToPlainText_ShouldExtractBasicText()
        {
            var buffer = new TerminalBuffer(80, 24);
            buffer.WriteContent("Hello, NovaTerminal!\r\nLine 2");

            string text = TerminalExporter.ExportToPlainText(buffer);

            Assert.Contains("Hello, NovaTerminal!", text);
            Assert.Contains("Line 2", text);
        }

        [Fact]
        public void ExportToAnsi_ShouldIncludeSgrSequences()
        {
            var buffer = new TerminalBuffer(80, 24);
            // Write formatted text: Bold Red
            buffer.WriteContent("\x1b[1;31mError:\x1b[0m Something went wrong.");

            string ansi = TerminalExporter.ExportToAnsi(buffer);

            Assert.Contains("\x1b[1", ansi); // Should have bold
            Assert.Contains("Error:", ansi);
            Assert.Contains("Something went wrong.", ansi);
        }

        [Fact]
        public void ExportToAnsi_ShouldResetAttributesAtLineEnd()
        {
            var buffer = new TerminalBuffer(80, 24);
            buffer.ViewportRows[0].Cells[0] = new TerminalCell('B', 0, 0, (ushort)TerminalCellFlags.Bold | (ushort)TerminalCellFlags.DefaultForeground | (ushort)TerminalCellFlags.DefaultBackground);
            buffer.ViewportRows[0].Cells[1] = new TerminalCell('o', 0, 0, (ushort)TerminalCellFlags.Bold | (ushort)TerminalCellFlags.DefaultForeground | (ushort)TerminalCellFlags.DefaultBackground);
            buffer.ViewportRows[0].Cells[2] = new TerminalCell('l', 0, 0, (ushort)TerminalCellFlags.Bold | (ushort)TerminalCellFlags.DefaultForeground | (ushort)TerminalCellFlags.DefaultBackground);
            buffer.ViewportRows[0].Cells[3] = new TerminalCell('d', 0, 0, (ushort)TerminalCellFlags.Bold | (ushort)TerminalCellFlags.DefaultForeground | (ushort)TerminalCellFlags.DefaultBackground);

            string ansi = TerminalExporter.ExportToAnsi(buffer);

            // The line must contain a reset at the end, even if colors are default.
            Assert.EndsWith("\x1b[0m", ansi.Split('\n')[0].TrimEnd('\r'));
        }

        [Fact]
        public void ExportToAnsi_ShouldEmitDeltaOnPaletteToRgbSwap()
        {
            var buffer = new TerminalBuffer(80, 24);

            // Palette Red (Index 1) -> Truecolor Red (rgb 255,0,0) -> same 1 value but different flags
            var paletteRed = new TerminalCell('A', 1, 0, (ushort)TerminalCellFlags.PaletteForeground | (ushort)TerminalCellFlags.DefaultBackground);
            ushort trueColorFlags = (ushort)TerminalCellFlags.DefaultBackground; // NOT PaletteForeground
            uint trueColorRedInt = new TermColor(255, 0, 0).ToUint();
            var truecolorRed = new TerminalCell('B', trueColorRedInt, 0, trueColorFlags);

            buffer.ViewportRows[0].Cells[0] = paletteRed;
            buffer.ViewportRows[0].Cells[1] = truecolorRed;

            string ansi = TerminalExporter.ExportToAnsi(buffer);

            // Verify both "A" and "B" have explicit SGR colors defined before them.
            Assert.Contains("\x1b[31mA", ansi);
            Assert.Contains("\x1b[38;2;255;0;0mB", ansi);
        }

        [Fact]
        public void GetLastNonEmptyRowText_ReturnsBottomMostNonEmptyRow()
        {
            var buffer = new TerminalBuffer(80, 24);
            WriteRow(buffer, 0, "alpha");
            WriteRow(buffer, 1, "beta");

            string text = TerminalExporter.GetLastNonEmptyRowText(buffer);

            Assert.Equal("beta", text);
        }

        [Fact]
        public void GetLastNonEmptyRowText_EmptyScreen_ReturnsEmptyString()
        {
            var buffer = new TerminalBuffer(80, 24);

            string text = TerminalExporter.GetLastNonEmptyRowText(buffer);

            Assert.Equal(string.Empty, text);
        }

        [Fact]
        public void GetLastNonEmptyRowText_TrimsTrailingWhitespace()
        {
            var buffer = new TerminalBuffer(80, 24);
            WriteRow(buffer, 0, "hello   ");

            string text = TerminalExporter.GetLastNonEmptyRowText(buffer);

            Assert.Equal("hello", text);
        }

        // Writes each character of `text` directly into row `row`'s cells, starting at column 0 —
        // mirrors the direct ViewportRows[].Cells[] construction used above for precise per-cell
        // control (buffer.WriteContent doesn't interpret \r/\n as cursor motion, so it can't be
        // used to populate distinct rows).
        private static void WriteRow(TerminalBuffer buffer, int row, string text)
        {
            for (int c = 0; c < text.Length; c++)
            {
                buffer.ViewportRows[row].Cells[c] = new TerminalCell(text[c], 0, 0, (ushort)(TerminalCellFlags.DefaultForeground | TerminalCellFlags.DefaultBackground));
            }
        }
    }
}

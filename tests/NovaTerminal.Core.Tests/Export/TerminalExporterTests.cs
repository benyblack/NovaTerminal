using System.Threading.Tasks;
using Xunit;
using NovaTerminal.Core;
using NovaTerminal.Core.Export;

namespace NovaTerminal.Core.Tests.Export
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
    }
}

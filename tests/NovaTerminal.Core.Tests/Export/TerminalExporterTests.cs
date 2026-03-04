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
    }
}

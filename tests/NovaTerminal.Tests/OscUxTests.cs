using NovaTerminal.Core;
using Xunit;

namespace NovaTerminal.Tests
{
    public class OscUxTests
    {
        [Fact]
        public void Osc7_ReportsWorkingDirectory()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);
            string? cwd = null;
            parser.OnWorkingDirectoryChanged = c => cwd = c;

            parser.Process("\u001b]7;file:///tmp/project\u0007");

            Assert.Equal("/tmp/project", cwd);
        }

        [Fact]
        public void Osc8_Hyperlink_IsAttachedToWrittenCells()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\u001b]8;;https://example.com\u0007abc\u001b]8;;\u0007");

            Assert.Equal("https://example.com", buffer.GetHyperlinkAbsolute(0, 0));
            Assert.Equal("https://example.com", buffer.GetHyperlinkAbsolute(1, 0));
            Assert.Equal("https://example.com", buffer.GetHyperlinkAbsolute(2, 0));
            Assert.Null(buffer.GetHyperlinkAbsolute(3, 0));
        }

        [Fact]
        public void Bell_TriggersEvent()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);
            bool bell = false;
            parser.OnBell = () => bell = true;

            parser.Process("\a");

            Assert.True(bell);
        }

        [Fact]
        public void CsiQ_UpdatesCursorStyleMode()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // CSI 6 SP q -> steady beam
            parser.Process("\u001b[6 q");

            Assert.Equal(CursorStyle.Beam, buffer.Modes.CursorStyle);
            Assert.False(buffer.Modes.IsCursorBlinkEnabled);
        }
    }
}

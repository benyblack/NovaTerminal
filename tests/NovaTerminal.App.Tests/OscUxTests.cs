using System.Collections.Generic;
using NovaTerminal.Shell;
using NovaTerminal.Platform;
using NovaTerminal.VT;
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

        // #265: OpenCode (and vim/nvim) probe OSC 10/11 at startup with a ~1s timeout to
        // detect a dark/light theme. Silence stalled every launch and misdetected the theme;
        // the parser must always answer, using the host-supplied theme colors when set and a
        // sane default otherwise.

        [Fact]
        public void Osc11_QueryWithProvider_ReturnsBackgroundColor_BelTerminated()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer)
            {
                DefaultBackground = new TermColor(0x11, 0x22, 0x33)
            };
            var responses = new List<string>();
            parser.OnResponse = r => responses.Add(r);

            parser.Process("\u001b]11;?\u0007");

            var response = Assert.Single(responses);
            Assert.Equal("\u001b]11;rgb:1111/2222/3333\u001b\\", response);
        }

        [Fact]
        public void Osc10_QueryWithProvider_ReturnsForegroundColor_StTerminated()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer)
            {
                DefaultForeground = new TermColor(0x11, 0x22, 0x33)
            };
            var responses = new List<string>();
            parser.OnResponse = r => responses.Add(r);

            // ST-terminated (ESC backslash) form instead of BEL.
            parser.Process("\u001b]10;?\u001b\\");

            var response = Assert.Single(responses);
            Assert.Equal("\u001b]10;rgb:1111/2222/3333\u001b\\", response);
        }

        [Fact]
        public void Osc10And11_QueryWithoutProvider_StillRespondsWithDefaults()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);
            var responses = new List<string>();
            parser.OnResponse = r => responses.Add(r);

            parser.Process("\u001b]10;?\u0007");
            parser.Process("\u001b]11;?\u0007");

            Assert.Equal(2, responses.Count);
            Assert.Equal("\u001b]10;rgb:c0c0/c0c0/c0c0\u001b\\", responses[0]);
            Assert.Equal("\u001b]11;rgb:0000/0000/0000\u001b\\", responses[1]);
        }

        [Fact]
        public void Osc11_SetForm_IsIgnoredSafely()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer)
            {
                DefaultBackground = new TermColor(0x11, 0x22, 0x33)
            };
            var responses = new List<string>();
            parser.OnResponse = r => responses.Add(r);

            // Set form (not a query): must not crash and must not emit a response.
            parser.Process("\u001b]11;#ff0000\u0007");

            Assert.Empty(responses);

            // Parser state must not be corrupted: a subsequent query still works.
            parser.Process("\u001b]11;?\u0007");
            var response2 = Assert.Single(responses);
            Assert.Equal("\u001b]11;rgb:1111/2222/3333\u001b\\", response2);
        }

        [Fact]
        public void Osc10_ChainedQuery_RespondsForForegroundThenBackground()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer)
            {
                DefaultForeground = new TermColor(0x11, 0x22, 0x33),
                DefaultBackground = new TermColor(0x44, 0x55, 0x66)
            };
            var responses = new List<string>();
            parser.OnResponse = r => responses.Add(r);

            // xterm advances to the next color slot per chained value: "OSC 10;?;?"
            // queries fg (slot 10) then bg (slot 11) and expects two replies.
            parser.Process("\u001b]10;?;?\u0007");

            Assert.Equal(2, responses.Count);
            Assert.Equal("\u001b]10;rgb:1111/2222/3333\u001b\\", responses[0]);
            Assert.Equal("\u001b]11;rgb:4444/5555/6666\u001b\\", responses[1]);
        }

        [Fact]
        public void Osc11_TrailingSemicolon_StillAnswersTheQuery()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer)
            {
                DefaultBackground = new TermColor(0x11, 0x22, 0x33)
            };
            var responses = new List<string>();
            parser.OnResponse = r => responses.Add(r);

            // A trailing ';' is legal and must not swallow the query into silence (#265 follow-up).
            parser.Process("\u001b]11;?;\u0007");

            var response = Assert.Single(responses);
            Assert.Equal("\u001b]11;rgb:1111/2222/3333\u001b\\", response);
        }
    }
}

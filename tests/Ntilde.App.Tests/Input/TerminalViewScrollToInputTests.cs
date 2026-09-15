using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Moq;
using NovaTerminal.Pty;
using NovaTerminal.Shell;
using NovaTerminal.VT;
using Xunit;

namespace NovaTerminal.Tests.Input
{
    /// <summary>
    /// Writing while scrolled up into scrollback must bring the viewport back to the live
    /// input line: the user cannot see what they type otherwise. Only user-originated input
    /// (typed text, keys that reach the PTY, pasted/dropped text) does this. Keys that never
    /// reach the PTY - Command-Assist-owned keys, dead-session Enter, Ctrl+C-as-copy - keep
    /// the scrolled position, and so must every non-writing send (focus reports, mouse
    /// reports), which stay outside <see cref="TerminalView"/> snap path by design.
    /// </summary>
    public class TerminalViewScrollToInputTests
    {
        private sealed class TestTerminalView : TerminalView
        {
            public void RaiseTextInputForTest(string text)
            {
                OnTextInput(new TextInputEventArgs { Text = text });
            }
        }

        private static TerminalBuffer CreateBufferWithScrollback()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);
            for (int i = 1; i <= 40; i++)
            {
                parser.Process($"line {i}\r\n");
            }
            Assert.True(buffer.Scrollback.Count > 0, "test setup: expected scrollback");
            return buffer;
        }

        private static (TestTerminalView View, Mock<ITerminalSession> Session) CreateScrolledUpView(bool processRunning = true)
        {
            var session = new Mock<ITerminalSession>();
            session.SetupGet(x => x.IsProcessRunning).Returns(processRunning);
            var view = new TestTerminalView();
            TerminalBuffer buffer = CreateBufferWithScrollback();
            view.SetBuffer(buffer);
            view.SetSession(session.Object);
            int maxScroll = Math.Max(0, buffer.TotalLines - buffer.Rows);
            view.ScrollOffset = maxScroll;
            Assert.True(view.ScrollOffset > 0, "test setup: expected the view to be scrolled up");
            return (view, session);
        }

        [AvaloniaFact]
        public void HandleKeyDownCore_WhenScrolledUp_TypingTabReturnsToInputLine()
        {
            var (view, session) = CreateScrolledUpView();

            bool handled = view.HandleKeyDownCore(Key.Tab, KeyModifiers.None);

            Assert.True(handled);
            Assert.Equal(0, view.ScrollOffset);
            session.Verify(x => x.SendInput("\t"), Times.Once);
        }

        [AvaloniaFact]
        public void HandleKeyDownCore_WhenScrolledUp_EnterReturnsToInputLine()
        {
            var (view, session) = CreateScrolledUpView();

            bool handled = view.HandleKeyDownCore(Key.Enter, KeyModifiers.None);

            Assert.True(handled);
            Assert.Equal(0, view.ScrollOffset);
            session.Verify(x => x.SendInput("\r"), Times.Once);
        }

        [AvaloniaFact]
        public void HandleKeyDownCore_WhenScrolledUp_ArrowKeyReturnsToInputLine()
        {
            // Arrow keys edit the (invisible) command line through the shell, so they count
            // as writing just like printable text does.
            var (view, session) = CreateScrolledUpView();

            bool handled = view.HandleKeyDownCore(Key.Up, KeyModifiers.None);

            Assert.True(handled);
            Assert.Equal(0, view.ScrollOffset);
            session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Once);
        }

        [AvaloniaFact]
        public void TextInput_WhenScrolledUp_TypedTextReturnsToInputLine()
        {
            var (view, session) = CreateScrolledUpView();

            view.RaiseTextInputForTest("x");

            Assert.Equal(0, view.ScrollOffset);
            session.Verify(x => x.SendInput("x"), Times.Once);
        }

        [AvaloniaFact]
        public void HandleKeyDownCore_WhenScrolledUp_DeadSessionEnterKeepsScrollPosition()
        {
            // Enter on a dead session is reconnect affordance, not terminal input - nothing
            // is being written, so the user's reading position must stay put.
            var (view, session) = CreateScrolledUpView(processRunning: false);

            bool handled = view.HandleKeyDownCore(Key.Enter, KeyModifiers.None);

            Assert.False(handled);
            Assert.True(view.ScrollOffset > 0);
            session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Never);
        }

        [AvaloniaFact]
        public void HandleKeyDownCore_WhenScrolledUp_InterceptorOwnedKeyKeepsScrollPosition()
        {
            // Command Assist owns the key: it never reaches the PTY, so it must not yank the
            // viewport back to the input line.
            var (view, session) = CreateScrolledUpView();
            view.KeyDownInterceptor = (key, modifiers) => key == Key.Tab;

            bool handled = view.HandleKeyDownCore(Key.Tab, KeyModifiers.None);

            Assert.True(handled);
            Assert.True(view.ScrollOffset > 0);
            session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Never);
        }

        [AvaloniaFact]
        public void HandleKeyDownCore_WhenScrolledUp_CopyWithSelectionKeepsScrollPosition()
        {
            // Ctrl+C with a selection copies instead of sending SIGINT - reading and copying
            // scrollback must not snap the viewport away from what is being read.
            var (view, session) = CreateScrolledUpView();
            view.SetSelectionForTest(0, 0, 0, 3);

            bool handled = view.HandleKeyDownCore(Key.C, KeyModifiers.Control);

            Assert.True(handled);
            Assert.True(view.ScrollOffset > 0);
            session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Never);
        }

        [AvaloniaFact]
        public void ScrollToInputLine_WhenAlreadyAtBottom_IsNoOp()
        {
            var buffer = new TerminalBuffer(80, 24);
            var view = new TerminalView();
            view.SetBuffer(buffer);

            view.ScrollToInputLine();

            Assert.Equal(0, view.ScrollOffset);
        }
    }
}

using Avalonia.Headless.XUnit;
using Moq;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Input
{
    /// <summary>
    /// Smooth wheel scrolling eases the viewport toward a target over several frames. The
    /// target must survive its own animation: every line the wheel asked for has to arrive,
    /// and output landing mid-gesture (a TUI redrawing its spinner) must not snap the viewport
    /// back to the live line while the gesture is still in flight. Only an external seek -
    /// typing, the scrollbar, search - cancels the animation.
    ///
    /// These drive the internal seams <see cref="TerminalView.ScrollByWheelLines"/> (what the
    /// wheel handler calls once a notch's lines are accumulated) and
    /// <see cref="TerminalView.AdvanceScrollAnimation"/> (what the 16 ms timer tick calls), so
    /// the easing under test is the one that ships.
    /// </summary>
    public class TerminalViewSmoothScrollTests
    {
        private const int WheelLinesPerNotch = 3;

        private static (TerminalView View, TerminalBuffer Buffer) CreateViewAtBottom()
        {
            var session = new Mock<ITerminalSession>();
            session.SetupGet(x => x.IsProcessRunning).Returns(true);
            var view = new TerminalView();
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);
            for (int i = 1; i <= 60; i++)
            {
                parser.Process($"line {i}\r\n");
            }
            Assert.True(buffer.TotalLines - buffer.Rows >= 12, "test setup: expected ample scrollback");
            view.SetBuffer(buffer);
            view.SetSession(session.Object);
            Assert.Equal(0, view.ScrollOffset);
            return (view, buffer);
        }

        private static void RunAnimationToCompletion(TerminalView view)
        {
            // Generous bound: the easing moves at least one line per frame.
            for (int frame = 0; frame < 200; frame++)
            {
                if (!view.AdvanceScrollAnimation()) return;
            }
            Assert.Fail("smooth-scroll animation did not settle");
        }

        [AvaloniaFact]
        public void ScrollByWheelLines_OneNotch_AnimationDeliversEveryLine()
        {
            var (view, _) = CreateViewAtBottom();

            view.ScrollByWheelLines(WheelLinesPerNotch);
            RunAnimationToCompletion(view);

            Assert.Equal(WheelLinesPerNotch, view.ScrollOffset);
        }

        [AvaloniaFact]
        public void ScrollByWheelLines_SecondNotchMidAnimation_AccumulatesOntoTarget()
        {
            var (view, _) = CreateViewAtBottom();

            view.ScrollByWheelLines(WheelLinesPerNotch);
            Assert.True(view.AdvanceScrollAnimation(), "first frame should leave the gesture in flight");
            view.ScrollByWheelLines(WheelLinesPerNotch);
            RunAnimationToCompletion(view);

            Assert.Equal(2 * WheelLinesPerNotch, view.ScrollOffset);
        }

        [AvaloniaFact]
        public void EnsureCursorVisible_WhileWheelAnimationInFlight_DoesNotSnapToBottom()
        {
            var (view, _) = CreateViewAtBottom();

            view.ScrollByWheelLines(WheelLinesPerNotch);
            Assert.True(view.AdvanceScrollAnimation(), "first frame should leave the gesture in flight");
            Assert.InRange(view.ScrollOffset, 1, 2); // inside the follow-output band

            // Output arrives mid-gesture (the pane calls this after every PTY chunk).
            view.EnsureCursorVisible();
            RunAnimationToCompletion(view);

            Assert.Equal(WheelLinesPerNotch, view.ScrollOffset);
        }

        [AvaloniaFact]
        public void EnsureCursorVisible_WithNoGestureInFlightNearBottom_StillFollowsOutput()
        {
            var (view, _) = CreateViewAtBottom();
            view.ScrollOffset = 2;

            view.EnsureCursorVisible();

            Assert.Equal(0, view.ScrollOffset);
            Assert.False(view.AdvanceScrollAnimation(), "no animation should be pending after a snap");
        }

        [AvaloniaFact]
        public void ScrollToInputLine_WhileWheelAnimationInFlight_CancelsTheGesture()
        {
            var (view, _) = CreateViewAtBottom();

            view.ScrollByWheelLines(3 * WheelLinesPerNotch);
            Assert.True(view.AdvanceScrollAnimation(), "first frame should leave the gesture in flight");

            view.ScrollToInputLine();

            Assert.Equal(0, view.ScrollOffset);
            Assert.False(view.AdvanceScrollAnimation(), "an external seek must retire the wheel target");
            Assert.Equal(0, view.ScrollOffset);
        }
    }
}

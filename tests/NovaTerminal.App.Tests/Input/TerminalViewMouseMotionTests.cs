using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Moq;
using NovaTerminal.Platform;
using NovaTerminal.Pty;
using NovaTerminal.Shell;
using NovaTerminal.VT;
using Xunit;

namespace NovaTerminal.Tests.Input
{
    /// <summary>
    /// Issue #269: `?1003` any-event mouse tracking must report pointer motion even when no
    /// button is held (hover-driven TUIs like ratatui/bubbletea rely on this), coalesced to at
    /// most one report per distinct cell so a stationary-but-jittery pointer doesn't flood the
    /// PTY. `?1002` button-event tracking must keep reporting motion only while a button is
    /// held.
    ///
    /// These exercise the internal seams <c>OnPointerMoved</c>/<c>OnPointerPressed</c> delegate
    /// to - <see cref="TerminalView.HandleMouseMoveAt"/> (position in, wire bytes out),
    /// <see cref="TerminalView.HandleMouseMoveCore"/> (already-converted cell in) and
    /// <see cref="TerminalView.HandleMousePressAt"/> - mirroring the existing
    /// <c>HandleKeyDownCore</c> testing pattern used for keyboard input. The <c>*At</c>/<c>*Core</c>
    /// position overloads are the production call path, not a copy of it, so the coordinate
    /// conversion under test is the one that ships.
    /// </summary>
    public class TerminalViewMouseMotionTests
    {
        // Deterministic cell metrics so a pointer position maps to a known cell without
        // depending on headless font measurement.
        private const float CellWidth = 8f;
        private const float CellHeight = 16f;

        /// <summary>View-local position of the (0-based) cell centre for the given grid cell.</summary>
        private static Point PositionOf(int visualRow, int column) =>
            new Point((column * CellWidth) + (CellWidth / 2), (visualRow * CellHeight) + (CellHeight / 2));

        private static (TerminalView View, Mock<ITerminalSession> Session, TerminalBuffer Buffer) CreateView()
        {
            var session = new Mock<ITerminalSession>();
            var view = new TerminalView();
            var buffer = new TerminalBuffer(80, 24);
            view.SetBuffer(buffer);
            view.SetSession(session.Object);
            view.SetMetricsForTest(CellWidth, CellHeight);
            return (view, session, buffer);
        }

        [AvaloniaFact]
        public void HandleMouseMoveCore_AnyEventWithSgr_ReportsHoverMotionAcrossCells()
        {
            var (view, session, buffer) = CreateView();
            buffer.Modes.MouseModeAnyEvent = true;
            buffer.Modes.MouseModeSGR = true;

            view.HandleMouseMoveCore(TerminalMouseButton.None, column: 5, row: 10, KeyModifiers.None);
            view.HandleMouseMoveCore(TerminalMouseButton.None, column: 6, row: 10, KeyModifiers.None);

            session.Verify(x => x.SendInput("\x1b[<35;5;10M"), Times.Once);
            session.Verify(x => x.SendInput("\x1b[<35;6;10M"), Times.Once);
            session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Exactly(2));
        }

        [AvaloniaFact]
        public void HandleMouseMoveCore_SameCellRepeated_EmitsOnlyOneReport()
        {
            var (view, session, buffer) = CreateView();
            buffer.Modes.MouseModeAnyEvent = true;
            buffer.Modes.MouseModeSGR = true;

            view.HandleMouseMoveCore(TerminalMouseButton.None, column: 5, row: 10, KeyModifiers.None);
            view.HandleMouseMoveCore(TerminalMouseButton.None, column: 5, row: 10, KeyModifiers.None);
            view.HandleMouseMoveCore(TerminalMouseButton.None, column: 5, row: 10, KeyModifiers.None);

            session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Once);
            session.Verify(x => x.SendInput("\x1b[<35;5;10M"), Times.Once);
        }

        [AvaloniaFact]
        public void HandleMouseMoveCore_AnyEventWithoutSgr_UsesLegacyEncoding()
        {
            var (view, session, buffer) = CreateView();
            buffer.Modes.MouseModeAnyEvent = true;

            // Legacy encoding: CSI M <32+buttonCode> <32+col> <32+row>, buttonCode = 35 (no
            // button motion) for column=5, row=10 -> chars 32+35=67='C', 32+5=37='%', 32+10=42='*'.
            view.HandleMouseMoveCore(TerminalMouseButton.None, column: 5, row: 10, KeyModifiers.None);

            session.Verify(x => x.SendInput("\x1b[MC%*"), Times.Once);
        }

        [AvaloniaFact]
        public void HandleMouseMoveCore_OnlyButtonEventTracking_UnbuttonedMotionEmitsNothing()
        {
            var (view, session, buffer) = CreateView();
            buffer.Modes.MouseModeButtonEvent = true;
            buffer.Modes.MouseModeSGR = true;

            view.HandleMouseMoveCore(TerminalMouseButton.None, column: 5, row: 10, KeyModifiers.None);

            session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Never);
        }

        [AvaloniaFact]
        public void HandleMouseMoveCore_OnlyButtonEventTracking_ButtonedMotionStillEmits()
        {
            // Regression: ?1002 drag-motion reporting must be unaffected by the ?1003 hover fix.
            var (view, session, buffer) = CreateView();
            buffer.Modes.MouseModeButtonEvent = true;
            buffer.Modes.MouseModeSGR = true;

            view.HandleMouseMoveCore(TerminalMouseButton.Left, column: 5, row: 10, KeyModifiers.None);

            session.Verify(x => x.SendInput("\x1b[<32;5;10M"), Times.Once);
        }

        [AvaloniaFact]
        public void HandleMouseMoveCore_NeitherModeSet_EmitsNothing()
        {
            var (view, session, buffer) = CreateView();

            view.HandleMouseMoveCore(TerminalMouseButton.None, column: 5, row: 10, KeyModifiers.None);
            view.HandleMouseMoveCore(TerminalMouseButton.Left, column: 5, row: 10, KeyModifiers.None);

            session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Never);
        }

        [AvaloniaFact]
        public void HandleMouseMoveCore_CtrlHeldDuringHover_AddsCtrlModifierBits()
        {
            // Ctrl adds 16 to the base button code per xterm, matching the button-press path
            // (TerminalInputModeEncoder.GetModifierBits) so hover and click agree.
            var (view, session, buffer) = CreateView();
            buffer.Modes.MouseModeAnyEvent = true;
            buffer.Modes.MouseModeSGR = true;

            view.HandleMouseMoveCore(TerminalMouseButton.None, column: 5, row: 10, KeyModifiers.Control);

            session.Verify(x => x.SendInput("\x1b[<51;5;10M"), Times.Once);
        }

        [AvaloniaFact]
        public void HandleMouseMoveCore_ReenteringSameCellAfterExit_ReReports()
        {
            // OnPointerExited calls ResetMouseMotionTracking(); exercised directly here since
            // constructing a real Avalonia pointer-exited event headlessly isn't practical.
            var (view, session, buffer) = CreateView();
            buffer.Modes.MouseModeAnyEvent = true;
            buffer.Modes.MouseModeSGR = true;

            view.HandleMouseMoveCore(TerminalMouseButton.None, column: 5, row: 10, KeyModifiers.None);
            view.ResetMouseMotionTracking();
            view.HandleMouseMoveCore(TerminalMouseButton.None, column: 5, row: 10, KeyModifiers.None);

            session.Verify(x => x.SendInput("\x1b[<35;5;10M"), Times.Exactly(2));
        }

        [AvaloniaFact]
        public void HandleMouseMoveCore_ModeToggledOffAndOn_ReReportsSameCell()
        {
            var (view, session, buffer) = CreateView();
            buffer.Modes.MouseModeAnyEvent = true;
            buffer.Modes.MouseModeSGR = true;

            view.HandleMouseMoveCore(TerminalMouseButton.None, column: 5, row: 10, KeyModifiers.None);

            // Application turns hover tracking off, then back on, without the pointer moving.
            // HandleMouseMoveCore itself detects the mode flip and resets tracking - no
            // explicit reset call needed here.
            buffer.Modes.MouseModeAnyEvent = false;
            view.HandleMouseMoveCore(TerminalMouseButton.None, column: 5, row: 10, KeyModifiers.None);
            buffer.Modes.MouseModeAnyEvent = true;
            view.HandleMouseMoveCore(TerminalMouseButton.None, column: 5, row: 10, KeyModifiers.None);

            session.Verify(x => x.SendInput("\x1b[<35;5;10M"), Times.Exactly(2));
        }

        [AvaloniaFact]
        public void HandleMouseMoveCore_ResizeBetweenMoves_ReReportsSameCell()
        {
            // The resize call sites in TerminalView call ResetMouseMotionTracking() right after
            // _buffer.Resize(...); exercised directly here since driving a real layout resize
            // headlessly isn't practical.
            var (view, session, buffer) = CreateView();
            buffer.Modes.MouseModeAnyEvent = true;
            buffer.Modes.MouseModeSGR = true;

            view.HandleMouseMoveCore(TerminalMouseButton.None, column: 5, row: 10, KeyModifiers.None);
            buffer.Resize(100, 30);
            view.ResetMouseMotionTracking();
            view.HandleMouseMoveCore(TerminalMouseButton.None, column: 5, row: 10, KeyModifiers.None);

            session.Verify(x => x.SendInput("\x1b[<35;5;10M"), Times.Exactly(2));
        }

        [AvaloniaFact]
        public void HandleMouseMoveAt_WithScrollback_ReportsViewportRelativeRowNotAbsoluteRow()
        {
            // Regression (issue #269 review): xterm mouse coordinates are viewport-relative and
            // 1-based. The motion path used to forward ScreenToTerminal's scrollback-ABSOLUTE row,
            // so hovering the last visible line of an 80x24 screen with 500 lines of scrollback
            // reported row 524 instead of 24 (and, on the legacy encoding, blew past the
            // 223-coordinate ceiling). Only the alt screen was accidentally correct.
            var (view, session, buffer) = CreateView();
            buffer.Modes.MouseModeAnyEvent = true;
            buffer.Modes.MouseModeSGR = true;

            for (int i = 0; i < 500; i++)
            {
                buffer.Write($"scrollback line {i}\n");
            }

            Assert.True(buffer.Scrollback.Count > 0, "test needs non-empty scrollback to be meaningful");
            Assert.True(buffer.TotalLines > buffer.Rows);

            // Bottom-right-ish cell: last visible row (visual row 23) of an 80x24 screen.
            view.HandleMouseMoveAt(PositionOf(visualRow: 23, column: 4), TerminalMouseButton.None, KeyModifiers.None);

            session.Verify(x => x.SendInput("\x1b[<35;5;24M"), Times.Once);
            // No other report: pins that nothing absolute-rowed (e.g. ";524M") went out.
            session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Once);
        }

        [AvaloniaFact]
        public void HandleMouseMoveAt_ScrollbackDepthDoesNotChangeReportedRow()
        {
            // The dedup key is the reported cell, so a scrollback-absolute row would drift under a
            // physically stationary pointer as output scrolled - one report per line of output
            // instead of one per cell. Same position before and after 500 lines of output must
            // produce the same coordinates (and therefore coalesce).
            var (view, session, buffer) = CreateView();
            buffer.Modes.MouseModeAnyEvent = true;
            buffer.Modes.MouseModeSGR = true;

            var stationary = PositionOf(visualRow: 9, column: 4);
            view.HandleMouseMoveAt(stationary, TerminalMouseButton.None, KeyModifiers.None);

            for (int i = 0; i < 500; i++)
            {
                buffer.Write($"scrollback line {i}\n");
            }

            view.HandleMouseMoveAt(stationary, TerminalMouseButton.None, KeyModifiers.None);

            session.Verify(x => x.SendInput("\x1b[<35;5;10M"), Times.Once);
            session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Once);
        }

        [AvaloniaFact]
        public void HandleMousePressAt_WithScrollback_ReportsViewportRelativeRowMatchingMotion()
        {
            // Press/release/wheel and motion must agree on the coordinate space, or an app sees a
            // click land on a different row than the hover that preceded it.
            var (view, session, buffer) = CreateView();
            buffer.Modes.MouseModeAnyEvent = true;
            buffer.Modes.MouseModeSGR = true;

            for (int i = 0; i < 500; i++)
            {
                buffer.Write($"scrollback line {i}\n");
            }

            var position = PositionOf(visualRow: 23, column: 4);
            view.HandleMousePressAt(position, TerminalMouseButton.Left, KeyModifiers.None);
            view.HandleMouseMoveAt(position, TerminalMouseButton.None, KeyModifiers.None);

            // Left press = button code 0, hover motion = 35; both at viewport row 24, column 5.
            session.Verify(x => x.SendInput("\x1b[<0;5;24M"), Times.Once);
            session.Verify(x => x.SendInput("\x1b[<35;5;24M"), Times.Once);
            session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Exactly(2));
        }

        [AvaloniaFact]
        public void ScreenSwitch_ClearsMotionTracking_SoIncomingAppGetsFirstHover()
        {
            // Issue #269 review: ?1049 (and ?47/?1047) replaces every cell under a stationary
            // pointer. Drives the REAL reset call site - TerminalView.OnScreenSwitched, reached
            // through TerminalBuffer's OnScreenSwitched event - rather than calling Reset directly.
            var (view, session, buffer) = CreateView();
            var parser = new AnsiParser(buffer);
            buffer.Modes.MouseModeAnyEvent = true;
            buffer.Modes.MouseModeSGR = true;

            var stationary = PositionOf(visualRow: 9, column: 4);
            view.HandleMouseMoveAt(stationary, TerminalMouseButton.None, KeyModifiers.None);

            parser.Process("\x1b[?1049h");

            // Screen switching does not touch the mouse modes, so the mode-flip sampling in
            // HandleMouseMoveCore cannot be what re-reports below - it has to be the reset.
            Assert.True(buffer.Modes.MouseModeAnyEvent);

            view.HandleMouseMoveAt(stationary, TerminalMouseButton.None, KeyModifiers.None);

            session.Verify(x => x.SendInput("\x1b[<35;5;10M"), Times.Exactly(2));
        }

        [AvaloniaFact]
        public void SetBuffer_ClearsMotionTracking_SoNextHoverInSameCellReports()
        {
            // Verifies a real reset call site (public SetBuffer) rather than ResetMouseMotionTracking
            // in isolation. Both buffers carry the same mouse modes, so the mode-flip sampling
            // cannot account for the second report.
            var (view, session, buffer) = CreateView();
            buffer.Modes.MouseModeAnyEvent = true;
            buffer.Modes.MouseModeSGR = true;

            var stationary = PositionOf(visualRow: 9, column: 4);
            view.HandleMouseMoveAt(stationary, TerminalMouseButton.None, KeyModifiers.None);

            var replacement = new TerminalBuffer(80, 24);
            replacement.Modes.MouseModeAnyEvent = true;
            replacement.Modes.MouseModeSGR = true;
            view.SetBuffer(replacement);
            view.SetMetricsForTest(CellWidth, CellHeight);

            view.HandleMouseMoveAt(stationary, TerminalMouseButton.None, KeyModifiers.None);

            session.Verify(x => x.SendInput("\x1b[<35;5;10M"), Times.Exactly(2));
        }

        [AvaloniaFact]
        public void HandleMouseMoveCore_WithoutSession_DoesNotRecordCell()
        {
            // Issue #269 review finding 7: a move that lands between SetBuffer and SetSession sends
            // nothing, so it must not be remembered - otherwise the first real hover in that cell
            // after the session attaches is suppressed as a duplicate.
            var session = new Mock<ITerminalSession>();
            var view = new TerminalView();
            var buffer = new TerminalBuffer(80, 24);
            buffer.Modes.MouseModeAnyEvent = true;
            buffer.Modes.MouseModeSGR = true;
            view.SetBuffer(buffer);
            view.SetMetricsForTest(CellWidth, CellHeight);

            // No session attached yet: nothing can be sent.
            view.HandleMouseMoveCore(TerminalMouseButton.None, column: 5, row: 10, KeyModifiers.None);

            view.SetSession(session.Object);
            view.HandleMouseMoveCore(TerminalMouseButton.None, column: 5, row: 10, KeyModifiers.None);

            session.Verify(x => x.SendInput("\x1b[<35;5;10M"), Times.Once);
        }
    }
}

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
    /// These exercise <see cref="TerminalView.HandleMouseMoveCore"/> directly - the internal
    /// seam <see cref="TerminalView.OnPointerMoved"/> delegates to - because driving a real
    /// Avalonia <c>PointerEventArgs</c> headlessly is not practical (its device/point plumbing
    /// is not constructible from test code). This mirrors the existing
    /// <c>HandleKeyDownCore</c> testing pattern used for keyboard input.
    /// </summary>
    public class TerminalViewMouseMotionTests
    {
        private static (TerminalView View, Mock<ITerminalSession> Session, TerminalBuffer Buffer) CreateView()
        {
            var session = new Mock<ITerminalSession>();
            var view = new TerminalView();
            var buffer = new TerminalBuffer(80, 24);
            view.SetBuffer(buffer);
            view.SetSession(session.Object);
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
    }
}

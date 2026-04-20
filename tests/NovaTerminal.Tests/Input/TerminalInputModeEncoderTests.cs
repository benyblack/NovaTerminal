using Avalonia.Input;
using NovaTerminal.Core;
using Xunit;

namespace NovaTerminal.Tests.Input
{
    public class TerminalInputModeEncoderTests
    {
        [Fact]
        public void EncodeMouseEvent_ButtonEventTrackingWithSgr_EncodesDragMotion()
        {
            var modes = new ModeState
            {
                MouseModeButtonEvent = true,
                MouseModeSGR = true
            };

            string? sequence = TerminalInputModeEncoder.EncodeMouseEvent(
                modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Move, TerminalMouseButton.Left, 12, 7, KeyModifiers.None));

            Assert.Equal("\x1b[<32;12;7M", sequence);
        }

        [Fact]
        public void EncodeMouseEvent_AnyEventWithSgr_EncodesHoverMotion()
        {
            var modes = new ModeState
            {
                MouseModeAnyEvent = true,
                MouseModeSGR = true
            };

            string? sequence = TerminalInputModeEncoder.EncodeMouseEvent(
                modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Move, TerminalMouseButton.None, 9, 4, KeyModifiers.None));

            Assert.Equal("\x1b[<35;9;4M", sequence);
        }

        [Fact]
        public void EncodeMouseEvent_SgrRelease_PreservesReleasedButtonAndModifiers()
        {
            var modes = new ModeState
            {
                MouseModeX10 = true,
                MouseModeSGR = true
            };

            string? sequence = TerminalInputModeEncoder.EncodeMouseEvent(
                modes,
                new TerminalMouseEvent(
                    TerminalMouseEventKind.Release,
                    TerminalMouseButton.Right,
                    3,
                    4,
                    KeyModifiers.Control | KeyModifiers.Shift));

            Assert.Equal("\x1b[<22;3;4m", sequence);
        }

        [Fact]
        public void EncodeMouseEvent_ButtonEventTracking_IgnoresHoverWithoutPressedButton()
        {
            var modes = new ModeState
            {
                MouseModeButtonEvent = true,
                MouseModeSGR = true
            };

            string? sequence = TerminalInputModeEncoder.EncodeMouseEvent(
                modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Move, TerminalMouseButton.None, 9, 4, KeyModifiers.None));

            Assert.Null(sequence);
        }

        [Fact]
        public void EncodeFocusChanged_RequiresFocusReportingMode()
        {
            Assert.Null(TerminalInputModeEncoder.EncodeFocusChanged(new ModeState(), isFocused: true));
            Assert.Equal(
                "\x1b[I",
                TerminalInputModeEncoder.EncodeFocusChanged(
                    new ModeState { IsFocusEventReporting = true },
                    isFocused: true));
            Assert.Equal(
                "\x1b[O",
                TerminalInputModeEncoder.EncodeFocusChanged(
                    new ModeState { IsFocusEventReporting = true },
                    isFocused: false));
        }
    }
}

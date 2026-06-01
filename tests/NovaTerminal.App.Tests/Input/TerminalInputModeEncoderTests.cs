using NovaTerminal.Shell;
using Avalonia.Input;
using NovaTerminal.Platform;
using NovaTerminal.VT;
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
        public void EncodeAltKey_AltLetter_EmitsEscapePrefixedLowercase()
        {
            // xterm "metaSendsEscape": Alt+<letter> sends ESC followed by the character.
            // Claude Code relies on Alt+V (ESC v) as its paste-image trigger.
            // Build ESC via concatenation: "\x1b" is a complete escape (the closing quote
            // ends it), avoiding \x greediness that would fold a trailing hex char into it.
            Assert.Equal("\x1b" + "v", TerminalInputModeEncoder.EncodeAltKey(Key.V, KeyModifiers.Alt));
            Assert.Equal("\x1b" + "b", TerminalInputModeEncoder.EncodeAltKey(Key.B, KeyModifiers.Alt));
        }

        [Fact]
        public void EncodeAltKey_AltShiftLetter_EmitsEscapePrefixedUppercase()
        {
            Assert.Equal("\x1b" + "V", TerminalInputModeEncoder.EncodeAltKey(Key.V, KeyModifiers.Alt | KeyModifiers.Shift));
        }

        [Fact]
        public void EncodeAltKey_AltDigit_EmitsEscapePrefixedDigit()
        {
            Assert.Equal("\x1b" + "5", TerminalInputModeEncoder.EncodeAltKey(Key.D5, KeyModifiers.Alt));
        }

        [Fact]
        public void EncodeAltKey_WithoutAlt_ReturnsNull()
        {
            Assert.Null(TerminalInputModeEncoder.EncodeAltKey(Key.V, KeyModifiers.None));
        }

        [Fact]
        public void EncodeAltKey_CtrlAltCombo_ReturnsNull()
        {
            // Ctrl+Alt is AltGr on many layouts and produces real text input;
            // encoding it here would double-handle the key.
            Assert.Null(TerminalInputModeEncoder.EncodeAltKey(Key.V, KeyModifiers.Alt | KeyModifiers.Control));
        }

        [Fact]
        public void EncodeAltKey_NonPrintableKey_ReturnsNull()
        {
            Assert.Null(TerminalInputModeEncoder.EncodeAltKey(Key.Up, KeyModifiers.Alt));
            Assert.Null(TerminalInputModeEncoder.EncodeAltKey(Key.F5, KeyModifiers.Alt));
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

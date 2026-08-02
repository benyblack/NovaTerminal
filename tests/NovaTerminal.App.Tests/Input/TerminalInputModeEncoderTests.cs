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
        public void EncodeMouseEvent_LegacyAtMaxCoordinate_EncodesInOneByteEach()
        {
            // 223 IS representable in the legacy X10 encoding: the byte sent is value+32 and
            // 32+223 = 255, the largest byte. Left press -> button code 0 -> char 32 (space).
            var modes = new ModeState { MouseModeX10 = true };

            string? sequence = TerminalInputModeEncoder.EncodeMouseEvent(
                modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Press, TerminalMouseButton.Left, 223, 223, KeyModifiers.None));

            // Built from char codes rather than a literal so this file stays pure ASCII.
            string expected = "\x1b[M\x20" + (char)255 + (char)255;
            Assert.Equal(expected, sequence);
        }

        [Fact]
        public void EncodeMouseEvent_LegacyBeyondMaxCoordinate_ClampsAndNeverEmitsSgr()
        {
            // Regression: coordinates past 223 used to fall back to the SGR form even when the
            // application never enabled ?1006 - unparseable for a legacy-mode app, which would
            // render it as text or desync. xterm clamps out-of-range coordinates instead of
            // switching protocols.
            var modes = new ModeState { MouseModeX10 = true };

            string? sequence = TerminalInputModeEncoder.EncodeMouseEvent(
                modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Press, TerminalMouseButton.Left, 400, 500, KeyModifiers.None));

            Assert.NotNull(sequence);
            Assert.DoesNotContain("\x1b[<", sequence);
            string expected = "\x1b[M\x20" + (char)255 + (char)255;
            Assert.Equal(expected, sequence);
        }

        [Fact]
        public void EncodeMouseEvent_SgrBeyondLegacyMaxCoordinate_IsNotClamped()
        {
            // The 223 ceiling is a property of the legacy one-byte-per-coordinate encoding only;
            // ?1006 sends decimal parameters and must carry the real coordinates.
            var modes = new ModeState { MouseModeX10 = true, MouseModeSGR = true };

            string? sequence = TerminalInputModeEncoder.EncodeMouseEvent(
                modes,
                new TerminalMouseEvent(TerminalMouseEventKind.Press, TerminalMouseButton.Left, 400, 500, KeyModifiers.None));

            Assert.Equal("\x1b[<0;400;500M", sequence);
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
        public void EncodeAltKey_AltBackspace_EmitsEscapeThenDelete()
        {
            // readline backward-kill-word (M-DEL): ESC followed by DEL (0x7f).
            Assert.Equal("\x1b\x7f", TerminalInputModeEncoder.EncodeAltKey(Key.Back, KeyModifiers.Alt));
        }

        [Fact]
        public void EncodeAltKey_AltEnter_EmitsEscapeThenCarriageReturn()
        {
            Assert.Equal("\x1b\r", TerminalInputModeEncoder.EncodeAltKey(Key.Enter, KeyModifiers.Alt));
        }

        [Fact]
        public void EncodeAltKey_AltPeriod_EmitsEscapeThenPeriod()
        {
            // readline yank-last-arg (M-.)
            Assert.Equal("\x1b.", TerminalInputModeEncoder.EncodeAltKey(Key.OemPeriod, KeyModifiers.Alt));
        }

        [Fact]
        public void EncodeAltKey_AltShiftPeriod_ReturnsNull()
        {
            // Shifted OemPeriod is '>' on most layouts, not '.'; don't mis-encode it.
            Assert.Null(TerminalInputModeEncoder.EncodeAltKey(Key.OemPeriod, KeyModifiers.Alt | KeyModifiers.Shift));
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

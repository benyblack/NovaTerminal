using Avalonia.Input;
using NovaTerminal.Shell;
using NovaTerminal.VT;
using Xunit;

namespace NovaTerminal.Tests.Input
{
    // Issue #266: kitty keyboard protocol key encoding, disambiguate tier (flag 0b1).
    // Spec: https://sw.kovidgoyal.net/kitty/keyboard-protocol/#disambiguate-escape-codes
    public class KittyKeyboardEncodingTests
    {
        private static ModeState LegacyModes() => new ModeState();

        private static ModeState DisambiguateModes()
        {
            var modes = new ModeState();
            modes.KittyKeyboard.Push(KittyKeyboardState.FlagDisambiguateEscapeCodes);
            return modes;
        }

        [Theory]
        [InlineData(Key.Enter, KeyModifiers.None)]
        [InlineData(Key.Enter, KeyModifiers.Shift)]
        [InlineData(Key.Enter, KeyModifiers.Control)]
        [InlineData(Key.Escape, KeyModifiers.None)]
        [InlineData(Key.Tab, KeyModifiers.None)]
        [InlineData(Key.Tab, KeyModifiers.Shift)]
        [InlineData(Key.Back, KeyModifiers.None)]
        [InlineData(Key.Back, KeyModifiers.Control)]
        [InlineData(Key.I, KeyModifiers.Control)]
        [InlineData(Key.C, KeyModifiers.Control)]
        [InlineData(Key.V, KeyModifiers.Alt)]
        [InlineData(Key.Space, KeyModifiers.Control)]
        [InlineData(Key.A, KeyModifiers.Control | KeyModifiers.Shift)]
        public void ProtocolDisabled_EveryKeyFallsThroughToLegacyEncoding(Key key, KeyModifiers modifiers)
        {
            Assert.Null(TerminalInputModeEncoder.EncodeKittyKey(key, modifiers, LegacyModes()));
        }

        [Fact]
        public void NullModeState_FallsThroughToLegacyEncoding()
        {
            Assert.Null(TerminalInputModeEncoder.EncodeKittyKey(Key.Escape, KeyModifiers.None, null));
        }

        [Theory]
        // Esc is the headline case: always disambiguated so an application can tell a real
        // Esc keypress from the first byte of an escape sequence. Modifiers omitted when none.
        [InlineData(Key.Escape, KeyModifiers.None, "\x1b[27u")]
        [InlineData(Key.Escape, KeyModifiers.Shift, "\x1b[27;2u")]
        [InlineData(Key.Escape, KeyModifiers.Control, "\x1b[27;5u")]

        // Enter/Tab/Backspace keep their legacy bytes unmodified; modified they take CSI u.
        // Shift+Enter -> CSI 13;2u is the Claude Code "insert a newline" fix.
        [InlineData(Key.Enter, KeyModifiers.Shift, "\x1b[13;2u")]
        [InlineData(Key.Enter, KeyModifiers.Control, "\x1b[13;5u")]
        [InlineData(Key.Enter, KeyModifiers.Alt, "\x1b[13;3u")]
        [InlineData(Key.Tab, KeyModifiers.Shift, "\x1b[9;2u")]
        [InlineData(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift, "\x1b[9;6u")]
        [InlineData(Key.Back, KeyModifiers.Control, "\x1b[127;5u")]
        [InlineData(Key.Back, KeyModifiers.Alt, "\x1b[127;3u")]

        // C0 collisions: Ctrl+I is no longer indistinguishable from Tab, Ctrl+M from Enter,
        // Ctrl+[ from Esc, Ctrl+H from Backspace.
        [InlineData(Key.I, KeyModifiers.Control, "\x1b[105;5u")]
        [InlineData(Key.M, KeyModifiers.Control, "\x1b[109;5u")]
        [InlineData(Key.H, KeyModifiers.Control, "\x1b[104;5u")]
        [InlineData(Key.OemOpenBrackets, KeyModifiers.Control, "\x1b[91;5u")]
        [InlineData(Key.C, KeyModifiers.Control, "\x1b[99;5u")]

        // The reported codepoint is always the unshifted key, never the shifted glyph.
        [InlineData(Key.A, KeyModifiers.Control | KeyModifiers.Shift, "\x1b[97;6u")]
        [InlineData(Key.D3, KeyModifiers.Control | KeyModifiers.Shift, "\x1b[51;6u")]

        // Alt combinations lose their ESC prefix for keys the protocol claims.
        [InlineData(Key.V, KeyModifiers.Alt, "\x1b[118;3u")]
        [InlineData(Key.V, KeyModifiers.Alt | KeyModifiers.Shift, "\x1b[118;4u")]
        [InlineData(Key.V, KeyModifiers.Alt | KeyModifiers.Control, "\x1b[118;7u")]

        // Modifier bit field: shift=1 alt=2 ctrl=4 super=8, transmitted as 1 + bits.
        [InlineData(Key.A, KeyModifiers.Meta, "\x1b[97;9u")]
        [InlineData(Key.A, KeyModifiers.Meta | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, "\x1b[97;16u")]

        // Punctuation and space resolve to their unshifted US-layout codepoints.
        [InlineData(Key.Space, KeyModifiers.Control, "\x1b[32;5u")]
        [InlineData(Key.OemQuestion, KeyModifiers.Control, "\x1b[47;5u")]
        [InlineData(Key.OemMinus, KeyModifiers.Control, "\x1b[45;5u")]
        [InlineData(Key.OemPlus, KeyModifiers.Control, "\x1b[61;5u")]
        [InlineData(Key.OemPeriod, KeyModifiers.Alt, "\x1b[46;3u")]
        public void Disambiguate_EncodesCsiU(Key key, KeyModifiers modifiers, string expected)
        {
            Assert.Equal(expected, TerminalInputModeEncoder.EncodeKittyKey(key, modifiers, DisambiguateModes()));
        }

        [Theory]
        // Unmodified Enter/Tab/Backspace deliberately keep their legacy bytes so a user can
        // still type "reset" after a TUI crashes with the mode left on.
        [InlineData(Key.Enter, KeyModifiers.None)]
        [InlineData(Key.Tab, KeyModifiers.None)]
        [InlineData(Key.Back, KeyModifiers.None)]

        // Text-producing presses stay text: plain and shift-only are not ambiguous.
        [InlineData(Key.A, KeyModifiers.None)]
        [InlineData(Key.A, KeyModifiers.Shift)]
        [InlineData(Key.D3, KeyModifiers.Shift)]
        [InlineData(Key.OemPeriod, KeyModifiers.None)]
        [InlineData(Key.Space, KeyModifiers.None)]

        // Out of scope for this tier in NovaTerminal: keypad and functional keys keep the
        // legacy CSI/SS3 encodings produced by EncodeSpecialKey.
        [InlineData(Key.Up, KeyModifiers.None)]
        [InlineData(Key.Up, KeyModifiers.Control)]
        [InlineData(Key.F5, KeyModifiers.Shift)]
        [InlineData(Key.Home, KeyModifiers.Control)]
        [InlineData(Key.Delete, KeyModifiers.Control)]
        [InlineData(Key.NumPad5, KeyModifiers.Control)]
        public void Disambiguate_LeavesLegacyEncodingsAlone(Key key, KeyModifiers modifiers)
        {
            Assert.Null(TerminalInputModeEncoder.EncodeKittyKey(key, modifiers, DisambiguateModes()));
        }

        [Fact]
        public void UnsupportedFlagsAlone_DoNotEnableEncoding()
        {
            var modes = new ModeState();
            modes.KittyKeyboard.Push(0b11110); // event types + alternates + all keys + text

            Assert.Equal(0, modes.KittyKeyboard.Flags);
            Assert.Null(TerminalInputModeEncoder.EncodeKittyKey(Key.Escape, KeyModifiers.None, modes));
        }

        [Fact]
        public void PoppingTheStack_ReturnsToLegacyEncoding()
        {
            var modes = DisambiguateModes();
            Assert.Equal("\x1b[27u", TerminalInputModeEncoder.EncodeKittyKey(Key.Escape, KeyModifiers.None, modes));

            modes.KittyKeyboard.Pop(1);

            Assert.Null(TerminalInputModeEncoder.EncodeKittyKey(Key.Escape, KeyModifiers.None, modes));
            Assert.Null(TerminalInputModeEncoder.EncodeKittyKey(Key.Enter, KeyModifiers.Shift, modes));
        }

        [Fact]
        public void ActiveScreenSwitch_SwapsWhichStackTheEncoderSees()
        {
            var modes = new ModeState();
            modes.KittyKeyboard.SetActiveScreen(true);
            modes.KittyKeyboard.Push(KittyKeyboardState.FlagDisambiguateEscapeCodes);

            Assert.Equal("\x1b[13;2u", TerminalInputModeEncoder.EncodeKittyKey(Key.Enter, KeyModifiers.Shift, modes));

            modes.KittyKeyboard.SetActiveScreen(false);

            Assert.Null(TerminalInputModeEncoder.EncodeKittyKey(Key.Enter, KeyModifiers.Shift, modes));
        }
    }
}

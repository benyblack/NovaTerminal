using System;
using Avalonia.Input;
using NovaTerminal.VT;

namespace NovaTerminal.Shell
{
    internal enum TerminalMouseButton
    {
        None = -1,
        Left = 0,
        Middle = 1,
        Right = 2,
        WheelUp = 64,
        WheelDown = 65
    }

    internal enum TerminalMouseEventKind
    {
        Press,
        Release,
        Move,
        Wheel
    }

    internal readonly record struct TerminalMouseEvent(
        TerminalMouseEventKind Kind,
        TerminalMouseButton Button,
        int Column,
        int Row,
        KeyModifiers Modifiers);

    internal static class TerminalInputModeEncoder
    {
        public static string? EncodeSpecialKey(Key key, ModeState? modes)
        {
            bool applicationCursorKeys = modes?.IsApplicationCursorKeys == true;

            return key switch
            {
                Key.Up => applicationCursorKeys ? "\x1bOA" : "\x1b[A",
                Key.Down => applicationCursorKeys ? "\x1bOB" : "\x1b[B",
                Key.Right => applicationCursorKeys ? "\x1bOC" : "\x1b[C",
                Key.Left => applicationCursorKeys ? "\x1bOD" : "\x1b[D",
                Key.Home => applicationCursorKeys ? "\x1bOH" : "\x1b[H",
                Key.End => applicationCursorKeys ? "\x1bOF" : "\x1b[F",
                Key.Delete => "\x1b[3~",
                Key.Insert => "\x1b[2~",
                Key.PageUp => "\x1b[5~",
                Key.PageDown => "\x1b[6~",
                Key.F1 => "\x1bOP",
                Key.F2 => "\x1bOQ",
                Key.F3 => "\x1bOR",
                Key.F4 => "\x1bOS",
                Key.F5 => "\x1b[15~",
                Key.F6 => "\x1b[17~",
                Key.F7 => "\x1b[18~",
                Key.F8 => "\x1b[19~",
                Key.F9 => "\x1b[20~",
                Key.F10 => "\x1b[21~",
                Key.F11 => "\x1b[23~",
                Key.F12 => "\x1b[24~",
                _ => null
            };
        }

        // Kitty keyboard protocol modifier bit field (shift=1, alt=2, ctrl=4, super=8).
        // The value transmitted in the escape code is 1 + this bit field.
        private const int KittyShift = 0b1;
        private const int KittyAlt = 0b10;
        private const int KittyCtrl = 0b100;
        private const int KittySuper = 0b1000;

        // Functional key codes from the kitty spec's functional key table. These three keep
        // their C0 byte for legacy compatibility when unmodified.
        private const int KittyKeyTab = 9;
        private const int KittyKeyEnter = 13;
        private const int KittyKeyEscape = 27;
        private const int KittyKeyBackspace = 127;

        /// <summary>
        /// Encodes a key event using the kitty keyboard protocol's <c>CSI number ; modifiers u</c>
        /// form when the disambiguate-escape-codes tier (flag 0b1) is active for the current
        /// screen buffer. Returns <c>null</c> when the protocol is off, or when the key is one the
        /// disambiguate tier deliberately leaves in its legacy encoding - the caller then falls
        /// through to the unchanged legacy paths, so behavior is byte-identical with flags = 0.
        ///
        /// Per spec (https://sw.kovidgoyal.net/kitty/keyboard-protocol/#disambiguate-escape-codes):
        /// - Esc always becomes <c>CSI 27 u</c>, which is the whole point of the tier: it is what
        ///   lets an application tell a real Esc keypress from the start of an escape sequence.
        /// - Enter, Tab and Backspace keep their legacy bytes when unmodified (so a user can still
        ///   type "reset" at a shell prompt after a crashed TUI leaves the mode on), but take the
        ///   CSI u form as soon as any modifier is held. This is the Shift+Enter fix:
        ///   <c>CSI 13;2u</c>.
        /// - "Legacy text" keys (a-z, 0-9, the ASCII punctuation keys and Space) switch to CSI u
        ///   whenever ctrl, alt or super is held, which is what disambiguates Ctrl+I from Tab and
        ///   Ctrl+M from Enter. Plain and shift-only presses still produce text.
        /// - Keypad and functional keys (arrows, F-keys, Home/End/PgUp/PgDn, Insert/Delete) are out
        ///   of scope for this tier in NovaTerminal and keep their legacy encodings.
        ///
        /// The modifiers field is omitted entirely when no modifiers are active, per spec.
        /// </summary>
        public static string? EncodeKittyKey(Key key, KeyModifiers modifiers, ModeState? modes)
        {
            if (modes?.KittyKeyboard.DisambiguateEscapeCodes != true)
            {
                return null;
            }

            // AltGr carve-out: on Windows, Avalonia reports AltGr as Control|Alt (there is no
            // separate "AltGr" modifier in KeyModifiers). On German/French/Spanish/Polish/
            // Nordic/Turkish and other non-US layouts, AltGr+<key> composes real text (e.g.
            // AltGr+Q -> '@' on a German layout) that arrives via OnTextInput/WM_CHAR - but only
            // if KeyDown is left unhandled. If we encode this here, HandleKeyDownCore returns
            // true, OnKeyDown sets e.Handled = true, and the Win32 backend then suppresses the
            // following WM_CHAR, so the composed character is silently lost rather than sent
            // twice. Mirrors the identical guard in EncodeAltKey below. Per spec, "all key
            // events that do NOT generate text" get the CSI u form - an AltGr keypress that
            // produces text does not qualify, so returning null here (falling through to the
            // legacy/text path) is spec-correct, not just a workaround.
            //
            // Trade-off (matches EncodeAltKey, same accepted status quo): on US layouts, a user
            // who deliberately holds literal Ctrl+Alt+<key> as a shortcut also loses kitty
            // encoding for that combination, because there is no way to distinguish "AltGr on a
            // non-US layout" from "Ctrl+Alt held together on a US layout" at this layer without
            // consulting the OS keyboard layout tables. That combination was never encodable by
            // this protocol path before AltGr support existed, so nothing regresses for it.
            if ((modifiers & (KeyModifiers.Control | KeyModifiers.Alt)) == (KeyModifiers.Control | KeyModifiers.Alt))
            {
                return null;
            }

            int modifierBits = GetKittyModifierBits(modifiers);

            switch (key)
            {
                case Key.Escape:
                    return FormatKittyCsiU(KittyKeyEscape, modifierBits);
                case Key.Enter:
                    return modifierBits == 0 ? null : FormatKittyCsiU(KittyKeyEnter, modifierBits);
                case Key.Tab:
                    return modifierBits == 0 ? null : FormatKittyCsiU(KittyKeyTab, modifierBits);
                case Key.Back:
                    return modifierBits == 0 ? null : FormatKittyCsiU(KittyKeyBackspace, modifierBits);
            }

            // Unmodified and shift-only presses of text keys still arrive through OnTextInput
            // as plain UTF-8; only ctrl/alt/super combinations are ambiguous in legacy encoding.
            if ((modifierBits & ~KittyShift) == 0)
            {
                return null;
            }

            int codepoint = GetUnshiftedCodepoint(key);
            return codepoint < 0 ? null : FormatKittyCsiU(codepoint, modifierBits);
        }

        private static int GetKittyModifierBits(KeyModifiers modifiers)
        {
            int bits = 0;
            if ((modifiers & KeyModifiers.Shift) != 0) bits |= KittyShift;
            if ((modifiers & KeyModifiers.Alt) != 0) bits |= KittyAlt;
            if ((modifiers & KeyModifiers.Control) != 0) bits |= KittyCtrl;
            if ((modifiers & KeyModifiers.Meta) != 0) bits |= KittySuper;
            return bits;
        }

        private static string FormatKittyCsiU(int codepoint, int modifierBits)
        {
            string csi = ((char)0x1b) + "[";
            return modifierBits == 0
                ? string.Concat(csi, codepoint.ToString(System.Globalization.CultureInfo.InvariantCulture), "u")
                : string.Concat(
                    csi,
                    codepoint.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ";",
                    (modifierBits + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "u");
        }

        /// <summary>
        /// Maps an Avalonia key to the unshifted ASCII codepoint the kitty protocol requires
        /// ("the codepoint used is always the lower-case (or more technically, un-shifted)
        /// version of the key"). Returns -1 for keys outside the spec's legacy-text set, which
        /// keeps them on their legacy encodings. Punctuation is resolved against the US layout
        /// because Avalonia's <see cref="Key"/> enum is itself a US-layout virtual key code.
        /// </summary>
        private static int GetUnshiftedCodepoint(Key key)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                return 'a' + (key - Key.A);
            }

            if (key >= Key.D0 && key <= Key.D9)
            {
                return '0' + (key - Key.D0);
            }

            return key switch
            {
                Key.Space => ' ',
                Key.OemTilde => '`',
                Key.OemMinus => '-',
                Key.OemPlus => '=',
                Key.OemOpenBrackets => '[',
                Key.OemCloseBrackets => ']',
                Key.OemPipe => '\\',
                Key.OemBackslash => '\\',
                Key.OemSemicolon => ';',
                Key.OemQuotes => '\'',
                Key.OemComma => ',',
                Key.OemPeriod => '.',
                Key.OemQuestion => '/',
                _ => -1
            };
        }

        /// <summary>
        /// Encodes an Alt/Meta + printable key as an ESC-prefixed sequence, matching the
        /// standard xterm "metaSendsEscape" behavior (e.g. Alt+V -> ESC v). This restores
        /// readline/emacs Alt keybindings and is the trigger Claude Code listens for to
        /// paste an image from the clipboard. Returns null when the combination should not
        /// be meta-encoded (no Alt, Ctrl+Alt/AltGr, or a non-printable key).
        /// </summary>
        public static string? EncodeAltKey(Key key, KeyModifiers modifiers)
        {
            if ((modifiers & KeyModifiers.Alt) == 0)
            {
                return null;
            }

            // Ctrl+Alt is AltGr on many keyboard layouts and produces real text input;
            // meta-encoding it here would double-handle the key.
            if ((modifiers & KeyModifiers.Control) != 0)
            {
                return null;
            }

            bool shift = (modifiers & KeyModifiers.Shift) != 0;

            if (key >= Key.A && key <= Key.Z)
            {
                char c = (char)('a' + (key - Key.A));
                if (shift)
                {
                    c = char.ToUpperInvariant(c);
                }

                return "\x1b" + c;
            }

            if (!shift && key >= Key.D0 && key <= Key.D9)
            {
                char c = (char)('0' + (key - Key.D0));
                return "\x1b" + c;
            }

            // Common non-alphanumeric meta keys (layout-independent), used by readline/emacs.
            switch (key)
            {
                case Key.Back:
                    return "\x1b\x7f";  // M-DEL: backward-kill-word
                case Key.Enter:
                    return "\x1b\r";
                case Key.OemPeriod when !shift:
                    return "\x1b.";     // M-.: yank-last-arg
            }

            return null;
        }

        public static string? EncodeFocusChanged(ModeState? modes, bool isFocused)
        {
            if (modes?.IsFocusEventReporting != true)
            {
                return null;
            }

            return isFocused ? "\x1b[I" : "\x1b[O";
        }

        /// <summary>
        /// Highest coordinate the legacy X10 mouse encoding can carry: the byte sent is
        /// value+32 and the largest byte is 255, so 255-32 = 223.
        /// </summary>
        private const int MaxLegacyCoordinate = 223;

        public static string? EncodeMouseEvent(ModeState modes, TerminalMouseEvent mouseEvent)
        {
            if (!ShouldReportMouseEvent(modes, mouseEvent))
            {
                return null;
            }

            int buttonCode = GetButtonCode(mouseEvent);
            if (buttonCode < 0)
            {
                return null;
            }

            int x = Math.Max(1, mouseEvent.Column);
            int y = Math.Max(1, mouseEvent.Row);

            if (modes.MouseModeSGR)
            {
                char finalChar = mouseEvent.Kind == TerminalMouseEventKind.Release ? 'm' : 'M';
                return $"\x1b[<{buttonCode};{x};{y}{finalChar}";
            }

            if (mouseEvent.Kind == TerminalMouseEventKind.Release)
            {
                buttonCode = 3 + GetModifierBits(mouseEvent.Modifiers);
            }

            // Legacy X10 coordinate encoding (`CSI M Cb Cx Cy`) transmits each coordinate as a
            // single byte holding value+32, so the largest representable coordinate is 223
            // (32+223 = 255, the highest byte value). Coordinates past that are CLAMPED to 223
            // rather than promoted to the SGR (`?1006`) form: an application that never enabled
            // `?1006` has no parser for `ESC [ <` and would render it as text or desync, so
            // xterm clamps (or drops) out-of-range coordinates instead of silently switching
            // protocols. Applications that need coordinates beyond 223 must enable `?1006`.
            char buttonChar = (char)(32 + buttonCode);
            char xChar = (char)(32 + Math.Clamp(x, 1, MaxLegacyCoordinate));
            char yChar = (char)(32 + Math.Clamp(y, 1, MaxLegacyCoordinate));
            return $"\x1b[M{buttonChar}{xChar}{yChar}";
        }

        private static bool ShouldReportMouseEvent(ModeState modes, TerminalMouseEvent mouseEvent)
        {
            if (!(modes.MouseModeX10 || modes.MouseModeButtonEvent || modes.MouseModeAnyEvent))
            {
                return false;
            }

            return mouseEvent.Kind switch
            {
                TerminalMouseEventKind.Press => IsButtonPress(mouseEvent.Button),
                TerminalMouseEventKind.Release => IsButtonPress(mouseEvent.Button),
                TerminalMouseEventKind.Wheel => mouseEvent.Button is TerminalMouseButton.WheelUp or TerminalMouseButton.WheelDown,
                TerminalMouseEventKind.Move => modes.MouseModeAnyEvent || (modes.MouseModeButtonEvent && IsButtonPress(mouseEvent.Button)),
                _ => false
            };
        }

        private static int GetButtonCode(TerminalMouseEvent mouseEvent)
        {
            int modifiers = GetModifierBits(mouseEvent.Modifiers);
            int baseCode = mouseEvent.Kind switch
            {
                TerminalMouseEventKind.Move => GetMotionBaseCode(mouseEvent.Button),
                TerminalMouseEventKind.Wheel => mouseEvent.Button switch
                {
                    TerminalMouseButton.WheelUp => 64,
                    TerminalMouseButton.WheelDown => 65,
                    _ => -1
                },
                TerminalMouseEventKind.Press or TerminalMouseEventKind.Release => GetButtonBaseCode(mouseEvent.Button),
                _ => -1
            };

            return baseCode >= 0 ? baseCode + modifiers : -1;
        }

        private static int GetMotionBaseCode(TerminalMouseButton button)
        {
            return button switch
            {
                TerminalMouseButton.Left => 32,
                TerminalMouseButton.Middle => 33,
                TerminalMouseButton.Right => 34,
                TerminalMouseButton.None => 35,
                _ => -1
            };
        }

        private static int GetButtonBaseCode(TerminalMouseButton button)
        {
            return button switch
            {
                TerminalMouseButton.Left => 0,
                TerminalMouseButton.Middle => 1,
                TerminalMouseButton.Right => 2,
                TerminalMouseButton.None => 3,
                _ => -1
            };
        }

        private static bool IsButtonPress(TerminalMouseButton button)
        {
            return button is TerminalMouseButton.Left or TerminalMouseButton.Middle or TerminalMouseButton.Right;
        }

        private static int GetModifierBits(KeyModifiers modifiers)
        {
            int bits = 0;
            if ((modifiers & KeyModifiers.Shift) != 0) bits += 4;
            if ((modifiers & KeyModifiers.Alt) != 0) bits += 8;
            if ((modifiers & KeyModifiers.Control) != 0) bits += 16;
            return bits;
        }
    }
}

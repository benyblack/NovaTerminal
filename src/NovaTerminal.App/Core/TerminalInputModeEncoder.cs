using System;
using Avalonia.Input;

namespace NovaTerminal.Core
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

        public static string? EncodeFocusChanged(ModeState? modes, bool isFocused)
        {
            if (modes?.IsFocusEventReporting != true)
            {
                return null;
            }

            return isFocused ? "\x1b[I" : "\x1b[O";
        }

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

            if (x >= 223 || y >= 223)
            {
                char finalChar = mouseEvent.Kind == TerminalMouseEventKind.Release ? 'm' : 'M';
                return $"\x1b[<{buttonCode};{x};{y}{finalChar}";
            }

            if (mouseEvent.Kind == TerminalMouseEventKind.Release)
            {
                buttonCode = 3 + GetModifierBits(mouseEvent.Modifiers);
            }

            char buttonChar = (char)(32 + buttonCode);
            char xChar = (char)(32 + Math.Clamp(x, 1, 223));
            char yChar = (char)(32 + Math.Clamp(y, 1, 223));
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

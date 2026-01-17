using Avalonia.Media;
using System;
using System.Collections.Generic;

namespace NovaTerminal.Core
{
    public class AnsiParser
    {
        private TerminalBuffer _buffer;
        private enum State { Normal, Esc, Csi, Osc, OscEsc }
        private State _state = State.Normal;
        private List<char> _paramBuffer = new List<char>();
        private List<char> _oscStringBuffer = new List<char>(); // Used for OSC string content

        public AnsiParser(TerminalBuffer buffer)
        {
            _buffer = buffer;
        }

        public void Process(string input) // Renamed 'text' to 'input' to match original
        {
            foreach (char c in input)
            {
                switch (_state)
                {
                    case State.Normal:
                        if (c == '\x1b') _state = State.Esc;
                        else if (c == '\u009B') // C1 CSI
                        {
                            _state = State.Csi;
                            _paramBuffer.Clear();
                        }
                        else if (c == '\u009D') // C1 OSC
                        {
                            _state = State.Osc;
                            _oscStringBuffer.Clear();
                        }
                        else if (c == '\a') { /* Ignore BEL in normal text */ }
                        else _buffer.WriteChar(c);
                        break;

                    case State.Esc:
                        if (c == '[')
                        {
                            _state = State.Csi;
                            _paramBuffer.Clear();
                        }
                        else if (c == ']') // OSC Start
                        {
                            _state = State.Osc;
                            _oscStringBuffer.Clear();
                        }
                        else if (c == '7') // DECSC Save Cursor
                        {
                            _buffer.SaveCursor();
                            _state = State.Normal;
                        }
                        else if (c == '8') // DECRC Restore Cursor
                        {
                            _buffer.RestoreCursor();
                            _state = State.Normal;
                        }
                        else
                        {
                            // Fallback
                            _state = State.Normal;
                        }
                        break;

                    case State.Osc:
                        if (c == '\a' || c == '\u009C') // BEL or ST (8-bit)
                        {
                            _state = State.Normal;
                        }
                        else if (c == '\x1b')
                        {
                            _state = State.OscEsc;
                        }
                        // Else consume
                        break;

                    case State.OscEsc:
                        if (c == '\\') // ST Terminator (ESC \)
                        {
                            _state = State.Normal;
                        }
                        else
                        {
                            // Invalid termination, return to normal
                            _state = State.Normal;
                            // Optionally re-process c, but for safety just consume
                        }
                        break;

                    case State.Csi:
                        // Collect Parameter bytes (0x30-0x3F) and Intermediate bytes (0x20-0x2F)
                        // This includes digits, semicolon, ?, >, etc.
                        if (c >= 0x20 && c <= 0x3F)
                        {
                            _paramBuffer.Add(c);
                        }
                        else
                        {
                            // Final byte (0x40-0x7E, usually @-~)
                            HandleCsi(c);
                            _state = State.Normal;
                        }
                        break;
                }
            }
        }

        private void HandleCsi(char finalByte)
        {
            string paramsStr = new string(_paramBuffer.ToArray());
            // Support both semi-colon and colon as separators (common in modern terminals)
            string[] parts = paramsStr.Split(new char[] { ';', ':' }, StringSplitOptions.RemoveEmptyEntries);
            int[] args = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++) int.TryParse(parts[i], out args[i]);

            int arg0 = args.Length > 0 ? args[0] : 0; // Default 0 varies by command

            switch (finalByte)
            {
                case 'A': // Cursor Up
                    _buffer.CursorRow = Math.Max(0, _buffer.CursorRow - Math.Max(1, arg0));
                    _buffer.Invalidate();
                    break;
                case 'B': // Cursor Down
                    _buffer.CursorRow = Math.Min(_buffer.Rows - 1, _buffer.CursorRow + Math.Max(1, arg0));
                    _buffer.Invalidate();
                    break;
                case 'C': // Cursor Forward
                    _buffer.CursorCol = Math.Min(_buffer.Cols - 1, _buffer.CursorCol + Math.Max(1, arg0));
                    _buffer.Invalidate();
                    break;
                case 'D': // Cursor Back
                    _buffer.CursorCol = Math.Max(0, _buffer.CursorCol - Math.Max(1, arg0));
                    _buffer.Invalidate();
                    break;
                case 'H': // Cursor Position (row;col)
                case 'f':
                    int row = (args.Length > 0 ? args[0] : 1) - 1;
                    int col = (args.Length > 1 ? args[1] : 1) - 1;
                    _buffer.CursorRow = Math.Clamp(row, 0, _buffer.Rows - 1);
                    _buffer.CursorCol = Math.Clamp(col, 0, _buffer.Cols - 1);
                    _buffer.Invalidate();
                    break;
                case 'G': // Cursor Horizontal Absolute (CHA)
                    int val = (args.Length > 0 ? args[0] : 1) - 1;
                    _buffer.CursorCol = Math.Clamp(val, 0, _buffer.Cols - 1);
                    _buffer.Invalidate();
                    break;
                case 'J': // Erase in Display
                    int displayMode = args.Length > 0 ? args[0] : 0;

                    if (displayMode == 0) // Erase from cursor to end of screen
                    {
                        _buffer.EraseLineToEnd(); // Clear rest of current line
                        // Clear all lines below (simplified - just clear current line for now)
                    }
                    else if (displayMode == 1) // Erase from start of screen to cursor
                    {
                        _buffer.EraseLineFromStart();
                    }
                    else if (displayMode == 2 || displayMode == 3) // Erase entire screen
                    {
                        _buffer.Clear();
                    }
                    break;
                case 'K': // Erase in Line
                    int mode = args.Length > 0 ? args[0] : 0;

                    if (mode == 0) _buffer.EraseLineToEnd();
                    else if (mode == 1) _buffer.EraseLineFromStart();
                    else if (mode == 2) _buffer.EraseLineAll();
                    break;
                case 'X': // Erase Character (ECH)
                    int count = args.Length > 0 ? args[0] : 1;
                    _buffer.EraseCharacters(count);
                    break;
                case 's': // Save Cursor (ANSI.SYS / SCO)
                    _buffer.SaveCursor();
                    break;
                case 'u': // Restore Cursor (ANSI.SYS / SCO)
                    _buffer.RestoreCursor();
                    break;
                case 'm': // SGR (Select Graphic Rendition)
                    HandleSgr(args);
                    break;
                case 'h': // Set Mode
                case 'l': // Reset Mode
                    // Check if this is a DEC Private Mode (CSI ? Ps h/l)
                    if (paramsStr.StartsWith("?"))
                    {
                        bool enable = (finalByte == 'h');
                        // Strip '?' and re-parse the mode numbers
                        string modeStr = paramsStr.Substring(1);
                        string[] modeParts = modeStr.Split(new char[] { ';', ':' }, StringSplitOptions.RemoveEmptyEntries);
                        int[] modes = new int[modeParts.Length];
                        for (int i = 0; i < modeParts.Length; i++)
                            int.TryParse(modeParts[i], out modes[i]);

                        HandleDECPrivateMode(modes, enable);
                    }
                    break;
            }
        }

        /// <summary>
        /// Handles DEC Private Mode sequences (CSI ? Ps h/l)
        /// </summary>
        private void HandleDECPrivateMode(int[] modes, bool enable)
        {
            foreach (int mode in modes)
            {
                switch (mode)
                {
                    case 1: // DECCKM - Cursor Keys Mode
                        _buffer.IsApplicationCursorKeys = enable;
                        break;
                    case 7: // DECAWM - Auto Wrap Mode
                        _buffer.IsAutoWrapMode = enable;
                        break;
                    case 1000: // X10 mouse reporting
                        _buffer.MouseModeX10 = enable;
                        break;
                    case 1002: // Button event tracking
                        _buffer.MouseModeButtonEvent = enable;
                        break;
                    case 1003: // Any event tracking  
                        _buffer.MouseModeAnyEvent = enable;
                        break;
                    case 1006: // SGR extended mouse mode
                        _buffer.MouseModeSGR = enable;
                        break;
                }
            }
        }

        private void HandleSgr(int[] args)
        {
            if (args.Length == 0)
            {
                ResetColors();
                _buffer.IsDefaultForeground = true;
                _buffer.IsDefaultBackground = true;
                _buffer.IsBold = false;
                _buffer.IsInverse = false;
                return;
            }

            for (int i = 0; i < args.Length; i++)
            {
                int code = args[i];

                if (code == 0)
                {
                    ResetColors();
                    _buffer.IsDefaultForeground = true;
                    _buffer.IsDefaultBackground = true;
                    _buffer.IsBold = false;
                    _buffer.IsInverse = false;
                }
                else if (code >= 30 && code <= 37)
                {
                    _buffer.CurrentForeground = GetBasicColor(code - 30);
                    _buffer.IsDefaultForeground = false;
                }
                else if (code >= 90 && code <= 97)
                {
                    _buffer.CurrentForeground = GetBasicColor(code - 90, true);
                    _buffer.IsDefaultForeground = false;
                }
                else if (code == 38)
                {
                    var color = ParseExtendedColor(args, ref i);
                    if (color.HasValue)
                    {
                        _buffer.CurrentForeground = color.Value;
                        _buffer.IsDefaultForeground = false;
                    }
                }
                else if (code == 39)
                {
                    _buffer.CurrentForeground = _buffer.Theme.Foreground;
                    _buffer.IsDefaultForeground = true;
                }
                else if (code >= 40 && code <= 47)
                {
                    _buffer.CurrentBackground = GetBasicColor(code - 40);
                    _buffer.IsDefaultBackground = false;
                }
                else if (code >= 100 && code <= 107)
                {
                    _buffer.CurrentBackground = GetBasicColor(code - 100, true);
                    _buffer.IsDefaultBackground = false;
                }
                else if (code == 48)
                {
                    var color = ParseExtendedColor(args, ref i);
                    if (color.HasValue)
                    {
                        _buffer.CurrentBackground = color.Value;
                        _buffer.IsDefaultBackground = false;
                    }
                }
                else if (code == 49)
                {
                    _buffer.CurrentBackground = _buffer.Theme.Background;
                    _buffer.IsDefaultBackground = true;
                }
                else if (code == 1) // Bold
                {
                    _buffer.IsBold = true;
                    // Optional: Auto-brighten current color if it's a basic color?
                    // Typically purely a state flag for upcoming chars.
                }
                else if (code == 22) // Normal Intensity (Not Bold)
                {
                    _buffer.IsBold = false;
                }
                else if (code == 7) // Inverse
                {
                    _buffer.IsInverse = true;
                }
                else if (code == 27) // No Inverse
                {
                    _buffer.IsInverse = false;
                }
                else if (code >= 90 && code <= 97) // Bright Foreground
                {
                    _buffer.CurrentForeground = GetBasicColor(code - 90, bright: true);
                }
                else if (code >= 100 && code <= 107) // Bright Background
                {
                    _buffer.CurrentBackground = GetBasicColor(code - 100, bright: true);
                }
                else if (code == 38) // Extended Foreground
                {
                    var c = ParseExtendedColor(args, ref i);
                    if (c.HasValue) _buffer.CurrentForeground = c.Value;
                }
                else if (code == 48) // Extended Background
                {
                    var c = ParseExtendedColor(args, ref i);
                    if (c.HasValue) _buffer.CurrentBackground = c.Value;
                }
            }
        }

        private void ResetColors()
        {
            _buffer.CurrentForeground = _buffer.Theme.Foreground;
            _buffer.CurrentBackground = _buffer.Theme.Background;
            _buffer.IsInverse = false;
            _buffer.IsBold = false;
        }

        private Color? ParseExtendedColor(int[] args, ref int i)
        {
            if (i + 1 >= args.Length) return null;

            int mode = args[++i];
            if (mode == 5) // 256 colors
            {
                if (i + 1 >= args.Length) return null;
                int index = args[++i];
                return GetXtermColor(index);
            }
            else if (mode == 2) // TrueColor (Next 3 args are R, G, B)
            {
                if (i + 3 >= args.Length) return null;
                byte r = (byte)args[++i];
                byte g = (byte)args[++i];
                byte b = (byte)args[++i];
                return Color.FromRgb(r, g, b);
            }
            return null;
        }

        private Color GetBasicColor(int index, bool bright = false)
        {
            return _buffer.Theme.GetAnsiColor(index, bright);
        }

        private Color GetXtermColor(int index)
        {
            // 0-15: Standard colors from theme
            if (index < 16)
            {
                return GetBasicColor(index % 8, index >= 8);
            }

            // 16-231: 6x6x6 Cube
            if (index < 232)
            {
                index -= 16;
                int r = (index / 36);
                int g = (index / 6) % 6;
                int b = index % 6;

                // Mapping 0-5 to 0-255: 0->0, 1->95, 2->135, 3->175, 4->215, 5->255
                byte ToByte(int v) => (byte)(v == 0 ? 0 : (v * 40 + 55));
                return Color.FromRgb(ToByte(r), ToByte(g), ToByte(b));
            }

            // 232-255: Grayscale
            if (index < 256)
            {
                index -= 232;
                byte v = (byte)(index * 10 + 8);
                return Color.FromRgb(v, v, v);
            }

            return Colors.White;
        }
    }
}

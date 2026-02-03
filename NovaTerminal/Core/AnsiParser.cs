using Avalonia.Media;
using System;
using System.Collections.Generic;

namespace NovaTerminal.Core
{
    public class AnsiParser
    {
        private TerminalBuffer _buffer;
        private enum State { Normal, Esc, Csi, Osc, OscEsc, Dcs, DcsEsc }
        private State _state = State.Normal;

        // Zero-alloc buffers
        private char[] _paramBuffer = new char[256];
        private int _paramLen = 0;

        private List<char> _oscStringBuffer = new List<char>(); // OSC strings can be long (titles, etc) - Keep List for now or limit

        public AnsiParser(TerminalBuffer buffer)
        {
            _buffer = buffer;
        }

        public void Process(string input)
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
                            _paramLen = 0;
                        }
                        else if (c == '\u009D') // C1 OSC
                        {
                            _state = State.Osc;
                            _oscStringBuffer.Clear();
                        }
                        else if (c == '\a') { /* Ignore BEL */ }
                        else _buffer.WriteChar(c);
                        break;

                    case State.Esc:
                        if (c == '[')
                        {
                            _state = State.Csi;
                            _paramLen = 0;
                        }
                        else if (c == ']') // OSC Start
                        {
                            _state = State.Osc;
                            _oscStringBuffer.Clear();
                        }
                        else if (c == 'P') // DCS Start (Device Control String) - Sixel, etc.
                        {
                            _state = State.Dcs;
                        }
                        else if (c == '7') // Save Cursor
                        {
                            _buffer.SaveCursor();
                            _state = State.Normal;
                        }
                        else if (c == '8') // Restore Cursor
                        {
                            _buffer.RestoreCursor();
                            _state = State.Normal;
                        }
                        else if (c == '(' || c == ')' || c == '*' || c == '+' || c == '-')
                        {
                            // G0/G1 charset selection - ignore for now but don't print
                            _state = State.Normal;
                        }
                        else if (c >= 0x20 && c <= 0x7E)
                        {
                            // Unknown escape sequence followed by printable char?
                            // Treat as literal printable character (fallback)
                            _buffer.WriteChar(c);
                            _state = State.Normal;
                        }
                        else
                        {
                            _state = State.Normal;
                        }
                        break;

                    case State.Osc:
                        if (c == '\a' || c == '\u009C')
                        {
                            // Handle OSC Here if needed (e.g. Title)
                            _state = State.Normal;
                        }
                        else if (c == '\x1b')
                        {
                            _state = State.OscEsc;
                        }
                        else
                        {
                            _oscStringBuffer.Add(c);
                        }
                        break;

                    case State.OscEsc:
                        if (c == '\\')
                        {
                            // Handle OSC Here
                            _state = State.Normal;
                        }
                        else
                        {
                            _state = State.Normal;
                        }
                        break;

                    case State.Dcs:
                        // Ignore everything until ST (ESC \)
                        if (c == '\x1b') _state = State.DcsEsc;
                        break;

                    case State.DcsEsc:
                        if (c == '\\') _state = State.Normal; // ST terminator
                        else _state = State.Dcs; // False alarm, back to consuming
                        break;

                    case State.Csi:
                        if (c >= 0x20 && c <= 0x3F)
                        {
                            // Collect params (limited to buffer size)
                            if (_paramLen < _paramBuffer.Length)
                            {
                                _paramBuffer[_paramLen++] = c;
                            }
                        }
                        else
                        {
                            // Final byte
                            HandleCsi(c, _paramBuffer.AsSpan(0, _paramLen));
                            _state = State.Normal;
                        }
                        break;
                }
            }
        }

        private void HandleCsi(char finalByte, ReadOnlySpan<char> parameters)
        {
            // Check for private mode prefix (?)
            bool isPrivate = parameters.Length > 0 && parameters[0] == '?';
            int startIdx = isPrivate ? 1 : 0;

            // Parse integers into stack buffer
            // Max 32 args is generous for standard CSI; SGR can have more but we'll cap it for now to avoid complexity or allocations
            Span<int> args = stackalloc int[32];
            int argCount = 0;

            int currentVal = 0;
            bool hasVal = false;

            for (int i = startIdx; i < parameters.Length; i++)
            {
                char c = parameters[i];
                if (c >= '0' && c <= '9')
                {
                    currentVal = (currentVal * 10) + (c - '0');
                    hasVal = true;
                }
                else if (c == ';' || c == ':')
                {
                    if (argCount < args.Length)
                    {
                        args[argCount++] = hasVal ? currentVal : 0;
                    }
                    currentVal = 0;
                    hasVal = false;
                }
            }
            // Add final arg
            if (hasVal || (parameters.Length > startIdx && (parameters[parameters.Length - 1] == ';' || parameters[parameters.Length - 1] == ':')))
            {
                if (argCount < args.Length)
                {
                    args[argCount++] = hasVal ? currentVal : 0;
                }
            }

            // Slice to actual count
            ReadOnlySpan<int> validArgs = args.Slice(0, argCount);
            int arg0 = argCount > 0 ? validArgs[0] : 0;

            switch (finalByte)
            {
                case 'A': // Cursor Up
                    {
                        int dist = Math.Max(1, arg0);
                        if (_buffer.CursorRow >= _buffer.ScrollTop && _buffer.CursorRow <= _buffer.ScrollBottom)
                            _buffer.CursorRow = Math.Max(_buffer.ScrollTop, _buffer.CursorRow - dist);
                        else
                            _buffer.CursorRow = Math.Max(0, _buffer.CursorRow - dist);
                        _buffer.Invalidate();
                    }
                    break;
                case 'B': // Cursor Down
                    {
                        int dist = Math.Max(1, arg0);
                        if (_buffer.CursorRow >= _buffer.ScrollTop && _buffer.CursorRow <= _buffer.ScrollBottom)
                            _buffer.CursorRow = Math.Min(_buffer.ScrollBottom, _buffer.CursorRow + dist);
                        else
                            _buffer.CursorRow = Math.Min(_buffer.Rows - 1, _buffer.CursorRow + dist);
                        _buffer.Invalidate();
                    }
                    break;
                case 'r': // DECSTBM - Set Scrolling Region
                    {
                        int regionTop = (argCount > 0 && validArgs[0] > 0 ? validArgs[0] : 1) - 1;
                        int regionBottom = (argCount > 1 && validArgs[1] > 0 ? validArgs[1] : _buffer.Rows) - 1;
                        _buffer.SetScrollingRegion(regionTop, regionBottom);
                        // Cursor moves to 1;1 after setting region
                        _buffer.SetCursorPosition(0, 0);
                    }
                    break;
                case '@': // ICH - Insert Character
                    {
                        int ichCount = Math.Max(1, arg0);
                        _buffer.InsertCharacters(ichCount);
                    }
                    break;
                case 'P': // DCH - Delete Character
                    {
                        int dchCount = Math.Max(1, arg0);
                        _buffer.DeleteCharacters(dchCount);
                    }
                    break;
                case 'S': // SU - Scroll Up
                    {
                        int suCount = Math.Max(1, arg0);
                        for (int i = 0; i < suCount; i++) _buffer.ScrollUp();
                    }
                    break;
                case 'T': // SD - Scroll Down
                    {
                        int sdCount = Math.Max(1, arg0);
                        for (int i = 0; i < sdCount; i++) _buffer.ScrollDown();
                    }
                    break;
                case 'd': // VPA - Vertical Position Absolute
                    {
                        int vpaRow = Math.Max(1, arg0) - 1;
                        _buffer.CursorRow = Math.Clamp(vpaRow, 0, _buffer.Rows - 1);
                        _buffer.Invalidate();
                    }
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
                    int row = (argCount > 0 ? validArgs[0] : 1) - 1;
                    int col = (argCount > 1 ? validArgs[1] : 1) - 1;
                    _buffer.SetCursorPosition(col, row);
                    _buffer.Invalidate();
                    break;
                case 'G': // Cursor Horizontal Absolute (CHA)
                    int val = (argCount > 0 ? validArgs[0] : 1) - 1;
                    _buffer.CursorCol = Math.Clamp(val, 0, _buffer.Cols - 1);
                    _buffer.Invalidate();
                    break;
                case 'J': // Erase in Display
                    int displayMode = argCount > 0 ? validArgs[0] : 0;

                    if (displayMode == 0) // Erase from cursor to end of screen
                    {
                        _buffer.EraseLineToEnd(); // Clear rest of current line
                        // Clear all lines below cursor
                        for (int r = _buffer.CursorRow + 1; r < _buffer.Rows; r++)
                        {
                            _buffer.EraseLineAll(r);
                        }
                    }
                    else if (displayMode == 1) // Erase from start of screen to cursor
                    {
                        // Clear all lines above cursor
                        for (int r = 0; r < _buffer.CursorRow; r++)
                        {
                            _buffer.EraseLineAll(r);
                        }
                        _buffer.EraseLineFromStart(); // Clear start of current line
                    }
                    else if (displayMode == 2 || displayMode == 3) // Erase entire screen
                    {
                        _buffer.Clear(resetCursor: false);
                    }
                    break;
                case 'K': // Erase in Line
                    int mode = argCount > 0 ? validArgs[0] : 0;

                    if (mode == 0) _buffer.EraseLineToEnd();
                    else if (mode == 1) _buffer.EraseLineFromStart();
                    else if (mode == 2) _buffer.EraseLineAll();
                    break;
                case 'X': // Erase Character (ECH)
                    int count = argCount > 0 ? validArgs[0] : 1;
                    _buffer.EraseCharacters(count);
                    break;
                case 'L': // Insert Line (IL)
                    int linesToInsert = argCount > 0 ? validArgs[0] : 1;
                    _buffer.InsertLines(linesToInsert);
                    break;
                case 'M': // Delete Line (DL)
                    int linesToDelete = argCount > 0 ? validArgs[0] : 1;
                    _buffer.DeleteLines(linesToDelete);
                    break;
                case 's': // Save Cursor (ANSI.SYS / SCO)
                    _buffer.SaveCursor();
                    break;
                case 'u': // Restore Cursor (ANSI.SYS / SCO)
                    _buffer.RestoreCursor();
                    break;
                case 'm': // SGR (Select Graphic Rendition)
                    HandleSgr(validArgs);
                    break;
                case 'h': // Set Mode
                case 'l': // Reset Mode
                    // Check if this is a DEC Private Mode (CSI ? Ps h/l)
                    if (isPrivate)
                    {
                        bool enable = (finalByte == 'h');
                        HandleDECPrivateMode(validArgs, enable);
                    }
                    break;
            }
        }

        /// <summary>
        /// Handles DEC Private Mode sequences (CSI ? Ps h/l)
        /// </summary>
        private void HandleDECPrivateMode(ReadOnlySpan<int> modes, bool enable)
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
                    case 47:    // Alternate screen (legacy)
                    case 1047:  // Alternate screen
                        if (enable)
                            _buffer.SwitchToAltScreen();
                        else
                            _buffer.SwitchToMainScreen();
                        break;
                    case 1049:  // Alternate screen + save cursor
                        if (enable)
                        {
                            _buffer.SaveCursor();
                            _buffer.SwitchToAltScreen();
                        }
                        else
                        {
                            _buffer.SwitchToMainScreen();
                            _buffer.RestoreCursor();
                        }
                        break;
                }
            }
        }

        private void HandleSgr(ReadOnlySpan<int> args)
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
                else if (code == 8) // Hidden
                {
                    _buffer.IsHidden = true;
                }
                else if (code == 28) // Visible (No Hidden)
                {
                    _buffer.IsHidden = false;
                }
            }
        }

        private void ResetColors()
        {
            _buffer.CurrentForeground = _buffer.Theme.Foreground;
            _buffer.CurrentBackground = _buffer.Theme.Background;
            _buffer.IsInverse = false;
            _buffer.IsBold = false;
            _buffer.IsHidden = false;
        }

        private Color? ParseExtendedColor(ReadOnlySpan<int> args, ref int i)
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

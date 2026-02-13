
using System;
using System.Collections.Generic;
using SkiaSharp;

namespace NovaTerminal.Core
{
    public class AnsiParser
    {
        private TerminalBuffer _buffer;
        private enum State { Normal, Esc, Csi, Osc, OscEsc, Dcs, DcsEsc, Charset, Apc, ApcEsc, EscHash }
        private State _state = State.Normal;

        // Flag to swallow a single newline after an inline image (common in scripts)
        private bool _swallowNextNewline = false;

        // Zero-alloc buffers
        private char[] _paramBuffer = new char[256];
        private int _paramLen = 0;

        // ConPTY Sync Fix: Track vertical offset caused by inline images that ConPTY doesn't see.
        // This effectively "scrolls" the PTY's logical cursor to match our visual cursor.
        private int _verticalOffset = 0;

        private List<char> _oscStringBuffer = new List<char>(); // OSC strings can be long (titles, etc) - Keep List for now or limit
        private List<char> _apcStringBuffer = new List<char>();
        private List<char> _dcsStringBuffer = new List<char>();
        private System.Text.StringBuilder _kittyPayloadBuffer = new System.Text.StringBuilder();
        private Dictionary<string, string> _kittyPendingParams = new();
        private SixelDecoder _sixelDecoder = new();

        public float CellWidth { get; set; } = 10.0f;  // Default fallback
        public float CellHeight { get; set; } = 20.0f; // Default fallback
        public Action<string>? OnResponse { get; set; }

        public AnsiParser(TerminalBuffer buffer)
        {
            _buffer = buffer;
        }

        public void Process(string input)
        {
            if (string.IsNullOrEmpty(input)) return;
            RendererStatistics.RecordBytes(input.Length * sizeof(char)); // Simplified for char count

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                foreach (char c in input)
                {
                    switch (_state)
                    {
                        case State.Normal:
                            if (c == '\x1b')
                            {
                                _state = State.Esc;
                            }
                            else if (c == '\u009B') // C1 CSI
                            {
                                _state = State.Csi;
                                _paramLen = 0;
                            }
                            else if (c == '\u0090') // C1 DCS
                            {
                                _state = State.Dcs;
                                _dcsStringBuffer.Clear();
                            }
                            else if (c == '\u009D') // C1 OSC
                            {
                                _state = State.Osc;
                                _oscStringBuffer.Clear();
                            }
                            else if (c == '\u009F') // C1 APC
                            {
                                _state = State.Apc;
                                _apcStringBuffer.Clear();
                            }
                            else if (c == '\a')
                            {
                                /* Ignore BEL */
                            }
                            else
                            {
                                if (_swallowNextNewline)
                                {
                                    if (c == '\r' || c == '\n')
                                    {
                                        // Swallow it
                                        if (c == '\n') _swallowNextNewline = false; // Done
                                        continue;
                                    }
                                    else
                                    {
                                        // Non-newline char? Stop swallowing.
                                        _swallowNextNewline = false;
                                    }
                                }
                                _buffer.WriteChar(c);
                            }
                            break;

                        case State.Esc:
                            // Log only in non-performance-critical scenarios if needed
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
                                _dcsStringBuffer.Clear();
                            }
                            else if (c == '_') // APC Start
                            {
                                _state = State.Apc;
                                _apcStringBuffer.Clear();
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
                            else if (c == 'c') // RIS - Reset to Initial State
                            {
                                _buffer.Reset(); // Ensure TerminalBuffer has a Reset method or use Clear
                                _verticalOffset = 0;
                                _state = State.Normal;
                            }
                            else if (c == '(' || c == ')' || c == '*' || c == '+' || c == '-')
                            {
                                // G0/G1 charset selection - wait for the next char but don't print
                                _state = State.Charset;
                            }
                            else if (c == '#')
                            {
                                _state = State.EscHash;
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

                        case State.Charset:
                            // Consume the charset designation character (e.g. 'B' in ESC ( B)
                            _state = State.Normal;
                            break;

                        case State.EscHash:
                            if (c == '8') // DECALN - Screen Alignment Pattern
                            {
                                _buffer.ScreenAlignmentPattern();
                            }
                            // 3, 4, 5, 6 are for single/double width/height lines.
                            // We ignore them for now as we don't support per-line rendering attributes yet.
                            _state = State.Normal;
                            break;

                        case State.Osc:
                            if (c == '\a' || c == '\u009C')
                            {
                                HandleOsc(new string(_oscStringBuffer.ToArray()));
                                _state = State.Normal;
                            }
                            else if (c == '\x1b')
                            {
                                _state = State.OscEsc;
                            }
                            else
                            {
                                if (_oscStringBuffer.Count < 50) // Only log the beginning of potential images to avoid huge logs
                                {
                                }
                                _oscStringBuffer.Add(c);
                            }
                            break;

                        case State.OscEsc:
                            if (c == '\\')
                            {
                                HandleOsc(new string(_oscStringBuffer.ToArray()));
                                _state = State.Normal;
                            }
                            else
                            {
                                _state = State.Normal;
                            }
                            break;

                        case State.Dcs:
                            if (c == '\x1b') _state = State.DcsEsc;
                            else _dcsStringBuffer.Add(c);
                            break;

                        case State.DcsEsc:
                            if (c == '\\')
                            {
                                HandleDcs(new string(_dcsStringBuffer.ToArray()));
                                _state = State.Normal;
                            }
                            else
                            {
                                _dcsStringBuffer.Add('\x1b');
                                _dcsStringBuffer.Add(c);
                                _state = State.Dcs;
                            }
                            break;

                        case State.Apc:
                            if (c == '\x1b')
                            {
                                _state = State.ApcEsc;
                            }
                            else if (c == '\u009C') // C1 ST
                            {
                                HandleApc(new string(_apcStringBuffer.ToArray()));
                                _state = State.Normal;
                            }
                            else
                            {
                                _apcStringBuffer.Add(c);
                            }
                            break;

                        case State.ApcEsc:
                            if (c == '\\')
                            {
                                HandleApc(new string(_apcStringBuffer.ToArray()));
                                _state = State.Normal;
                            }
                            else
                            {
                                _apcStringBuffer.Add('\x1b');
                                _apcStringBuffer.Add(c);
                                _state = State.Apc;
                            }
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
            finally
            {
                sw.Stop();
                RendererStatistics.RecordParseTime(sw.ElapsedMilliseconds);
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
                        if (_verticalOffset > 0) vpaRow += _verticalOffset;
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

                    // ConPTY Fix: Apply vertical offset if active
                    if (_verticalOffset > 0)
                    {
                        // Only apply if the requested row is "above" where we think we are?
                        // Actually, ConPTY is absolute. If it says row 0, it means top of screen.
                        // But we want top of screen + offset.
                        row += _verticalOffset;

                        // Clamp to prevent overflow (though SetCursorPosition handles bounds)
                        if (row >= _buffer.Rows)
                        {
                            // If we push off screen, we might need to scroll?
                            // For now, let SetCursorPosition clamp or we rely on buffer scrolling logic if we were writing text.
                            // But CUP just moves cursor.
                        }
                    }

                    _buffer.SetCursorPosition(col, row);
                    _buffer.Invalidate();
                    break;
                case 'G': // Cursor Horizontal Absolute (CHA)
                    int val = (argCount > 0 ? validArgs[0] : 1) - 1;
                    int oldCol = _buffer.CursorCol;
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
                        _verticalOffset = 0; // Reset offset on clear screen
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
                    bool enableMode = (finalByte == 'h');
                    if (isPrivate)
                    {
                        HandleDECPrivateMode(validArgs, enableMode);
                    }
                    else
                    {
                        // Handle Standard Modes (ANSI)
                        foreach (int m in validArgs)
                        {
                            if (m == 4) // IRM - Insert Replacement Mode
                            {
                                _buffer.Modes.IsInsertMode = enableMode;
                            }
                        }
                    }
                    break;
                    break;
                case 'c': // DA - Device Attributes
                    if (parameters.Length > 0 && parameters[0] == '>')
                    {
                        // Secondary Device Attributes
                        // CSI > 1 ; 1 0 ; 0 c (standard for VT220-ish)
                        OnResponse?.Invoke("\x1b[>1;10;0c");
                    }
                    else if (!isPrivate)
                    {
                        // Primary Device Attributes (CSI c)
                        // Respond with VT100/VT102 capability to satisfy vttest
                        // ?1;2c (VT100 with AVO) is safest baseline, but we claim more.
                        // ?62;4;22c (VT220 + Sixel + ANSI)
                        // vttest checks these. 
                        // Let's stick to ?62;4;22c as it worked for XTerm.
                        OnResponse?.Invoke("\x1b[?62;4;22c");
                    }
                    break;
                case 'n': // DSR - Device Status Report
                    if (arg0 == 5) // Status Query
                    {
                        OnResponse?.Invoke("\x1b[0n"); // OK
                    }
                    else if (arg0 == 6) // Cursor Position Report (CPR)
                    {
                        // CSI r ; c R
                        string response = $"\x1b[{_buffer.CursorRow + 1};{_buffer.CursorCol + 1}R";
                        OnResponse?.Invoke(response);
                    }
                    else if (arg0 == 0) // DSR response (ignore)
                    {
                    }
                    break;
                default:
                    TerminalLogger.Log($"[ANSI_PARSER] Unhandled CSI: {finalByte} (private={isPrivate}), params={new string(parameters)}");
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
                        _buffer.Modes.IsApplicationCursorKeys = enable;
                        break;
                    case 7: // DECAWM - Auto Wrap Mode
                        _buffer.Modes.IsAutoWrapMode = enable;
                        break;
                    case 1000: // X10 mouse reporting
                        _buffer.Modes.MouseModeX10 = enable;
                        break;
                    case 1002: // Button event tracking
                        _buffer.Modes.MouseModeButtonEvent = enable;
                        break;
                    case 1003: // Any event tracking
                        _buffer.Modes.MouseModeAnyEvent = enable;
                        break;
                    case 1006: // SGR extended mouse mode
                        _buffer.Modes.MouseModeSGR = enable;
                        break;
                    case 25:    // DECTCEM - Text Cursor Enable Mode
                        _buffer.Modes.IsCursorVisible = enable;
                        break;
                    case 47:    // Alternate screen (legacy)
                    case 1047:  // Alternate screen
                        if (enable) _buffer.SwitchToAltScreen();
                        else _buffer.SwitchToMainScreen();
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
                    case 2004:  // Bracketed Paste Mode
                        _buffer.Modes.IsBracketedPasteMode = enable;
                        break;
                    case 2026: // Synchronized Output (Batch Rendering)
                        if (enable) _buffer.BeginSync();
                        else _buffer.EndSync();
                        break;
                    case 9001: // ConPTY Passthrough Mode
                        break;
                    default:
                        // Only log unhandled modes as they might be important for future features
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
                _buffer.CurrentFgIndex = -1; // Default
                _buffer.CurrentBgIndex = -1; // Default
                _buffer.IsBold = false;
                _buffer.IsFaint = false;
                _buffer.IsItalic = false;
                _buffer.IsUnderline = false;
                _buffer.IsBlink = false;
                _buffer.IsStrikethrough = false;
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
                    _buffer.CurrentFgIndex = -1;
                    _buffer.CurrentBgIndex = -1;
                    _buffer.IsBold = false;
                    _buffer.IsFaint = false;
                    _buffer.IsItalic = false;
                    _buffer.IsUnderline = false;
                    _buffer.IsBlink = false;
                    _buffer.IsStrikethrough = false;
                    _buffer.IsInverse = false;
                }
                else if (code >= 30 && code <= 37)
                {
                    _buffer.CurrentForeground = GetBasicColor(code - 30);
                    _buffer.CurrentFgIndex = (short)(code - 30);
                    _buffer.IsDefaultForeground = false;
                }
                else if (code >= 90 && code <= 97)
                {
                    _buffer.CurrentForeground = GetBasicColor(code - 90, true);
                    _buffer.CurrentFgIndex = (short)((code - 90) + 8);
                    _buffer.IsDefaultForeground = false;
                }
                else if (code == 38)
                {
                    short idx = -1;
                    var color = ParseExtendedColor(args, ref i, out idx);
                    if (color.HasValue)
                    {
                        // Snapping for Campbell Blue/Black
                        if (idx == -1 && color.Value.R == 0 && color.Value.G == 55 && color.Value.B == 218) idx = 4; // #0037DA Blue
                        if (idx == -1 && color.Value.R == 58 && color.Value.G == 150 && color.Value.B == 221) idx = 4; // #3A96DD Blue (PS)
                        if (idx == -1 && color.Value.R == 12 && color.Value.G == 12 && color.Value.B == 12) idx = 0; // #0C0C0C Black
                        if (idx == -1 && color.Value.R == 204 && color.Value.G == 204 && color.Value.B == 204) idx = 7; // #CCCCCC White
                        if (idx == -1 && color.Value.R == 242 && color.Value.G == 242 && color.Value.B == 242) idx = 15; // #F2F2F2 Bright White

                        // Use GetBasicColor ONLY for snapped or basic indices (0-15).
                        // For 256-color indices (16-255) or TrueColor (-1), use the raw color.Value.
                        _buffer.CurrentForeground = (idx >= 0 && idx <= 15) ? GetBasicColor(idx % 8, idx >= 8) : color.Value;
                        _buffer.CurrentFgIndex = idx;
                        _buffer.IsDefaultForeground = false;
                    }
                }
                else if (code == 39)
                {
                    _buffer.CurrentForeground = _buffer.Theme.Foreground;
                    _buffer.CurrentFgIndex = -1;
                    _buffer.IsDefaultForeground = true;
                }
                else if (code >= 40 && code <= 47)
                {
                    _buffer.CurrentBackground = GetBasicColor(code - 40);
                    _buffer.CurrentBgIndex = (short)(code - 40);
                    _buffer.IsDefaultBackground = false;
                }
                else if (code >= 100 && code <= 107)
                {
                    _buffer.CurrentBackground = GetBasicColor(code - 100, true);
                    _buffer.CurrentBgIndex = (short)((code - 100) + 8);
                    _buffer.IsDefaultBackground = false;
                }
                else if (code == 48)
                {
                    short idx = -1;
                    var color = ParseExtendedColor(args, ref i, out idx);
                    if (color.HasValue)
                    {
                        // Snapping for Campbell Blue/Black
                        if (idx == -1 && color.Value.R == 0 && color.Value.G == 55 && color.Value.B == 218) idx = 4; // #0037DA Blue
                        if (idx == -1 && color.Value.R == 58 && color.Value.G == 150 && color.Value.B == 221) idx = 4; // #3A96DD Blue (PS)
                        if (idx == -1 && color.Value.R == 12 && color.Value.G == 12 && color.Value.B == 12) idx = 0; // #0C0C0C Black
                        if (idx == -1 && color.Value.R == 204 && color.Value.G == 204 && color.Value.B == 204) idx = 7; // #CCCCCC White
                        if (idx == -1 && color.Value.R == 242 && color.Value.G == 242 && color.Value.B == 242) idx = 15; // #F2F2F2 Bright White

                        _buffer.CurrentBackground = (idx >= 0 && idx <= 15) ? GetBasicColor(idx % 8, idx >= 8) : color.Value;
                        _buffer.CurrentBgIndex = idx;
                        _buffer.IsDefaultBackground = false;
                    }
                }
                else if (code == 49)
                {
                    _buffer.CurrentBackground = _buffer.Theme.Background;
                    _buffer.CurrentBgIndex = -1;
                    _buffer.IsDefaultBackground = true;
                }
                else if (code == 1) // Bold
                {
                    _buffer.IsBold = true;
                }
                else if (code == 22) // Normal Intensity (Not Bold, Not Faint)
                {
                    _buffer.IsBold = false;
                    _buffer.IsFaint = false;
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
                else if (code == 2) // Faint
                {
                    _buffer.IsFaint = true;
                }
                else if (code == 3) // Italic
                {
                    _buffer.IsItalic = true;
                }
                else if (code == 4) // Underline
                {
                    _buffer.IsUnderline = true;
                }
                else if (code == 5) // Blink
                {
                    _buffer.IsBlink = true;
                }
                else if (code == 9) // Strikethrough
                {
                    _buffer.IsStrikethrough = true;
                }
                else if (code == 23) // No Italic
                {
                    _buffer.IsItalic = false;
                }
                else if (code == 24) // No Underline
                {
                    _buffer.IsUnderline = false;
                }
                else if (code == 25) // No Blink
                {
                    _buffer.IsBlink = false;
                }
                else if (code == 29) // No Strikethrough
                {
                    _buffer.IsStrikethrough = false;
                }
            }
        }

        private void ResetColors()
        {
            _buffer.CurrentForeground = _buffer.Theme.Foreground;
            _buffer.CurrentBackground = _buffer.Theme.Background;
            _buffer.CurrentFgIndex = -1;
            _buffer.CurrentBgIndex = -1;
            _buffer.IsInverse = false;
            _buffer.IsBold = false;
            _buffer.IsFaint = false;
            _buffer.IsItalic = false;
            _buffer.IsUnderline = false;
            _buffer.IsBlink = false;
            _buffer.IsStrikethrough = false;
            _buffer.IsHidden = false;
        }

        private TermColor? ParseExtendedColor(ReadOnlySpan<int> args, ref int i, out short index)
        {
            index = -1;
            if (i + 1 >= args.Length) return null;

            int mode = args[++i];
            if (mode == 5) // 256 colors
            {
                if (i + 1 >= args.Length) return null;
                int idx = args[++i];
                index = (short)idx; // Use the palette index!
                return GetXtermColor(idx);
            }
            else if (mode == 2) // TrueColor (Next 3 args are R, G, B)
            {
                if (i + 3 >= args.Length) return null;
                byte r = (byte)args[++i];
                byte g = (byte)args[++i];
                byte b = (byte)args[++i];
                return TermColor.FromRgb(r, g, b);
            }
            return null;
        }

        private void HandleDcs(string dcs)
        {

            // Sixel Support (DCS Ps ; Pi ; Pj q <sixel_data> ST)
            if (dcs.Contains('q'))
            {
                HandleSixel(dcs);
            }
            _dcsStringBuffer.Clear();
        }

        private void HandleSixel(string dcs)
        {
            try
            {
                var bitmap = _sixelDecoder.Decode(dcs);
                if (bitmap != null)
                {
                    // Calculate cell dimensions (rough estimate based on current font metrics)
                    int widthCells = (int)Math.Max(1, Math.Ceiling(bitmap.Width / (CellWidth > 0 ? CellWidth : 10f)));
                    int heightCells = (int)Math.Max(1, Math.Ceiling(bitmap.Height / (CellHeight > 0 ? CellHeight : 20f)));

                    int absRow = _buffer.CursorRow + (_buffer.TotalLines - _buffer.Rows);
                    if (_buffer.IsAltScreenActive) absRow = _buffer.CursorRow;

                    // Decode now returns SKImage? or we convert. Let's see SixelDecoder.
                    using var skData = SKData.CreateCopy(System.Text.Encoding.UTF8.GetBytes(dcs));
                    // Wait, SixelDecoder.Decode returns SKBitmap. We'll convert it to SKImage for TerminalImage.
                    var img = new TerminalImage(SKImage.FromBitmap(bitmap), _buffer.CursorCol, absRow, widthCells, heightCells);
                    _buffer.AddImage(img);

                    // Position cursor past the image
                    // We move the cursor relative to current position, but try to avoid redundant newlines
                    // Tools like 'viu' usually handle their own layout. 
                    // We just need to ensure the buffer knows we've "used" these cells.
                    bool oldHidden = _buffer.IsHidden;
                    _buffer.IsHidden = true;
                    try
                    {
                        for (int y = 0; y < heightCells; y++)
                        {
                            // 1. Advance horizontally on current row
                            for (int x = 0; x < widthCells; x++) _buffer.WriteContent(" ", false);

                            // 2. If more rows exist, move to next row and align to image start column
                            if (y < heightCells - 1)
                            {
                                _buffer.WriteChar('\n');
                                for (int x = 0; x < img.CellX; x++) _buffer.WriteContent(" ", false);
                            }
                        }
                    }
                    finally
                    {
                        _buffer.IsHidden = oldHidden;
                    }
                }
            }
            catch (Exception ex)
            {
                TerminalLogger.Log($"[ANSI_PARSER] Sixel decode failed: {ex.Message}");
            }
        }

        private void HandleOsc(string osc)
        {
            if (string.IsNullOrEmpty(osc)) return;
            if (osc.StartsWith("1337;File="))
            {
                HandleITerm2Image(osc);
            }
            else if (osc.StartsWith("1339;"))
            {
                string content = osc.Substring(5);
                if (content.StartsWith("Kitty:") || content.StartsWith("K:"))
                {
                    int skip = content.StartsWith("Kitty:") ? 6 : 2;
                    content = content.Substring(skip);
                    HandleKittyGraphics(content);
                }
                else
                {
                    HandleSixel(content);
                }
            }
            else
            {
            }
        }

        private void HandleApc(string content)
        {
            TerminalLogger.Log($"[ANSI_PARSER] HandleApc: {content.Substring(0, Math.Min(content.Length, 20))}...");
            // Kitty protocol uses 'G' as command identifier in APC
            if (content.StartsWith("G"))
            {
                HandleKittyGraphics(content.Substring(1));
            }
        }

        private void HandleKittyGraphics(string content)
        {
            TerminalLogger.Log($"[ANSI_PARSER] HandleKittyGraphics: {content.Substring(0, Math.Min(content.Length, 40))}...");

            // The Kitty protocol control string starts with 'G'.
            if (content.StartsWith("G")) content = content.Substring(1);

            // Format: params ; payload
            var parts = content.Split(';', 2);
            string paramsPart = parts[0];
            string payload = parts.Length > 1 ? parts[1] : "";

            // Parse params
            var kvParams = paramsPart.Split(',');
            foreach (var kv in kvParams)
            {
                var side = kv.Split('=');
                if (side.Length == 2)
                {
                    _kittyPendingParams[side[0]] = side[1];
                }
                else if (kv.Length > 0)
                {
                    // Some flags might not have '='
                    _kittyPendingParams[kv] = "1";
                }
            }

            // Accumulate payload
            _kittyPayloadBuffer.Append(payload);

            // Check if more chunks are coming (m=1)
            bool more = false;
            if (paramsPart.Contains("m=1")) more = true;
            else if (paramsPart.Contains("m=0")) more = false;
            else if (_kittyPendingParams.TryGetValue("m", out var mVal)) more = (mVal == "1");

            if (more)
            {
                TerminalLogger.Log($"[ANSI_PARSER] Kitty chunk received, waiting for more (m=1). Buffer size: {_kittyPayloadBuffer.Length}");
                return;
            }

            // Process finalized image
            try
            {
                string action = _kittyPendingParams.TryGetValue("a", out var aVal) ? aVal : "t";
                TerminalLogger.Log($"[ANSI_PARSER] Kitty finalizing image. Action={action}, TotalPayload={_kittyPayloadBuffer.Length}");

                if (action == "q")
                {
                    TerminalLogger.Log($"[ANSI_PARSER] Kitty: Handling query (a=q)");
                    // Respond that we support Kitty graphics (Standard 'OK' response)
                    // Format: \e_Gi=XX;OK\e\ where XX is the id from the request
                    string id = _kittyPendingParams.TryGetValue("i", out var idVal) ? idVal : "31";
                    OnResponse?.Invoke($"\x1b_Gi={id};OK\x1b\\");
                    ClearKittyState();
                    return;
                }

                if (action != "t" && action != "T")
                {
                    TerminalLogger.Log($"[ANSI_PARSER] Kitty: skipping unsupported action '{action}'");
                    ClearKittyState();
                    return;
                }

                string combinedPayload = _kittyPayloadBuffer.ToString();
                if (string.IsNullOrEmpty(combinedPayload)) return;

                // Handle optional 'G' prefix
                if (combinedPayload.StartsWith("G")) combinedPayload = combinedPayload.Substring(1);

                TerminalLogger.Log($"[ANSI_PARSER] Kitty payload preview: {combinedPayload.Substring(0, Math.Min(combinedPayload.Length, 20))}...");
                byte[] data = Convert.FromBase64String(combinedPayload);
                if (data.Length >= 8)
                {
                    TerminalLogger.Log($"[ANSI_PARSER] Kitty data magic: {BitConverter.ToString(data, 0, Math.Min(data.Length, 8))}, Decode");
                }

                SKImage? image = null;
                try
                {
                    using var skData = SKData.CreateCopy(data);

                    // Trace codec info
                    using (var codec = SKCodec.Create(skData))
                    {
                        if (codec != null)
                        {
                            TerminalLogger.Log($"[ANSI_PARSER] SKCodec created: {codec.EncodedFormat}, Size={codec.Info.Width}x{codec.Info.Height}");
                        }
                        else
                        {
                            TerminalLogger.Log($"[ANSI_PARSER] SKCodec.Create failed for Kitty data (skData.Size={skData.Size})");
                        }
                    }

                    image = SKImage.FromEncodedData(skData);
                    if (image == null)
                    {
                        TerminalLogger.Log("[ANSI_PARSER] SKImage.FromEncodedData returned null.");
                    }
                }
                catch (Exception ex)
                {
                    TerminalLogger.Log($"[ANSI_PARSER] SkiaSharp exception during Kitty decode: {ex.Message}");
                }

                if (image == null)
                {
                    TerminalLogger.Log("[ANSI_PARSER] SkiaSharp failed to decode Kitty image data (image was null).");
                    ClearKittyState();
                    return;
                }

                TerminalLogger.Log($"[ANSI_PARSER] Kitty image decoded successfully: {image.Width}x{image.Height}");

                // Guardrail: Limit pixel dimensions
                if (image.Width > 2000 || image.Height > 2000)
                {
                    TerminalLogger.Log($"[ANSI_PARSER] Kitty image pixel dimensions too large ({image.Width}x{image.Height}), skipping.");
                    image.Dispose();
                    ClearKittyState();
                    return;
                }

                // Mapping Kitty params to our model
                int width = 0;
                int height = 0;

                if (_kittyPendingParams.TryGetValue("c", out var cVal)) width = ParseDimension(cVal, isHeight: false);
                if (_kittyPendingParams.TryGetValue("r", out var rVal)) height = ParseDimension(rVal, isHeight: true);

                if (width == 0 && _kittyPendingParams.TryGetValue("w", out var wVal))
                {
                    string wSfx = wVal.EndsWith("px") || wVal.EndsWith("%") ? wVal : wVal + "px";
                    width = ParseDimension(wSfx, isHeight: false);
                }

                if (height == 0 && _kittyPendingParams.TryGetValue("h", out var hVal))
                {
                    string hSfx = hVal.EndsWith("px") || hVal.EndsWith("%") ? hVal : hVal + "px";
                    height = ParseDimension(hSfx, isHeight: true);
                }

                float effectiveCellWidth = CellWidth > 0 ? CellWidth : 10f;
                float effectiveCellHeight = CellHeight > 0 ? CellHeight : 20f;
                double cellRatio = (effectiveCellHeight > 0 && effectiveCellWidth > 0) ? (effectiveCellWidth / (double)effectiveCellHeight) : 0.5;
                double imageRatio = (double)image.Height / image.Width;

                if (width == 0 && height == 0)
                {
                    width = (int)Math.Max(1, Math.Ceiling(image.Width / effectiveCellWidth));
                    height = (int)Math.Max(1, Math.Ceiling(width * cellRatio * imageRatio));
                }
                else if (width == 0)
                {
                    width = (int)Math.Max(1, Math.Ceiling(height / (cellRatio * imageRatio)));
                }
                else if (height == 0)
                {
                    height = (int)Math.Max(1, Math.Ceiling(width * cellRatio * imageRatio));
                }

                width = Math.Clamp(width, 1, 200);
                height = Math.Clamp(height, 1, 200);

                int absRow = _buffer.CursorRow + (_buffer.TotalLines - _buffer.Rows);
                if (_buffer.IsAltScreenActive) absRow = _buffer.CursorRow;

                TerminalLogger.Log($"[ANSI_PARSER] Kitty image placement: CursorCol={_buffer.CursorCol}, CursorRow={_buffer.CursorRow}, absRow={absRow}, widthCells={width}, heightCells={height}, effectiveCellW={effectiveCellWidth}, effectiveCellH={effectiveCellHeight}");
                var img = new TerminalImage(image, _buffer.CursorCol, absRow, width, height);
                _buffer.AddImage(img);

                if (action == "T" || action == "t")
                {
                    bool oldHidden = _buffer.IsHidden;
                    _buffer.IsHidden = true;
                    try
                    {
                        for (int y = 0; y < height; y++)
                        {
                            // 1. Advance horizontally on current row
                            for (int i = 0; i < width; i++) _buffer.WriteContent(" ", false);

                            if (y < height - 1)
                            {
                                _buffer.WriteChar('\n');
                                for (int i = 0; i < img.CellX; i++) _buffer.WriteContent(" ", false);
                            }
                        }
                    }
                    finally
                    {
                        _buffer.IsHidden = oldHidden;
                    }
                }
            }
            catch (Exception ex)
            {
                TerminalLogger.Log($"[ANSI_PARSER] Failed to decode Kitty image: {ex.Message}");
                ClearKittyState();
            }
            finally
            {
                if (!more)
                {
                    ClearKittyState();
                }
            }
        }

        private void ClearKittyState()
        {
            _kittyPayloadBuffer.Clear();
            _kittyPendingParams.Clear();
        }

        private void HandleITerm2Image(string osc)
        {
            var parts = osc.Split(':', 2);
            if (parts.Length < 2)
            {
                return;
            }

            if (!parts[0].StartsWith("1337;File="))
            {
                return;
            }

            var argsPart = parts[0].Substring("1337;File=".Length);
            var base64Data = parts[1];

            var args = argsPart.Split(';');
            int width = 0;
            int height = 0;
            bool inline = false;

            foreach (var arg in args)
            {
                var kv = arg.Split('=');
                if (kv.Length != 2) continue;
                string key = kv[0].ToLower();
                string val = kv[1];

                if (key == "width") width = ParseDimension(val, isHeight: false);
                else if (key == "height") height = ParseDimension(val, isHeight: true);
                else if (key == "inline") inline = (val == "1");
            }

            if (!inline) return;

            try
            {
                // Guardrail: Limit base64 length to avoid excessive memory usage before decoding
                if (base64Data.Length > 10 * 1024 * 1024) // 10MB limit
                {
                    return;
                }

                byte[] data = Convert.FromBase64String(base64Data);

                using var skData = SKData.CreateCopy(data);
                SKImage? image = SKImage.FromEncodedData(skData);
                if (image == null)
                {
                    return;
                }


                // Guardrail: Limit pixel dimensions
                if (image.Width > 2000 || image.Height > 2000)
                {
                    image.Dispose();
                    return;
                }

                if (width == 0 && height == 0)
                {
                    // Default: Scale to fit 100% of image pixel width mapped to cells
                    float divisor = CellWidth > 0 ? CellWidth : 10.0f;
                    width = Math.Max(10, (int)Math.Ceiling(image.Width / divisor));

                    // Sanity check: Clamp width to terminal width to prevent massive wrapping
                    width = Math.Min(width, _buffer.Cols);

                    double cellRatio = (CellHeight > 0 && CellWidth > 0) ? (CellWidth / (double)CellHeight) : 0.5;
                    double imageRatio = (double)image.Height / image.Width;
                    height = Math.Max(1, (int)Math.Round(width * cellRatio * imageRatio));
                }
                else if (width == 0)
                {
                    double cellRatio = (CellHeight > 0 && CellWidth > 0) ? (CellWidth / (double)CellHeight) : 0.5;
                    double imageRatio = (double)image.Height / image.Width;
                    width = Math.Max(1, (int)Math.Round(height / (cellRatio * imageRatio)));
                }
                else if (height == 0)
                {
                    double cellRatio = (CellHeight > 0 && CellWidth > 0) ? (CellWidth / (double)CellHeight) : 0.5;
                    double imageRatio = (double)image.Height / image.Width;
                    height = Math.Max(1, (int)Math.Round(width * cellRatio * imageRatio));
                }

                // Guardrail: Limit cell dimensions
                width = Math.Clamp(width, 1, Math.Min(200, _buffer.Cols));
                height = Math.Clamp(height, 1, 200);

                // Calculate absolute row
                int absRow = _buffer.CursorRow + (_buffer.TotalLines - _buffer.Rows);
                if (_buffer.IsAltScreenActive) absRow = _buffer.CursorRow;

                var img = new TerminalImage(image, _buffer.CursorCol, absRow, width, height);
                _buffer.AddImage(img);

                // Finalize placement
                bool oldHidden = _buffer.IsHidden;
                int startRow = _buffer.CursorRow;
                try
                {
                    // Calculate visual lines required
                    // Note: height is in cells.
                    for (int y = 0; y < height; y++)
                    {
                        // 1. Remember row before writing
                        int rowBefore = _buffer.CursorRow;

                        // 2. Advance horizontally on current row (width spaces)
                        // If width > Cols, WriteContent handles wrapping automatically
                        for (int i = 0; i < width; i++)
                        {
                            _buffer.WriteContent(" ", false);
                        }

                        // 3. If valid row, force newline ONLY if we didn't already wrap
                        if (y < height - 1)
                        {
                            // If cursor row is same as before, we fit on the line. Force newline.
                            // If cursor row changed, we (auto) wrapped. Don't double-newline unless we are mid-line?
                            // If we wrapped exactly to start of next line, we are good.
                            // If width == Cols, we wrap to col 0 of next line.

                            if (_buffer.CursorRow == rowBefore)
                            {
                                _buffer.WriteChar('\n');
                            }

                            // Move to image start col
                            for (int i = 0; i < img.CellX; i++) _buffer.WriteContent(" ", false);
                        }
                    }


                    // Accumulate vertical offset for ConPTY sync
                    // Use actual visual cursor delta instead of image height to account for scrolling/clamping
                    int endRow = _buffer.CursorRow;
                    int delta = endRow - startRow;
                    // If we wrapped/scrolled, delta is how much further down the cursor is visually relative to start.
                    // This maps PTY (Start) -> Visual (End).
                    _verticalOffset += delta;


                    // Set flag to swallow the next newline if it comes immediately
                    _swallowNextNewline = true;
                }
                finally
                {
                    _buffer.IsHidden = oldHidden;
                }
            }
            catch (Exception ex)
            {
                TerminalLogger.Log($"[ANSI_PARSER] Failed to decode iTerm2 image: {ex.Message}");
            }
        }

        private int ParseDimension(string val, bool isHeight = false)
        {
            if (string.IsNullOrEmpty(val)) return 0;

            if (val.EndsWith("px"))
            {
                if (double.TryParse(val.Substring(0, val.Length - 2), out double px))
                {
                    // Convert pixels to cells based on current metrics
                    float metric = isHeight ? CellHeight : CellWidth;
                    return (int)Math.Max(1, Math.Ceiling(px / (metric > 0 ? metric : 10f)));
                }
            }
            if (val.EndsWith("%"))
            {
                if (double.TryParse(val.Substring(0, val.Length - 1), out double pct))
                {
                    // % of terminal width/height in cells
                    int total = isHeight ? _buffer.Rows : _buffer.Cols;
                    return (int)Math.Max(1, (total * pct / 100.0));
                }
            }

            if (int.TryParse(val, out int result)) return result;
            return 0;
        }

        private TermColor GetBasicColor(int index, bool bright = false)
        {
            return _buffer.Theme.GetAnsiColor(index, bright);
        }

        private TermColor GetXtermColor(int index)
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
                return TermColor.FromRgb(ToByte(r), ToByte(g), ToByte(b));
            }

            // 232-255: Grayscale
            if (index < 256)
            {
                index -= 232;
                byte v = (byte)(index * 10 + 8);
                return TermColor.FromRgb(v, v, v);
            }

            return TermColor.White;
        }
    }
}

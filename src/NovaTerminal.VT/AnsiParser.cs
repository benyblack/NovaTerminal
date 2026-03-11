
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

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
        private const int MaxCsiParamChars = 65536;

        // ConPTY Sync Fix: Track vertical offset caused by inline images that ConPTY doesn't see.
        // This effectively "scrolls" the PTY's logical cursor to match our visual cursor.
        private int _verticalOffset = 0;

        private List<char> _oscStringBuffer = new List<char>(); // OSC strings can be long (titles, etc) - Keep List for now or limit
        private List<char> _apcStringBuffer = new List<char>();
        private List<char> _dcsStringBuffer = new List<char>();
        private System.Text.StringBuilder _kittyPayloadBuffer = new System.Text.StringBuilder();
        private Dictionary<string, string> _kittyPendingParams = new();
        private readonly bool _isConPtyFilteringLikely;

        public IImageDecoder? ImageDecoder { get; set; }

        // M2.1: Lock Batching Buffer
        private System.Text.StringBuilder _textBuffer = new System.Text.StringBuilder(4096);
        private bool _sawCursorHideInBatch;
        private bool _sawCursorShowAfterHideInBatch;
        private static readonly TimeSpan CursorTransientSuppressionWindow = TimeSpan.FromMilliseconds(60);

        public float CellWidth { get; set; } = 10.0f;  // Default fallback
        public float CellHeight { get; set; } = 20.0f; // Default fallback
        public Action<string>? OnResponse { get; set; }
        public Action? OnBell { get; set; }
        public Action<string>? OnWorkingDirectoryChanged { get; set; }
        public Action<string>? OnTitleChanged { get; set; }
        public Action? OnPromptReady { get; set; }
        public Action<string>? OnCommandAccepted { get; set; }
        public Action? OnCommandStarted { get; set; }
        public Action<int?>? OnCommandFinished { get; set; }
        public Action<int?, long?>? OnCommandFinishedDetailed { get; set; }

        public AnsiParser(TerminalBuffer buffer, bool? forceConPtyFiltering = null)
        {
            _buffer = buffer;
            _isConPtyFilteringLikely = forceConPtyFiltering ?? DetectConPtyFiltering();
        }

        public bool IsConPtyFilteringLikely => _isConPtyFilteringLikely;

        private static bool DetectConPtyFiltering()
        {
            if (!OperatingSystem.IsWindows()) return false;

            // Today our Windows backend uses ConPTY, which strips several image control strings.
            // Keep env checks explicit so future backend changes can loosen this behavior.
            string? wt = Environment.GetEnvironmentVariable("WT_SESSION");
            string? termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM");
            if (!string.IsNullOrEmpty(wt)) return true;
            if (!string.IsNullOrEmpty(termProgram) &&
                termProgram.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return true;
        }

        private void FlushText()
        {
            if (_textBuffer.Length > 0)
            {
                _buffer.WriteContent(_textBuffer.ToString());
                _textBuffer.Clear();
            }
        }

        public void Process(string input)
        {
            if (string.IsNullOrEmpty(input)) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            _sawCursorHideInBatch = false;
            _sawCursorShowAfterHideInBatch = false;



            _buffer.EnterBatchWrite();
            try
            {
                foreach (char c in input)
                {
                    switch (_state)
                    {
                        case State.Normal:
                            if (c == '\x1b')
                            {
                                FlushText();
                                _state = State.Esc;
                            }
                            else if (c >= 0x80 && c <= 0x9F) // C1 Controls
                            {
                                FlushText();
                                switch (c)
                                {
                                    case '\u009B': _state = State.Csi; _paramLen = 0; break;
                                    case '\u0090': _state = State.Dcs; _dcsStringBuffer.Clear(); break;
                                    case '\u009D': _state = State.Osc; _oscStringBuffer.Clear(); break;
                                    case '\u009F': _state = State.Apc; _apcStringBuffer.Clear(); break;
                                    default: _buffer.WriteChar(c); break;
                                }
                            }
                            else if (c < 0x20 || c == 0x7F) // C0 Controls & DEL
                            {
                                FlushText();
                                if (c == '\a')
                                {
                                    OnBell?.Invoke();
                                }
                                else
                                {
                                    if (_swallowNextNewline)
                                    {
                                        if (c == '\r' || c == '\n')
                                        {
                                            if (c == '\n') _swallowNextNewline = false;
                                            continue;
                                        }
                                        else _swallowNextNewline = false;
                                    }
                                    _buffer.WriteChar(c);
                                }
                            }
                            else
                            {
                                // Printable Characters
                                _textBuffer.Append(c);
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
                            else if (c == '=' || c == '>') // DECKPAM / DECKPNM
                            {
                                // Keypad application/numeric mode does not render visible text.
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
                                // Collect params (grow up to a hard safety cap).
                                if (_paramLen < MaxCsiParamChars && EnsureCsiParamCapacity(_paramLen + 1))
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
                FlushText();

                if (_sawCursorShowAfterHideInBatch)
                {
                    _buffer.ExtendCursorSuppression_NoLock(CursorTransientSuppressionWindow);
                    _buffer.Invalidate();
                }
            }
            finally
            {
                _buffer.ExitBatchWrite();
                sw.Stop();
            }
        }

        private void HandleCsi(char finalByte, ReadOnlySpan<char> parameters)
        {
            // CSI format: CSI [leader] [params] [intermediates] final
            // leader: '<' '=' '>' '?'
            // params: digits + ';' + ':'
            // intermediates: 0x20..0x2F
            char leader = '\0';
            ReadOnlySpan<char> csiBody = parameters;
            if (csiBody.Length > 0)
            {
                char p0 = csiBody[0];
                if (p0 == '<' || p0 == '=' || p0 == '>' || p0 == '?')
                {
                    leader = p0;
                    csiBody = csiBody.Slice(1);
                }
            }

            int firstIntermediate = -1;
            for (int i = 0; i < csiBody.Length; i++)
            {
                char c = csiBody[i];
                if (c >= '\x20' && c <= '\x2F')
                {
                    firstIntermediate = i;
                    break;
                }
            }

            ReadOnlySpan<char> paramPart = firstIntermediate >= 0 ? csiBody.Slice(0, firstIntermediate) : csiBody;
            ReadOnlySpan<char> intermediates = firstIntermediate >= 0 ? csiBody.Slice(firstIntermediate) : ReadOnlySpan<char>.Empty;
            bool isPrivate = leader == '?';

            int[]? rentedArgs = null;
            char[]? rentedSeparators = null;
            Span<int> args;
            Span<char> separators;

            // Reserve enough room for all parameters in this CSI so trailing reset
            // codes (for example 24 = no underline) are never dropped.
            int estimatedArgs = 1;
            for (int i = 0; i < paramPart.Length; i++)
            {
                char c = paramPart[i];
                if (c == ';' || c == ':') estimatedArgs++;
            }
            estimatedArgs = Math.Clamp(estimatedArgs, 1, _paramBuffer.Length + 1);

            rentedArgs = ArrayPool<int>.Shared.Rent(estimatedArgs);
            rentedSeparators = ArrayPool<char>.Shared.Rent(estimatedArgs);
            args = rentedArgs.AsSpan(0, estimatedArgs);
            separators = rentedSeparators.AsSpan(0, estimatedArgs);

            int argCount = 0;
            int currentVal = 0;
            bool hasVal = false;

            for (int i = 0; i < paramPart.Length; i++)
            {
                char c = paramPart[i];
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
                        separators[argCount - 1] = c;
                    }
                    currentVal = 0;
                    hasVal = false;
                }
                else
                {
                    // Ignore unexpected chars in params; treat them as hard separators.
                    if (hasVal && argCount < args.Length)
                    {
                        args[argCount++] = currentVal;
                        separators[argCount - 1] = '\0';
                    }
                    currentVal = 0;
                    hasVal = false;
                }
            }
            // Add final arg
            bool paramEndsWithSeparator = paramPart.Length > 0 &&
                                          (paramPart[paramPart.Length - 1] == ';' || paramPart[paramPart.Length - 1] == ':');
            if (hasVal || paramEndsWithSeparator)
            {
                if (argCount < args.Length)
                {
                    args[argCount++] = hasVal ? currentVal : 0;
                    separators[argCount - 1] = '\0';
                }
            }

            // Slice to actual count
            ReadOnlySpan<int> validArgs = args.Slice(0, argCount);
            ReadOnlySpan<char> validSeparators = separators.Slice(0, argCount);
            int arg0 = argCount > 0 ? validArgs[0] : 0;


            try
            {
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
                            // Cursor moves to home after setting region.
                            // In DECOM, home is the top of the scrolling region.
                            int homeRow = _buffer.Modes.IsOriginMode ? _buffer.ScrollTop : 0;
                            _buffer.SetCursorPosition(0, homeRow);
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
                            if (_buffer.Modes.IsOriginMode) vpaRow += _buffer.ScrollTop;
                            // NOTE: _verticalOffset intentionally NOT applied here — it's a ConPTY
                            // image-rendering artifact that must not affect general cursor positioning.
                            _buffer.CursorRow = ClampRowForMode(vpaRow);
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

                        if (_buffer.Modes.IsOriginMode)
                        {
                            row += _buffer.ScrollTop;
                        }

                        // NOTE: _verticalOffset intentionally NOT applied here — it is a ConPTY
                        // image-rendering artifact that must not displace general TUI cursor positions.

                        row = ClampRowForMode(row);
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
                              // Ignore non-standard leader-prefixed "...m" control sequences, e.g.
                              // CSI > Pp ; Pv m (xterm key-modifier options). They are NOT SGR.
                        if (leader == '\0' && intermediates.Length == 0)
                        {
                            HandleSgr(validArgs, validSeparators);
                        }
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
                                else if (m == 12) // SRM - Send/Receive Mode (Echo)
                                {
                                    // SRM Reset (l) means Local Echo is ON.
                                    // SRM Set (h) means Local Echo is OFF.
                                    _buffer.Modes.IsEchoEnabled = !enableMode;
                                }
                                else if (m == 20) // LNM - Line Feed New Line Mode
                                {
                                    _buffer.Modes.IsLineFeedNewLineMode = enableMode;
                                }
                            }
                        }
                        break;
                    case 'c': // DA - Device Attributes
                        if (leader == '>')
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
                    case 'q':
                        // DECSCUSR - Set Cursor Style (CSI Ps SP q)
                        if (leader == '\0' && intermediates.Length > 0)
                        {
                            ApplyCursorStyle(argCount > 0 ? validArgs[0] : 0);
                        }
                        break;
                    default:
                        TerminalLogger.Log($"[ANSI_PARSER] Unhandled CSI: {finalByte} (private={isPrivate}), params={new string(parameters)}");
                        break;
                }
            }
            finally
            {
                if (rentedArgs != null) ArrayPool<int>.Shared.Return(rentedArgs);
                if (rentedSeparators != null) ArrayPool<char>.Shared.Return(rentedSeparators);
            }
        }

        private bool EnsureCsiParamCapacity(int required)
        {
            if (required <= _paramBuffer.Length) return true;
            if (_paramBuffer.Length >= MaxCsiParamChars) return false;

            int next = _paramBuffer.Length;
            while (next < required && next < MaxCsiParamChars)
            {
                next *= 2;
            }

            next = Math.Min(next, MaxCsiParamChars);
            if (next < required) return false;

            Array.Resize(ref _paramBuffer, next);
            return true;
        }

        private void ApplyCursorStyle(int value)
        {
            // DECSCUSR:
            // 0/1 blinking block, 2 steady block, 3 blinking underline, 4 steady underline, 5 blinking bar, 6 steady bar
            switch (value)
            {
                case 0:
                case 1:
                    _buffer.Modes.CursorStyle = CursorStyle.Block;
                    _buffer.Modes.IsCursorBlinkEnabled = true;
                    break;
                case 2:
                    _buffer.Modes.CursorStyle = CursorStyle.Block;
                    _buffer.Modes.IsCursorBlinkEnabled = false;
                    break;
                case 3:
                    _buffer.Modes.CursorStyle = CursorStyle.Underline;
                    _buffer.Modes.IsCursorBlinkEnabled = true;
                    break;
                case 4:
                    _buffer.Modes.CursorStyle = CursorStyle.Underline;
                    _buffer.Modes.IsCursorBlinkEnabled = false;
                    break;
                case 5:
                    _buffer.Modes.CursorStyle = CursorStyle.Beam;
                    _buffer.Modes.IsCursorBlinkEnabled = true;
                    break;
                case 6:
                    _buffer.Modes.CursorStyle = CursorStyle.Beam;
                    _buffer.Modes.IsCursorBlinkEnabled = false;
                    break;
            }
        }

        private int ClampRowForMode(int row)
        {
            if (_buffer.Modes.IsOriginMode)
            {
                return Math.Clamp(row, _buffer.ScrollTop, _buffer.ScrollBottom);
            }
            return Math.Clamp(row, 0, _buffer.Rows - 1);
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
                    case 6: // DECOM - Origin Mode
                        _buffer.Modes.IsOriginMode = enable;
                        // On DECOM change, move cursor to home for that mode.
                        _buffer.SetCursorPosition(0, enable ? _buffer.ScrollTop : 0);
                        break;
                    case 7: // DECAWM - Auto Wrap Mode
                        _buffer.Modes.IsAutoWrapMode = enable;
                        break;
                    case 1004: // FocusIn/FocusOut event reporting
                        _buffer.Modes.IsFocusEventReporting = enable;
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
                        if (!enable)
                        {
                            _sawCursorHideInBatch = true;
                        }
                        else if (_sawCursorHideInBatch)
                        {
                            _sawCursorShowAfterHideInBatch = true;
                        }

                        _buffer.Modes.IsCursorVisible = enable;
                        _buffer.Invalidate();
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

        private void HandleSgr(ReadOnlySpan<int> args, ReadOnlySpan<char> separators)
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
                    var color = ParseExtendedColor(args, separators, ref i, out idx);
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
                    var color = ParseExtendedColor(args, separators, ref i, out idx);
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
                else if (code == 58)
                {
                    // Underline color. We currently don't render underline color separately,
                    // but we MUST consume its parameters so subvalues don't leak as SGR codes.
                    short _;
                    ParseExtendedColor(args, separators, ref i, out _);
                }
                else if (code == 59)
                {
                    // Default underline color. No-op for now.
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
                else if (code == 4) // Underline / Underline Style (4:0-5)
                {
                    // Support colon-form subparameters (e.g. 4:2) without misinterpreting
                    // the style selector as a standalone SGR code (notably 2=faint).
                    if (i < args.Length - 1 && i < separators.Length && separators[i] == ':')
                    {
                        int underlineStyle = args[i + 1];
                        _buffer.IsUnderline = underlineStyle != 0;
                        i++; // consume subparameter
                    }
                    else
                    {
                        _buffer.IsUnderline = true;
                    }
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

        private TermColor? ParseExtendedColor(ReadOnlySpan<int> args, ReadOnlySpan<char> separators, ref int i, out short index)
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
                // Colon form may carry an optional colorspace-id parameter:
                // 38:2:<cs>:R:G:B and common omitted form 38:2::R:G:B.
                // If present, consume it so RGB doesn't shift/leak.
                bool modeWasColonDelimited = i < separators.Length && separators[i] == ':';
                if (modeWasColonDelimited && i + 4 < args.Length)
                {
                    i++; // skip colorspace-id (or omitted 0 placeholder from "::")
                }

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
                if (ImageDecoder == null) return;

                object? imageHandle = ImageDecoder.DecodeSixel(dcs, out int pixelWidth, out int pixelHeight);
                if (imageHandle != null)
                {
                    // Calculate cell dimensions (rough estimate based on current font metrics)
                    int widthCells = (int)Math.Max(1, Math.Ceiling(pixelWidth / (CellWidth > 0 ? CellWidth : 10f)));
                    int heightCells = (int)Math.Max(1, Math.Ceiling(pixelHeight / (CellHeight > 0 ? CellHeight : 20f)));

                    int absRow = _buffer.CursorRow + (_buffer.TotalLines - _buffer.Rows);
                    if (_buffer.IsAltScreenActive) absRow = _buffer.CursorRow;

                    var img = new TerminalImage(imageHandle, _buffer.CursorCol, absRow, widthCells, heightCells);
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
            if (osc.StartsWith("1337;File=", StringComparison.Ordinal))
            {
                HandleITerm2Image(osc);
                return;
            }

            if (osc.StartsWith("1339;", StringComparison.Ordinal))
            {
                string content = osc.Substring(5);
                if (content.StartsWith("Kitty:", StringComparison.Ordinal) || content.StartsWith("K:", StringComparison.Ordinal))
                {
                    int skip = content.StartsWith("Kitty:", StringComparison.Ordinal) ? 6 : 2;
                    content = content.Substring(skip);
                    HandleKittyGraphics(content, isTunneled: true);
                }
                else
                {
                    HandleSixel(content);
                }
                return;
            }

            int split = osc.IndexOf(';');
            if (split <= 0) return;

            string code = osc.Substring(0, split);
            string data = osc.Substring(split + 1);

            // OSC 0/2: Window title
            if (code == "0" || code == "2")
            {
                if (!string.IsNullOrWhiteSpace(data))
                {
                    OnTitleChanged?.Invoke(data.Trim());
                }
                return;
            }

            // OSC 7: current working directory URI
            if (code == "7")
            {
                if (TryExtractPathFromOsc7(data, out var cwd))
                {
                    OnWorkingDirectoryChanged?.Invoke(cwd);
                }
                return;
            }

            // OSC 133: shell integration markers.
            // Common terminals/shell integrations emit:
            //   OSC 133;A     -> prompt ready
            //   OSC 133;B   -> command started
            //   OSC 133;C;X -> command accepted with base64-encoded command X
            //   OSC 133;D;N[;M] -> command finished with exit code N and optional duration M
            if (code == "133")
            {
                if (string.IsNullOrWhiteSpace(data)) return;

                string[] parts = data.Split(';', StringSplitOptions.None);
                string marker = parts[0];
                if (string.Equals(marker, "A", StringComparison.Ordinal))
                {
                    OnPromptReady?.Invoke();
                }
                else if (string.Equals(marker, "B", StringComparison.Ordinal))
                {
                    OnCommandStarted?.Invoke();
                }
                else if (string.Equals(marker, "C", StringComparison.Ordinal))
                {
                    if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
                    {
                        try
                        {
                            byte[] bytes = Convert.FromBase64String(parts[1]);
                            string commandText = Encoding.UTF8.GetString(bytes);
                            if (!string.IsNullOrWhiteSpace(commandText))
                            {
                                OnCommandAccepted?.Invoke(commandText);
                            }
                        }
                        catch
                        {
                            // Ignore malformed command payloads and preserve terminal behavior.
                        }
                    }
                }
                else if (string.Equals(marker, "D", StringComparison.Ordinal))
                {
                    int? exitCode = null;
                    long? durationMs = null;
                    if (parts.Length > 1 &&
                        int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                    {
                        exitCode = parsed;
                    }

                    if (parts.Length > 2 &&
                        long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedDuration))
                    {
                        durationMs = parsedDuration;
                    }

                    OnCommandFinished?.Invoke(exitCode);
                    OnCommandFinishedDetailed?.Invoke(exitCode, durationMs);
                }

                return;
            }

            // OSC 8: hyperlinks (open/close)
            // Format: OSC 8 ; params ; URI ST/BEL
            if (code == "8")
            {
                int secondSep = data.IndexOf(';');
                if (secondSep >= 0)
                {
                    string uri = data.Substring(secondSep + 1);
                    _buffer.CurrentHyperlink = string.IsNullOrWhiteSpace(uri) ? null : uri;
                }
            }
        }

        private static bool TryExtractPathFromOsc7(string data, out string path)
        {
            path = string.Empty;
            if (string.IsNullOrWhiteSpace(data)) return false;

            if (Uri.TryCreate(data, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                path = Uri.UnescapeDataString(uri.LocalPath);
                return !string.IsNullOrWhiteSpace(path);
            }

            path = data.Trim();
            return !string.IsNullOrWhiteSpace(path);
        }

        private void HandleApc(string content)
        {
            TerminalLogger.Log($"[ANSI_PARSER] HandleApc: {content.Substring(0, Math.Min(content.Length, 20))}...");
            // Kitty protocol uses 'G' as command identifier in APC
            if (content.StartsWith("G"))
            {
                HandleKittyGraphics(content.Substring(1), isTunneled: false);
            }
        }

        private void HandleKittyGraphics(string content, bool isTunneled)
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
                    // If we are likely under ConPTY and this is non-tunneled Kitty APC, advertise
                    // unsupported so clients can choose Sixel or other fallback.
                    string id = _kittyPendingParams.TryGetValue("i", out var idVal) ? idVal : "31";
                    string status = (_isConPtyFilteringLikely && !isTunneled) ? "ERR" : "OK";
                    OnResponse?.Invoke($"\x1b_Gi={id};{status}\x1b\\");
                    ClearKittyState();
                    return;
                }

                if (_isConPtyFilteringLikely && !isTunneled)
                {
                    TerminalLogger.Log("[ANSI_PARSER] Kitty non-tunneled image skipped due to likely ConPTY filtering.");
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

                if (ImageDecoder == null)
                {
                    TerminalLogger.Log("[ANSI_PARSER] IImageDecoder is null, cannot decode Kitty image.");
                    ClearKittyState();
                    return;
                }

                object? imageHandle = ImageDecoder.DecodeImageBytes(data, out int pixelWidth, out int pixelHeight);
                if (imageHandle == null)
                {
                    TerminalLogger.Log("[ANSI_PARSER] IImageDecoder failed to decode Kitty image data.");
                    ClearKittyState();
                    return;
                }

                TerminalLogger.Log($"[ANSI_PARSER] Kitty image decoded successfully: {pixelWidth}x{pixelHeight}");

                // Guardrail: Limit pixel dimensions
                if (pixelWidth > 2000 || pixelHeight > 2000)
                {
                    TerminalLogger.Log($"[ANSI_PARSER] Kitty image pixel dimensions too large ({pixelWidth}x{pixelHeight}), skipping.");
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
                double imageRatio = (double)pixelHeight / pixelWidth;

                if (width == 0 && height == 0)
                {
                    width = (int)Math.Max(1, Math.Ceiling(pixelWidth / effectiveCellWidth));
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
                var img = new TerminalImage(imageHandle, _buffer.CursorCol, absRow, width, height);
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

                if (ImageDecoder == null) return;

                object? imageHandle = ImageDecoder.DecodeImageBytes(data, out int pixelWidth, out int pixelHeight);
                if (imageHandle == null) return;

                // Guardrail: Limit pixel dimensions
                if (pixelWidth > 2000 || pixelHeight > 2000)
                {
                    return;
                }

                if (width == 0 && height == 0)
                {
                    // Default: Scale to fit 100% of image pixel width mapped to cells
                    float divisor = CellWidth > 0 ? CellWidth : 10.0f;
                    width = Math.Max(10, (int)Math.Ceiling(pixelWidth / divisor));

                    // Sanity check: Clamp width to terminal width to prevent massive wrapping
                    width = Math.Min(width, _buffer.Cols);

                    double cellRatio = (CellHeight > 0 && CellWidth > 0) ? (CellWidth / (double)CellHeight) : 0.5;
                    double imageRatio = (double)pixelHeight / pixelWidth;
                    height = Math.Max(1, (int)Math.Round(width * cellRatio * imageRatio));
                }
                else if (width == 0)
                {
                    double cellRatio = (CellHeight > 0 && CellWidth > 0) ? (CellWidth / (double)CellHeight) : 0.5;
                    double imageRatio = (double)pixelHeight / pixelWidth;
                    width = Math.Max(1, (int)Math.Round(height / (cellRatio * imageRatio)));
                }
                else if (height == 0)
                {
                    double cellRatio = (CellHeight > 0 && CellWidth > 0) ? (CellWidth / (double)CellHeight) : 0.5;
                    double imageRatio = (double)pixelHeight / pixelWidth;
                    height = Math.Max(1, (int)Math.Round(width * cellRatio * imageRatio));
                }

                // Guardrail: Limit cell dimensions
                width = Math.Clamp(width, 1, Math.Min(200, _buffer.Cols));
                height = Math.Clamp(height, 1, 200);

                // Calculate absolute row
                int absRow = _buffer.CursorRow + (_buffer.TotalLines - _buffer.Rows);
                if (_buffer.IsAltScreenActive) absRow = _buffer.CursorRow;

                var img = new TerminalImage(imageHandle, _buffer.CursorCol, absRow, width, height);
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

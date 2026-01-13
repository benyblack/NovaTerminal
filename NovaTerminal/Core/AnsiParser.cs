using Avalonia.Media;
using System;
using System.Collections.Generic;

namespace NovaTerminal.Core
{
    public class AnsiParser
    {
        private TerminalBuffer _buffer;
        private enum State { Normal, Esc, Csi }
        private State _state = State.Normal;
        private List<char> _paramBuffer = new List<char>();

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
                        else _buffer.WriteChar(c);
                        break;

                    case State.Esc:
                        if (c == '[')
                        {
                            _state = State.Csi;
                            _paramBuffer.Clear();
                        }
                        else
                        {
                            // Unsupported escape sequence, just reset or print?
                            // For simplicity, print the Esc char and this char?
                            // Or ignore.
                            _state = State.Normal;
                            // _buffer.WriteChar(c); // Be careful echoing control chars
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
            string[] parts = paramsStr.Split(';', StringSplitOptions.RemoveEmptyEntries);
            int[] args = new int[parts.Length];
            for(int i=0; i<parts.Length; i++) int.TryParse(parts[i], out args[i]);

            int arg0 = args.Length > 0 ? args[0] : 0; // Default 0 varies by command

            switch (finalByte)
            {
                case 'J': // Erase in Display
                    if (arg0 == 2) _buffer.Clear();
                    break;
                case 'A': // Cursor Up
                    // TODO: Buffer should expose cursor movement methods
                     _buffer.CursorRow = Math.Max(0, _buffer.CursorRow - Math.Max(1, arg0));
                    break;
                case 'B': // Cursor Down
                     _buffer.CursorRow = Math.Min(_buffer.Rows - 1, _buffer.CursorRow + Math.Max(1, arg0));
                    break;
                case 'C': // Cursor Forward
                     _buffer.CursorCol = Math.Min(_buffer.Cols - 1, _buffer.CursorCol + Math.Max(1, arg0));
                    break;
                case 'D': // Cursor Back
                     _buffer.CursorCol = Math.Max(0, _buffer.CursorCol - Math.Max(1, arg0));
                    break;
                case 'H': // Cursor Position (row;col)
                case 'f':
                    int row = (args.Length > 0 ? args[0] : 1) - 1;
                    int col = (args.Length > 1 ? args[1] : 1) - 1;
                    _buffer.CursorRow = Math.Clamp(row, 0, _buffer.Rows - 1);
                    _buffer.CursorCol = Math.Clamp(col, 0, _buffer.Cols - 1);
                    break;
                case 'm': // SGR (Select Graphic Rendition)
                    HandleSgr(args);
                    break;
            }
        }

        private void HandleSgr(int[] args)
        {
            if (args.Length == 0)
            {
                // Reset
                _buffer.CurrentForeground = Colors.LightGray;
                _buffer.CurrentBackground = Colors.Black;
                return;
            }

            foreach (var code in args)
            {
                if (code == 0)
                {
                    _buffer.CurrentForeground = Colors.LightGray;
                    _buffer.CurrentBackground = Colors.Black;
                }
                else if (code >= 30 && code <= 37) // Foreground
                {
                    _buffer.CurrentForeground = GetColor(code - 30);
                }
                else if (code >= 40 && code <= 47) // Background
                {
                    _buffer.CurrentBackground = GetColor(code - 40);
                }
                // Extended colors (38/48) TODO later
            }
        }

        private Color GetColor(int index)
        {
            switch(index)
            {
                case 0: return Colors.Black;
                case 1: return Colors.Red;
                case 2: return Colors.Green;
                case 3: return Colors.Yellow;
                case 4: return Colors.Blue;
                case 5: return Colors.Magenta;
                case 6: return Colors.Cyan;
                case 7: return Colors.White;
                default: return Colors.White;
            }
        }
    }
}

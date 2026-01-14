using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NovaTerminal.Core
{
    public class TerminalBuffer
    {
        // Active viewport - what ConPTY writes to (fixed size)
        private TerminalRow[] _viewport;
        
        // Scrollback buffer - historical lines that scrolled off the top
        private List<TerminalRow> _scrollback = new List<TerminalRow>();
        private const int MaxScrollbackLines = 10000;
        
        public int Cols { get; private set; }
        public int Rows { get; private set; }
        
        public int CursorCol { get; set; }
        public int CursorRow { get; set; } // Row within viewport (0 to Rows-1)
        
        public IReadOnlyList<TerminalRow> ScrollbackRows => _scrollback;
        public int TotalLines => _scrollback.Count + Rows;
        
        // Track previous position for auto-clear heuristic
        private int _prevCursorCol = 0;
        private int _prevCursorRow = 0;
        private int _maxColThisRow = 0; // Track furthest column written on current row

        public Color CurrentForeground { get; set; } = Colors.LightGray;
        public Color CurrentBackground { get; set; } = Colors.Black;
        public bool IsInverse { get; set; }
        public bool IsBold { get; set; }

        // Mouse reporting modes (for TUI apps like vim, htop)
        public bool MouseModeX10 { get; set; }          // ?1000 - X10 mouse reporting
        public bool MouseModeButtonEvent { get; set; }  // ?1002 - Button event tracking
        public bool MouseModeAnyEvent { get; set; }     // ?1003 - Any event tracking
        public bool MouseModeSGR { get; set; }          // ?1006 - SGR extended mode

        public event Action? OnInvalidate;

        public TerminalBuffer(int cols, int rows)
        {
            Cols = cols;
            Rows = rows;
            
            // Create fixed-size viewport
            _viewport = new TerminalRow[rows];
            for (int i = 0; i < rows; i++)
            {
                _viewport[i] = new TerminalRow(cols);
            }
            
            CursorRow = 0;
            CursorCol = 0;
        }

        public void Clear()
    {
        // Clear viewport
        for (int i = 0; i < Rows; i++)
        {
            _viewport[i] = new TerminalRow(Cols);
        }
        
        CursorCol = 0;
        CursorRow = 0;
        IsInverse = false;
        IsBold = false;
        
        // NOTE: Do NOT reset mouse modes here!
        // Mouse modes should only change via DEC private mode sequences,
        // not from screen clearing operations (htop clears screen after enabling mouse)
        
        OnInvalidate?.Invoke();
    }

        public void Invalidate()
        {
            OnInvalidate?.Invoke();
        }

        /// <summary>
        /// Checks if any mouse reporting mode is active.
        /// </summary>
        public bool IsMouseReportingActive()
        {
            return MouseModeX10 || MouseModeButtonEvent || MouseModeAnyEvent;
        }

        public void WriteChar(char c)
        {
            // Clamp cursor to viewport
            CursorRow = Math.Clamp(CursorRow, 0, Rows - 1);
            CursorCol = Math.Clamp(CursorCol, 0, Cols - 1);
            
            // Auto-clear heuristic removed
            
            // Track row changes
            if (CursorRow != _prevCursorRow)
            {
                _maxColThisRow = 0; // Reset max column for new row
            }

            if (c == '\r') 
            {
                CursorCol = 0;
                _prevCursorCol = CursorCol;
                _prevCursorRow = CursorRow;
                OnInvalidate?.Invoke();
                return;
            }
            
            if (c == '\n') 
            {
                // Mark current line as not wrapped
                if (CursorRow >= 0 && CursorRow < Rows)
                {
                    _viewport[CursorRow].IsWrapped = false;
                }
                
                CursorCol = 0;
                CursorRow++;
                
                // If we're past the bottom, scroll
                if (CursorRow >= Rows)
                {
                    ScrollUp();
                    CursorRow = Rows - 1; // Stay at bottom row
                }
                
                return;
            }
            
            if (c == '\b') 
            {
                if (CursorCol > 0) CursorCol--;
                OnInvalidate?.Invoke();
                return;
            }
            
            if (c == '\t')
            {
                 int spaces = 4 - (CursorCol % 4);
                 for (int i=0; i<spaces;i++) WriteChar(' ');
                 return;
            }

            // Wrap if needed
            if (CursorCol >= Cols)
            {
                if (CursorRow >= 0 && CursorRow < Rows)
                {
                    _viewport[CursorRow].IsWrapped = true;
                }
                
                CursorCol = 0;
                CursorRow++;
                
                if (CursorRow >= Rows)
                {
                    ScrollUp();
                    CursorRow = Rows - 1;
                }
            }

            // Apply attributes
            Color fg = CurrentForeground;
            Color bg = CurrentBackground;

            if (IsInverse)
            {
                var tmp = fg;
                fg = bg;
                bg = tmp;
            }

            // Write to viewport
            if (CursorRow >= 0 && CursorRow < Rows && CursorCol >= 0 && CursorCol < Cols)
            {
                _viewport[CursorRow].Cells[CursorCol] = new TerminalCell(c, fg, bg);
                
                // Track max column written on this row
                if (CursorCol > _maxColThisRow) _maxColThisRow = CursorCol;
                
                CursorCol++;
                OnInvalidate?.Invoke();
            }
            else
            {
                CursorCol++; 
            }
            
            // Update tracking
            _prevCursorCol = CursorCol;
            _prevCursorRow = CursorRow;
        }

        /// <summary>
        /// Scrolls the viewport up by one line, moving top line to scrollback
        /// </summary>
        private void ScrollUp()
        {
            // Move top line to scrollback
            _scrollback.Add(_viewport[0]);
            
            // Trim scrollback if needed
            if (_scrollback.Count > MaxScrollbackLines)
            {
                _scrollback.RemoveAt(0);
            }
            
            // Shift viewport up
            for (int i = 0; i < Rows - 1; i++)
            {
                _viewport[i] = _viewport[i + 1];
            }
            
            // Create new blank line at bottom
            _viewport[Rows - 1] = new TerminalRow(Cols);
        }

        public void Write(string text)
        {
            foreach (char c in text) WriteChar(c);
        }

        public void Resize(int newCols, int newRows)
        {
            if (newCols == Cols && newRows == Rows) return;

            // For simplicity with ConPTY, just recreate the viewport
            // ConPTY will redraw everything after resize anyway
            Cols = newCols;
            Rows = newRows;
            
            // Recreate viewport
            _viewport = new TerminalRow[newRows];
            for (int i = 0; i < newRows; i++)
            {
                _viewport[i] = new TerminalRow(newCols);
            }
            
            // Clamp cursor
            CursorRow = Math.Clamp(CursorRow, 0, Rows - 1);
            CursorCol = Math.Clamp(CursorCol, 0, Cols - 1);
            
            OnInvalidate?.Invoke();
        }

        public TerminalCell GetCell(int col, int fieldRow, int scrollOffset = 0) 
        {
            // fieldRow is the visual row (0 to Rows-1)
            // We show: [scrollback tail] + [viewport]
            
            int totalLines = _scrollback.Count + Rows;
            // Visible Top Index = Total - Rows - Offset
            int displayStart = Math.Max(0, totalLines - Rows - scrollOffset);
            
            int actualIndex = displayStart + fieldRow;
            
            if (actualIndex < _scrollback.Count)
            {
                // Reading from scrollback
                if (col < 0 || col >= Cols) return TerminalCell.Default;
                return _scrollback[actualIndex].Cells[col];
            }
            else
            {
                // Reading from viewport
                int viewportRow = actualIndex - _scrollback.Count;
                if (viewportRow < 0 || viewportRow >= Rows) return TerminalCell.Default;
                if (col < 0 || col >= Cols) return TerminalCell.Default;
                return _viewport[viewportRow].Cells[col];
            }
        }
        
        public int GetVisualCursorRow(int scrollOffset = 0)
        {
            // Cursor is at _scrollback.Count + CursorRow
            // Visible Start is _scrollback.Count + Rows - Rows - scrollOffset
            
            // Logical Index of Cursor:
            // int cursorAbsIndex = _scrollback.Count + CursorRow;
            // Logical Index of Screen Top:
            // int screenTopAbsIndex = (_scrollback.Count) - scrollOffset; 
            
            // Visual Row = CursorAbs - ScreenTop
            // = (_scrollback.Count + CursorRow) - (_scrollback.Count - scrollOffset)
            // = CursorRow + scrollOffset
            
            return CursorRow + scrollOffset;
        }
        // Saved Cursor State (DEC SC / DEC RC)
        private int _savedCursorRow;
        private int _savedCursorCol;
        private Color _savedForeground = Colors.LightGray;
        private Color _savedBackground = Colors.Black;
        private bool _savedIsInverse;
        private bool _savedIsBold;

        public void SaveCursor()
        {
            _savedCursorRow = CursorRow;
            _savedCursorCol = CursorCol;
            _savedForeground = CurrentForeground;
            _savedBackground = CurrentBackground;
            _savedIsInverse = IsInverse;
            _savedIsBold = IsBold;
        }

        public void RestoreCursor()
        {
            CursorRow = Math.Clamp(_savedCursorRow, 0, Rows - 1); 
            CursorCol = Math.Clamp(_savedCursorCol, 0, Cols - 1);
            CurrentForeground = _savedForeground;
            CurrentBackground = _savedBackground;
            IsInverse = _savedIsInverse;
            IsBold = _savedIsBold;
        }

        public void EraseLineToEnd()
        {
            if (CursorRow < 0 || CursorRow >= Rows) return;
            var row = _viewport[CursorRow];
            
            for (int i = CursorCol; i < Cols; i++)
            {
                row.Cells[i] = new TerminalCell(' ', Colors.LightGray, Colors.Black);
            }
            OnInvalidate?.Invoke();
        }

        public void EraseLineFromStart()
        {
            if (CursorRow < 0 || CursorRow >= Rows) return;
            var row = _viewport[CursorRow];
            for (int i = 0; i <= CursorCol; i++)
            {
                 row.Cells[i] = TerminalCell.Default;
            }
            OnInvalidate?.Invoke();
        }

        public void EraseLineAll()
        {
            if (CursorRow < 0 || CursorRow >= Rows) return;
            var row = _viewport[CursorRow];
            for (int i = 0; i < Cols; i++)
            {
                 row.Cells[i] = TerminalCell.Default;
            }
            OnInvalidate?.Invoke();
        }

        public void EraseCharacters(int count)
        {
            if (CursorRow < 0 || CursorRow >= Rows) return;
            var row = _viewport[CursorRow];
            
            for (int i = 0; i < count; i++)
            {
                int col = CursorCol + i;
                if (col >= Cols) break;
                
                // VT100/Xterm usually erase to current background color
                row.Cells[col] = new TerminalCell(' ', CurrentForeground, CurrentBackground);
            }
            OnInvalidate?.Invoke();
        }
    }
}

using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NovaTerminal.Core
{
    public class TerminalBuffer
    {
        private List<TerminalRow> _lines = new List<TerminalRow>();
        
        public int Cols { get; private set; }
        public int Rows { get; private set; }
        
        public int CursorCol { get; set; }
        public int CursorRow { get; set; } // Absolute Row Index

        public Color CurrentForeground { get; set; } = Colors.LightGray;
        public Color CurrentBackground { get; set; } = Colors.Black;
        public bool IsInverse { get; set; }
        public bool IsBold { get; set; }

        public event Action? OnInvalidate;

        public TerminalBuffer(int cols, int rows)
        {
            Cols = cols;
            Rows = rows;
            // distinct from pre-filling: Start with just one empty line to write to.
            AddNewLine();
            CursorRow = 0;
            CursorCol = 0;
        }

        private void AddNewLine()
        {
            _lines.Add(new TerminalRow(Cols));
        }

        public void Clear()
        {
            _lines.Clear();
            AddNewLine();
            CursorCol = 0;
            CursorRow = 0;
            IsInverse = false;
            IsBold = false;
            OnInvalidate?.Invoke();
        }

        public void WriteChar(char c)
        {
            while (CursorRow >= _lines.Count) AddNewLine();

            if (c == '\r') 
            {
                CursorCol = 0;
                return;
            }
            if (c == '\n') 
            {
                // Explicit newline -> Current line is NOT wrapped
                if (CursorRow < _lines.Count) _lines[CursorRow].IsWrapped = false;
                
                CursorCol = 0;
                CursorRow++;
                if (CursorRow >= _lines.Count) AddNewLine();
                return;
            }
            if (c == '\b') 
            {
                if (CursorCol > 0) CursorCol--;
                return;
            }
            if (c == '\t')
            {
                 int spaces = 4 - (CursorCol % 4);
                 for (int i=0; i<spaces;i++) WriteChar(' ');
                 return;
            }

            // Wrap
            if (CursorCol >= Cols)
            {
                _lines[CursorRow].IsWrapped = true;
                
                CursorCol = 0;
                CursorRow++;
                 if (CursorRow >= _lines.Count) AddNewLine();
            }

            // Apply Attributes
            Color fg = CurrentForeground;
            Color bg = CurrentBackground;
            
            // Adjust for Bold (If generic colors, brighten them. If Extended/RGB, usually ignored or handled elsewhere, but we can try)
            // For now, simpler: AnsiParser already handles Bold by picking Bright color indices. 
            // So IsBold is mostly for "Text Weight" if we supported it, or re-brightening standard colors.
            // Let's rely on AnsiParser setting the correct color for now.

            if (IsInverse)
            {
                var tmp = fg;
                fg = bg;
                bg = tmp;
            }

            _lines[CursorRow].Cells[CursorCol] = new TerminalCell(c, fg, bg);
            CursorCol++; 
            OnInvalidate?.Invoke();
        }

        public void Write(string text)
        {
            foreach (char c in text) WriteChar(c);
        }

        public void Resize(int newCols, int newRows)
        {
            if (newCols == Cols && newRows == Rows) return;

            // Reflow Algorithm
            if (newCols != Cols)
            {
                var logicalLines = new List<List<TerminalCell>>();
                List<TerminalCell> currentLogicalLine = new List<TerminalCell>();

                foreach (var row in _lines)
                {
                    // Add cells, but careful with trailing "empty" ones if this line is NOT wrapped.
                    // If Wrapped, we MUST include everything (even spaces) because they might be significant padding.
                    // If Not Wrapped, the trailing spaces are just "end of line".
                    
                    var cells = row.Cells;
                    int length = cells.Length;
                    
                    if (!row.IsWrapped)
                    {
                        // Trim trailing default cells
                        while (length > 0)
                        {
                            var last = cells[length - 1];
                            if (last.Character == ' ' && last.Foreground == TerminalCell.Default.Foreground && last.Background == TerminalCell.Default.Background)
                            {
                                length--;
                            }
                            else
                            {
                                break; 
                            }
                        }
                    }
                    else
                    {
                        // Optimization: Even if wrapped, if it wrapped on pure whitespace... 
                        // But usually we keep it all.
                        // Actually, if we shrink width, simple wrapping happens.
                        // Let's assume wrapped lines are fully significant.
                    }

                    for(int i=0; i<length; i++) currentLogicalLine.Add(cells[i]);

                    if (!row.IsWrapped)
                    {
                        logicalLines.Add(currentLogicalLine);
                        currentLogicalLine = new List<TerminalCell>();
                    }
                }
                if (currentLogicalLine.Count > 0) logicalLines.Add(currentLogicalLine);

                // Rebuild
                _lines.Clear();
                CursorRow = 0;
                CursorCol = 0;
                
                int oldCols = Cols;
                Cols = newCols; 
                
                // Need to ensure at least one line exists for empty buffer
                if (logicalLines.Count == 0 && _lines.Count == 0) AddNewLine();

                foreach (var logicalLine in logicalLines)
                {
                    // Flow this logical line into new rows
                    for (int i = 0; i < logicalLine.Count; i++)
                    {
                        EnsureCursorLine();
                        if (CursorCol >= Cols)
                        {
                            _lines[CursorRow].IsWrapped = true;
                            CursorCol = 0;
                            CursorRow++;
                            EnsureCursorLine();
                        }
                         _lines[CursorRow].Cells[CursorCol] = logicalLine[i];
                         CursorCol++;
                    }
                    
                    // End of logical line -> Explicit Newline
                     EnsureCursorLine();
                     _lines[CursorRow].IsWrapped = false;
                     CursorCol = 0;
                     CursorRow++;
                }

                if (_lines.Count == 0) AddNewLine();
            }

            Cols = newCols;
            Rows = newRows;
            OnInvalidate?.Invoke();
        }

        private void EnsureCursorLine()
        {
             while (CursorRow >= _lines.Count) AddNewLine();
        }

        public TerminalCell GetCell(int col, int fieldRow) 
        {
            int totalLines = _lines.Count;
            // Default: Scroll to bottom
            int startLine = 0;
            if (totalLines > Rows) startLine = totalLines - Rows;
            
            // If totalLines < Rows (Screen has empty space), we want lines to start at TOP (0).
            // startLine is already 0.
            // But if we return Default when index >= totalLines, it renders black.
            // This is correct behavior for "Top Aligned".
            
            int actualRowIndex = startLine + fieldRow;

            if (actualRowIndex < 0 || actualRowIndex >= totalLines) return TerminalCell.Default;
            if (col < 0 || col >= Cols) return TerminalCell.Default;
            
            return _lines[actualRowIndex].Cells[col];
        }
        
        public int GetVisualCursorRow()
        {
             int totalLines = _lines.Count;
             int startLine = 0;
             if (totalLines > Rows) startLine = totalLines - Rows;
             return CursorRow - startLine;
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
            CursorRow = Math.Clamp(_savedCursorRow, 0, _lines.Count); 
            CursorCol = Math.Clamp(_savedCursorCol, 0, Cols - 1);
            CurrentForeground = _savedForeground;
            CurrentBackground = _savedBackground;
            IsInverse = _savedIsInverse;
            IsBold = _savedIsBold;
        }

        public void EraseLineToEnd()
        {
            if (CursorRow >= _lines.Count) return;
            var row = _lines[CursorRow];
            for (int i = CursorCol; i < Cols; i++)
            {
                row.Cells[i] = new TerminalCell(' ', CurrentForeground, CurrentBackground);
            }
            OnInvalidate?.Invoke();
        }

        public void EraseLineFromStart()
        {
            if (CursorRow >= _lines.Count) return;
            var row = _lines[CursorRow];
            for (int i = 0; i <= CursorCol; i++) // Inclusive of cursor often? Standard is "start to cursor inclusive"
            {
                 row.Cells[i] = new TerminalCell(' ', CurrentForeground, CurrentBackground);
            }
            OnInvalidate?.Invoke();
        }

        public void EraseLineAll()
        {
            if (CursorRow >= _lines.Count) return;
            var row = _lines[CursorRow];
            for (int i = 0; i < Cols; i++)
            {
                 row.Cells[i] = new TerminalCell(' ', CurrentForeground, CurrentBackground);
            }
            OnInvalidate?.Invoke();
        }
    }
}

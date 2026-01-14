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
        public bool IsDefaultForeground { get; set; } = true;
        public bool IsDefaultBackground { get; set; } = true;
        public TerminalTheme Theme { get; set; } = TerminalTheme.Dark;
        public bool IsInverse { get; set; }
        public bool IsBold { get; set; }

        // Mouse reporting modes (for TUI apps like vim, htop)
        public bool MouseModeX10 { get; set; }          // ?1000 - X10 mouse reporting
        public bool MouseModeButtonEvent { get; set; }  // ?1002 - Button event tracking
        public bool MouseModeAnyEvent { get; set; }     // ?1003 - Any event tracking
        public bool MouseModeSGR { get; set; }          // ?1006 - SGR extended mode

        public event Action? OnInvalidate;

        // Thread safety
        public readonly System.Threading.ReaderWriterLockSlim Lock = new System.Threading.ReaderWriterLockSlim();

        public TerminalBuffer(int cols, int rows)
        {
            Cols = cols;
            Rows = rows;
            
            CurrentForeground = Theme.Foreground;
            CurrentBackground = Theme.Background;
            IsDefaultForeground = true;
            IsDefaultBackground = true;

            _viewport = new TerminalRow[rows];
            for (int i = 0; i < rows; i++)
            {
                _viewport[i] = new TerminalRow(cols, Theme.Foreground, Theme.Background);
            }
            CursorRow = 0;
            CursorCol = 0;
        }

        public void Clear()
        {
            Lock.EnterWriteLock();
            try
            {
                _scrollback.Clear();
                for (int i = 0; i < Rows; i++)
                {
                    _viewport[i] = new TerminalRow(Cols, Theme.Foreground, Theme.Background);
                }
                CursorCol = 0;
                CursorRow = 0;
                IsInverse = false;
                IsBold = false;
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            
            // NOTE: Do NOT reset mouse modes here!
            // Mouse modes should only change via DEC private mode sequences,
            // not from screen clearing operations (htop clears screen after enabling mouse)
            
            OnInvalidate?.Invoke();
        }

        public void UpdateThemeColors(TerminalTheme oldTheme)
        {
            Lock.EnterWriteLock();
            try
            {
                var debugPath = @"d:\theme_debug.txt";
                var debugLines = new System.Collections.Generic.List<string>();
                debugLines.Add($"=== UpdateThemeColors called at {DateTime.Now} ===");
                debugLines.Add($"Theme: {Theme.Name}");
                
                // Helper function to check if a color is "dark" (likely a background)
                bool IsDarkColor(Color c)
                {
                    // Calculate perceived brightness
                    double brightness = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                    return brightness < 0.3; // Dark if brightness < 30%
                }
                
                int remappedCount = 0;
                int totalCells = 0;
                int darkCellsFound = 0;
                
                for (int r = 0; r < Rows; r++)
                {
                    for (int c = 0; c < Cols; c++)
                    {
                        ref var cell = ref _viewport[r].Cells[c];
                        totalCells++;
                        
                        // Debug first few rows
                        if (r < 3 && c < 5)
                        {
                            double brightness = (0.299 * cell.Background.R + 0.587 * cell.Background.G + 0.114 * cell.Background.B) / 255.0;
                            debugLines.Add($"Cell[{r},{c}]: BG={cell.Background} (brightness={brightness:F2}), IsDefault={cell.IsDefaultBackground}, Char='{cell.Character}'");
                        }
                        
                        // Always update cells marked as default
                        if (cell.IsDefaultForeground)
                        {
                            cell.Foreground = Theme.Foreground;
                        }
                        if (cell.IsDefaultBackground)
                        {
                            cell.Background = Theme.Background;
                        }
                        
                        // Convert dark backgrounds to theme background
                        if (!cell.IsDefaultBackground && IsDarkColor(cell.Background))
                        {
                            darkCellsFound++;
                            if (darkCellsFound <= 5)
                            {
                                debugLines.Add($"  -> Remapping dark BG at [{r},{c}]: {cell.Background} -> {Theme.Background}");
                            }
                            cell.Background = Theme.Background;
                            cell.IsDefaultBackground = true;
                            remappedCount++;
                        }
                    }
                }
                
                debugLines.Add($"Total cells: {totalCells}, Dark cells found: {darkCellsFound}, Remapped: {remappedCount}");
                System.IO.File.WriteAllLines(debugPath, debugLines);

                // Also update scrollback (no debug for scrollback to keep it simple)
                foreach (var row in _scrollback)
                {
                    for (int c = 0; c < row.Cells.Length; c++)
                    {
                        ref var cell = ref row.Cells[c];
                        
                        if (cell.IsDefaultForeground)
                        {
                            cell.Foreground = Theme.Foreground;
                        }
                        if (cell.IsDefaultBackground)
                        {
                            cell.Background = Theme.Background;
                        }
                        
                        if (!cell.IsDefaultBackground && IsDarkColor(cell.Background))
                        {
                            cell.Background = Theme.Background;
                            cell.IsDefaultBackground = true;
                        }
                    }
                }
            }
            finally
            {
                Lock.ExitWriteLock();
            }
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
            Lock.EnterWriteLock();
            try
            {
                // Clamp cursor to viewport
                CursorRow = Math.Clamp(CursorRow, 0, Rows - 1);
                CursorCol = Math.Clamp(CursorCol, 0, Cols - 1);
                
                // Track row changes
                if (CursorRow != _prevCursorRow)
                {
                    _maxColThisRow = 0;
                }

                if (c == '\r') 
                {
                    CursorCol = 0;
                    _prevCursorCol = CursorCol;
                    _prevCursorRow = CursorRow;
                    // Invalidate handled at end
                }
                else if (c == '\n') 
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
                        CursorRow = Rows - 1; 
                    }
                }
                else if (c == '\b') 
                {
                    if (CursorCol > 0) CursorCol--;
                }
                else if (c == '\t')
                {
                     int spaces = 4 - (CursorCol % 4);
                     for (int i=0; i<spaces;i++)
                     {
                         if (CursorCol >= Cols)
                         {
                             if (CursorRow >= 0 && CursorRow < Rows)
                                 _viewport[CursorRow].IsWrapped = true;

                             CursorCol = 0;
                             CursorRow++;
                             if (CursorRow >= Rows)
                             {
                                 ScrollUp();
                                 CursorRow = Rows - 1;
                             }
                         }
                         
                         if (CursorRow >= 0 && CursorRow < Rows && CursorCol >= 0 && CursorCol < Cols)
                         {
                             _viewport[CursorRow].Cells[CursorCol] = new TerminalCell(' ', CurrentForeground, CurrentBackground, IsInverse, IsBold, IsDefaultForeground, IsDefaultBackground);
                             if (CursorCol > _maxColThisRow) _maxColThisRow = CursorCol;
                             CursorCol++;
                         }
                         else
                         {
                             CursorCol++;
                         }
                     }
                }
                else
                {
                     // Normal Character
                    // Wrap if needed
                    if (CursorCol >= Cols)
                    {
                        if (CursorRow >= 0 && CursorRow < Rows)
                            _viewport[CursorRow].IsWrapped = true;
                        
                        CursorCol = 0;
                        CursorRow++;
                        
                        if (CursorRow >= Rows)
                        {
                            // Scroll up
                            if (Rows > 0)
                            {
                                _scrollback.Add(_viewport[0]);
                                if (_scrollback.Count > MaxScrollbackLines) _scrollback.RemoveAt(0);

                                for (int i = 0; i < Rows - 1; i++)
                                {
                                    _viewport[i] = _viewport[i + 1];
                                }
                                _viewport[Rows - 1] = new TerminalRow(Cols, Theme.Foreground, Theme.Background);
                            }
                            CursorRow = Math.Max(0, Rows - 1);
                        }
                    }

                    // Write to viewport
                    if (CursorRow >= 0 && CursorRow < Rows && CursorCol >= 0 && CursorCol < Cols)
                    {
                        _viewport[CursorRow].Cells[CursorCol] = new TerminalCell(c, CurrentForeground, CurrentBackground, IsInverse, IsBold);
                        
                        if (CursorCol > _maxColThisRow) _maxColThisRow = CursorCol;
                        
                        CursorCol++;
                    }
                    else
                    {
                        CursorCol++; 
                    }
                }

                // Update tracking
                _prevCursorCol = CursorCol;
                _prevCursorRow = CursorRow;
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            
            OnInvalidate?.Invoke();
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
            Lock.EnterWriteLock();
            try
            {
                if (newCols == Cols && newRows == Rows) return;

                var oldViewport = _viewport;
                var oldRows = Rows;
                var oldCols = Cols;

                _viewport = new TerminalRow[newRows];
                for (int i = 0; i < newRows; i++)
                {
                    if (i < oldRows)
                    {
                        // Preserve existing row content
                        var row = oldViewport[i];
                        if (row.Cells.Length != newCols)
                        {
                            var oldCells = row.Cells;
                            row.Cells = new TerminalCell[newCols];
                            for (int j = 0; j < newCols; j++)
                            {
                                if (j < oldCols) row.Cells[j] = oldCells[j];
                                else row.Cells[j] = TerminalCell.Default;
                            }
                        }
                        _viewport[i] = row;
                    }
                    else
                    {
                        // Add new empty rows if expanding
                        _viewport[i] = new TerminalRow(newCols);
                    }
                }
                
                Cols = newCols;
                Rows = newRows;
                
                // Clamp cursor
                CursorRow = Math.Clamp(CursorRow, 0, Rows - 1);
                CursorCol = Math.Clamp(CursorCol, 0, Cols - 1);
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            
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
            
            return GetCellAbsolute(col, actualIndex);
        }

        public TerminalCell GetCellAbsolute(int col, int absRow)
        {
            if (absRow < 0) return TerminalCell.Default;

            if (absRow < _scrollback.Count)
            {
                // Reading from scrollback
                if (col < 0 || col >= Cols) return TerminalCell.Default;
                return _scrollback[absRow].Cells[col];
            }
            else
            {
                // Reading from viewport
                int viewportRow = absRow - _scrollback.Count;
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
            IsBold = IsBold;
        }

        public void EraseLineToEnd()
        {
            Lock.EnterWriteLock();
            try
            {
                if (CursorRow < 0 || CursorRow >= Rows) return;
                var row = _viewport[CursorRow];
                for (int i = CursorCol; i < Cols; i++)
                {
                     row.Cells[i] = new TerminalCell(' ', Theme.Foreground, Theme.Background, false, false, true, true);
                }
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            OnInvalidate?.Invoke();
        }

        public void EraseLineFromStart()
        {
            Lock.EnterWriteLock();
            try
            {
                if (CursorRow < 0 || CursorRow >= Rows) return;
                var row = _viewport[CursorRow];
                for (int i = 0; i <= CursorCol; i++)
                {
                     row.Cells[i] = new TerminalCell(' ', Theme.Foreground, Theme.Background, false, false, true, true);
                }
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            OnInvalidate?.Invoke();
        }

        public void EraseLineAll()
        {
            Lock.EnterWriteLock();
            try
            {
                if (CursorRow < 0 || CursorRow >= Rows) return;
                var row = _viewport[CursorRow];
                for (int i = 0; i < Cols; i++)
                {
                     row.Cells[i] = new TerminalCell(' ', Theme.Foreground, Theme.Background, false, false, true, true);
                }
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            OnInvalidate?.Invoke();
        }

        public void EraseCharacters(int count)
        {
            Lock.EnterWriteLock();
            try
            {
                if (CursorRow < 0 || CursorRow >= Rows) return;
                var row = _viewport[CursorRow];
                
                for (int i = 0; i < count; i++)
                {
                    int col = CursorCol + i;
                    if (col >= Cols) break;
                    
                    row.Cells[col] = new TerminalCell(' ', CurrentForeground, CurrentBackground);
                }
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            OnInvalidate?.Invoke();
        }

        public List<SearchMatch> FindMatches(string query)
        {
            if (string.IsNullOrEmpty(query)) return new List<SearchMatch>();

            var matches = new List<SearchMatch>();
            Lock.EnterReadLock();
            try
            {
                int totalRows = _scrollback.Count + Rows;
                for (int r = 0; r < totalRows; r++)
                {
                    // Build row string
                    var row = (r < _scrollback.Count) ? _scrollback[r] : _viewport[r - _scrollback.Count];
                    
                    // Optimization: Use a shared StringBuilder if this becomes a bottleneck
                    char[] chars = new char[Cols];
                    for (int c = 0; c < Cols; c++) chars[c] = row.Cells[c].Character;
                    string lineText = new string(chars);

                    int index = lineText.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    while (index != -1)
                    {
                        matches.Add(new SearchMatch(r, index, index + query.Length - 1));
                        index = lineText.IndexOf(query, index + 1, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            finally
            {
                Lock.ExitReadLock();
            }
            return matches;
        }
    }
}

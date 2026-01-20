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
        public int MaxHistory { get; set; } = 10000;

        public int Cols { get; private set; }
        public int Rows { get; private set; }

        public int CursorCol
        {
            get
            {
                Lock.EnterReadLock();
                try { return _cursorCol; }
                finally { Lock.ExitReadLock(); }
            }
            set
            {
                Lock.EnterWriteLock();
                try { _cursorCol = value; }
                finally { Lock.ExitWriteLock(); }
            }
        }

        public int CursorRow
        {
            get
            {
                Lock.EnterReadLock();
                try { return _cursorRow; }
                finally { Lock.ExitReadLock(); }
            }
            set
            {
                Lock.EnterWriteLock();
                try { _cursorRow = value; }
                finally { Lock.ExitWriteLock(); }
            }
        } // Row within viewport (0 to Rows-1)

        // Internal backing fields for thread-safe access within lock-held methods
        private int _cursorCol;
        private int _cursorRow;

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

        // Input modes
        public bool IsApplicationCursorKeys { get; set; } // ?1 - DECCKM (Application Cursor Keys)
        public bool IsAutoWrapMode { get; set; } = true;  // ?7 - DECAWM (Auto Wrap Mode)

        public event Action? OnInvalidate;

        // Thread safety
        public readonly System.Threading.ReaderWriterLockSlim Lock = new System.Threading.ReaderWriterLockSlim(System.Threading.LockRecursionPolicy.SupportsRecursion);

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
            _cursorRow = 0;
            _cursorCol = 0;
        }

        public void Clear(bool resetCursor = true)
        {
            Lock.EnterWriteLock();
            try
            {
                _scrollback.Clear();
                for (int i = 0; i < Rows; i++)
                {
                    _viewport[i] = new TerminalRow(Cols, Theme.Foreground, Theme.Background);
                }

                if (resetCursor)
                {
                    _cursorCol = 0;
                    _cursorRow = 0;
                }
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
                // Allow CursorCol == Cols (Wrap Pending state)
                _cursorRow = Math.Clamp(_cursorRow, 0, Rows - 1);
                _cursorCol = Math.Clamp(_cursorCol, 0, Cols);

                // Track row changes
                if (_cursorRow != _prevCursorRow)
                {
                    _maxColThisRow = 0;
                }

                if (c == '\r')
                {
                    _cursorCol = 0;
                    _prevCursorCol = _cursorCol;
                    _prevCursorRow = _cursorRow;
                    // Invalidate handled at end
                }
                else if (c == '\n')
                {
                    // Mark current line as not wrapped
                    if (_cursorRow >= 0 && _cursorRow < Rows)
                    {
                        _viewport[_cursorRow].IsWrapped = false;
                    }

                    _cursorCol = 0;
                    _cursorRow++;

                    // If we're past the bottom, scroll
                    if (_cursorRow >= Rows)
                    {
                        ScrollUp();
                        _cursorRow = Rows - 1;
                    }
                }
                else if (c == '\b')
                {
                    if (_cursorCol > 0) _cursorCol--;
                }
                else if (c == '\t')
                {
                    int spaces = 4 - (_cursorCol % 4);
                    for (int i = 0; i < spaces; i++)
                    {
                        if (_cursorCol >= Cols)
                        {
                            if (_cursorRow >= 0 && _cursorRow < Rows)
                                _viewport[_cursorRow].IsWrapped = true;

                            _cursorCol = 0;
                            _cursorRow++;
                            if (_cursorRow >= Rows)
                            {
                                ScrollUp();
                                _cursorRow = Rows - 1;
                            }
                        }

                        if (_cursorRow >= 0 && _cursorRow < Rows && _cursorCol >= 0 && _cursorCol < Cols)
                        {
                            _viewport[_cursorRow].Cells[_cursorCol] = new TerminalCell(' ', CurrentForeground, CurrentBackground, IsInverse, IsBold, IsDefaultForeground, IsDefaultBackground);
                            if (_cursorCol > _maxColThisRow) _maxColThisRow = _cursorCol;
                            _cursorCol++;
                        }
                        else
                        {
                            _cursorCol++;
                        }
                    }
                }
                else
                {
                    // Normal Character

                    // Handle DECAWM (Auto Wrap Mode)
                    // If OFF, we clamp to the last column and overwrite it.
                    if (!IsAutoWrapMode && _cursorCol >= Cols)
                    {
                        _cursorCol = Cols - 1;
                    }

                    // Wrap if needed (only if AutoWrap is ON)
                    if (IsAutoWrapMode && _cursorCol >= Cols)
                    {
                        if (_cursorRow >= 0 && _cursorRow < Rows)
                            _viewport[_cursorRow].IsWrapped = true;

                        _cursorCol = 0;
                        _cursorRow++;

                        if (_cursorRow >= Rows)
                        {
                            // Scroll up
                            ScrollUp();
                            _cursorRow = Math.Max(0, Rows - 1);
                        }
                    }

                    // Write to viewport
                    if (_cursorRow >= 0 && _cursorRow < Rows && _cursorCol >= 0 && _cursorCol < Cols)
                    {
                        _viewport[_cursorRow].Cells[_cursorCol] = new TerminalCell(c, CurrentForeground, CurrentBackground, IsInverse, IsBold, IsDefaultForeground, IsDefaultBackground);

                        if (_cursorCol > _maxColThisRow) _maxColThisRow = _cursorCol;

                        _cursorCol++;
                    }
                    else
                    {
                        _cursorCol++;
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
            if (_scrollback.Count > MaxHistory)
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

                int oldCols = Cols;
                int oldRows = Rows;

                // Update Dimensions BEFORE Reflow might be needed for some helpers, 
                // but Reflow MUST know the original size.
                // Update Dimensions BEFORE Reflow, but we need old and new for Reflow
                Cols = newCols;
                Rows = newRows;

                // Full Reflow
                Reflow(oldCols, oldRows, newCols, newRows);

                // Ensure cursor is within bounds (allow Cols for wrap-pending state)
                _cursorRow = Math.Clamp(_cursorRow, 0, Rows - 1);
                _cursorCol = Math.Clamp(_cursorCol, 0, Cols);
            }
            finally
            {
                Lock.ExitWriteLock();
            }

            OnInvalidate?.Invoke();
        }

        private void Reflow(int oldCols, int oldRows, int newCols, int newRows)
        {
            if (newCols <= 0 || newRows <= 0) return;

            // 1. Capture Cursor Content Pre-Resize
            int absCursorPhysicalIdx = _scrollback.Count + _cursorRow;
            int cursorLogicalIdx = -1;
            int cursorInLogicalOffset = -1;

            // 2. Physical Extraction with Padding Trim
            var allPhysicalRows = new List<TerminalRow>();
            for (int i = 0; i < _scrollback.Count; i++) allPhysicalRows.Add(_scrollback[i]);

            // Find the last row in the viewport that has ANY content (including non-default background or non-space char)
            int lastActiveVpRow = -1;
            for (int i = 0; i < oldRows; i++)
            {
                var row = _viewport[i];
                bool isEmpty = true;
                foreach (var cell in row.Cells)
                {
                    if (cell.Character != ' ' && cell.Character != '\0' || !cell.IsDefaultBackground)
                    {
                        isEmpty = false;
                        break;
                    }
                }
                if (!isEmpty || i <= _cursorRow) lastActiveVpRow = i;
            }

            int vpRowsToTake = lastActiveVpRow + 1;
            for (int i = 0; i < vpRowsToTake; i++) allPhysicalRows.Add(_viewport[i]);

            // 3. Metadata-Aware Logical Reconstruction
            var logicalLines = new List<(List<TerminalCell> Cells, bool IsWrapped, int StartPhysIdx)>();
            List<TerminalCell>? currentLogical = null;
            int currentStartPhys = -1;

            for (int i = 0; i < allPhysicalRows.Count; i++)
            {
                var physRow = allPhysicalRows[i];
                if (currentLogical == null)
                {
                    currentLogical = new List<TerminalCell>();
                    currentStartPhys = i;
                }

                // Cursor Tracking
                if (i == absCursorPhysicalIdx)
                {
                    cursorLogicalIdx = logicalLines.Count;
                    cursorInLogicalOffset = currentLogical.Count + _cursorCol;
                }

                int validLen = physRow.Cells.Length;
                if (!physRow.IsWrapped)
                {
                    // Calculate minimum length to preserve if this row contains the cursor
                    // If cursor is on this row, we must not trim characters before the cursor
                    int minResultLen = 0;
                    if (i == absCursorPhysicalIdx)
                    {
                        // cursorCol is the index. So validLen must be at least cursorCol.
                        // Example: "A " (Space at 1). Cursor at 2. validLen must be at least 2? No, cursorCol=2 means 2 chars?
                        // If we have chars 0,1. Cursor at 2 means we are PAST the last char.
                        // So we need to preserve up to index 1. Length 2.
                        // So minResultLen = _cursorCol.
                        minResultLen = _cursorCol;
                    }

                    // Trim trailing spaces (only if they use default background)
                    while (validLen > minResultLen)
                    {
                        var cell = physRow.Cells[validLen - 1];
                        if (cell.IsDefaultBackground && (cell.Character == ' ' || cell.Character == '\0')) validLen--;
                        else break;
                    }
                }

                for (int k = 0; k < validLen; k++) currentLogical.Add(physRow.Cells[k]);

                if (!physRow.IsWrapped)
                {
                    logicalLines.Add((currentLogical, false, currentStartPhys));
                    currentLogical = null;
                }
            }

            if (currentLogical != null)
            {
                logicalLines.Add((currentLogical, true, currentStartPhys));
            }


            // 5. Distribution logic
            _scrollback.Clear();
            _viewport = new TerminalRow[newRows];
            var allFlowedRows = new List<TerminalRow>();

            int newCursorPhysRow = -1;
            int newCursorPhysCol = -1;
            int historyRowCount = 0; // Tracks physical rows generated from original history

            // Identify the logical line index that starts the viewport
            // The first viewport row in 'allPhysicalRows' was at index 'oldScrollbackCount'
            // We need to find the first logical line that includes 'oldScrollbackCount' or higher.
            int firstViewportLogicalIdx = logicalLines.Count; // Default to end
            int oldScrollbackCount = absCursorPhysicalIdx - _cursorRow; // Re-derive or pass in? 
            // Better to capture oldScrollbackCount at the start of Reflow.
            // But we can infer it: absCursorPhysicalIdx is _scrollback.Count + _cursorRow.
            // So _scrollback.Count = absCursorPhysicalIdx - _cursorRow.
            // Wait, absCursorPhysicalIdx is calculated using CURRENT _cursorRow and _scrollback.Count.
            // So yes, that works.
            int splitPhysIndex = absCursorPhysicalIdx - _cursorRow;

            // Find first logical line that starts at or after splitPhysIndex
            for (int i = 0; i < logicalLines.Count; i++)
            {
                if (logicalLines[i].StartPhysIdx >= splitPhysIndex)
                {
                    firstViewportLogicalIdx = i;
                    break;
                }
            }

            for (int i = 0; i < logicalLines.Count; i++)
            {
                var lineCells = logicalLines[i].Cells;

                // Track start of this logical line in flowed rows
                int startFlowIndex = allFlowedRows.Count;

                if (lineCells.Count == 0)
                {
                    // If this is the WIPED prompt, place cursor here
                    if (i == cursorLogicalIdx) { newCursorPhysRow = allFlowedRows.Count; newCursorPhysCol = 0; }
                    allFlowedRows.Add(new TerminalRow(newCols, Theme.Foreground, Theme.Background));
                }
                else
                {
                    int processed = 0;
                    while (processed < lineCells.Count)
                    {
                        int remaining = lineCells.Count - processed;
                        int take = Math.Min(remaining, newCols);

                        // Mapping
                        if (i == cursorLogicalIdx)
                        {
                            if (cursorInLogicalOffset >= processed && cursorInLogicalOffset < processed + newCols)
                            {
                                newCursorPhysRow = allFlowedRows.Count;
                                newCursorPhysCol = cursorInLogicalOffset - processed;
                            }
                            else if (cursorInLogicalOffset == processed + newCols && remaining == newCols)
                            {
                                newCursorPhysRow = allFlowedRows.Count;
                                newCursorPhysCol = newCols;
                            }
                        }

                        var row = new TerminalRow(newCols, Theme.Foreground, Theme.Background);
                        for (int c = 0; c < take; c++) row.Cells[c] = lineCells[processed + c];

                        // Style-Aware Padding
                        if (take < newCols)
                        {
                            var last = row.Cells[take - 1];
                            var def = new TerminalCell(' ', last.Foreground, last.Background, last.IsBold, last.IsInverse, last.IsDefaultForeground, last.IsDefaultBackground);
                            for (int c = take; c < newCols; c++) row.Cells[c] = def;
                        }

                        if (remaining > newCols) row.IsWrapped = true;
                        allFlowedRows.Add(row);
                        processed += take;
                    }
                }

                // If this line belongs to history (before viewport start), add its generated rows to count
                if (i < firstViewportLogicalIdx)
                {
                    historyRowCount += (allFlowedRows.Count - startFlowIndex);
                }
            }

            // 6. Final Layout (Anchor-to-Top of Viewport)
            // We want _scrollback to contain AT LEAST 'historyRowCount'.
            // But if the remaining lines (viewport content) > newRows, we must push some of them to SB (Shrink).
            int total = allFlowedRows.Count;

            // Base split: Everything that was history stays history.
            int sbCount = historyRowCount;

            // Shrink Adjustment: If active content doesn't fit in new viewport, overflow goes to SB.
            int activeContentSize = total - historyRowCount;
            if (activeContentSize > newRows)
            {
                sbCount += (activeContentSize - newRows);
            }
            // Grow Adjustment: If active content fits, we keep sbCount as is. Viewport will have padding at bottom.

            // Ensure safety
            sbCount = Math.Clamp(sbCount, 0, total);
            int vpCount = total - sbCount;

            for (int i = 0; i < sbCount; i++) _scrollback.Add(allFlowedRows[i]);

            if (_scrollback.Count > MaxHistory)
            {
                int diff = _scrollback.Count - MaxHistory;
                _scrollback.RemoveRange(0, diff);
                sbCount -= diff;
            }

            // Fill viewport
            // If vpCount < newRows (Growth), we will have empty space at the bottom (Top Anchoring).
            int vIdx = 0;
            for (int i = 0; i < vpCount; i++) _viewport[vIdx++] = allFlowedRows[sbCount + i]; // Offset by updated sbCount

            // Pad remaining viewport rows (at the BOTTOM now)
            while (vIdx < newRows)
            {
                _viewport[vIdx++] = new TerminalRow(newCols, Theme.Foreground, Theme.Background);
            }

            // 7. Restore Cursor
            if (newCursorPhysRow != -1)
            {
                // newCursorPhysRow is absolute index in allFlowedRows
                // We need to map it to viewport relative.
                // It might be in scrollback now!
                if (newCursorPhysRow < sbCount)
                {
                    // Cursor pushed to scrollback?
                    // We must clamp it to 0? Or keep it?
                    // TerminalBuffer usually keeps cursor in Viewport.
                    // But if we shrank so much the cursor is gone... 
                    // We forcibly scroll? Or just clamp to top?
                    _cursorRow = 0;
                    // _scrollOffset adjustment would be needed here to keep it in view, but simplest is clamp.
                }
                else
                {
                    _cursorRow = newCursorPhysRow - sbCount;
                }
                _cursorCol = Math.Clamp(newCursorPhysCol, 0, newCols);
            }
            else
            {
                _cursorRow = newRows - 1;
                _cursorCol = 0;
            }

            // 8. Surgical Wipe of Cursor Row
            // We clear the row under the cursor to prevent "Ghost" prompts.
            // The PTY/Shell is expected to redraw the prompt/input line on resize.
            // If we don't wipe, we might end up with "Old Prompt" (Reflowed) + "New Prompt" (Redrawn)
            // resulting in duplication or visual corruption.
            if (_cursorRow >= 0 && _cursorRow < newRows)
            {
                _viewport[_cursorRow] = new TerminalRow(newCols, Theme.Foreground, Theme.Background);
            }
        }

        // Helper to resize a single row (Visual resize only - content clipping/padding)
        private TerminalRow _ResizeRow(TerminalRow oldRow, int newWidth)
        {
            if (oldRow.Cells.Length == newWidth) return oldRow;

            int oldWidth = oldRow.Cells.Length;

            // Create new cell array
            var newCells = new TerminalCell[newWidth];

            try
            {
                // Logging removed
            }
            catch { }

            // 1. Copy existing cells that fit
            int copyLen = Math.Min(oldWidth, newWidth);
            Array.Copy(oldRow.Cells, newCells, copyLen);

            // 2. Fill remaining space (if growing)
            if (newWidth > oldWidth)
            {
                // Use edge extension for background color
                var templateCell = newCells[oldWidth - 1]; // Use last valid cell as template
                                                           // Reset char to space
                var fillCell = new TerminalCell(' ', templateCell.Foreground, templateCell.Background,
                                                templateCell.IsInverse, templateCell.IsBold,
                                                templateCell.IsDefaultForeground, templateCell.IsDefaultBackground);

                for (int i = oldWidth; i < newWidth; i++)
                {
                    newCells[i] = fillCell;
                }
            }

            // Return new row wrapper
            var newRow = new TerminalRow(newWidth);
            newRow.Cells = newCells;
            newRow.IsWrapped = oldRow.IsWrapped; // Preserve wrap flag?

            return newRow;
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
                if (_cursorRow < 0 || _cursorRow >= Rows) return;
                var row = _viewport[_cursorRow];
                for (int i = _cursorCol; i < Cols; i++)
                {
                    row.Cells[i] = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground);
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
                if (_cursorRow < 0 || _cursorRow >= Rows) return;
                var row = _viewport[_cursorRow];
                for (int i = 0; i <= _cursorCol; i++)
                {
                    row.Cells[i] = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground);
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
                if (_cursorRow < 0 || _cursorRow >= Rows) return;
                var row = _viewport[_cursorRow];
                for (int i = 0; i < Cols; i++)
                {
                    row.Cells[i] = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground);
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
                if (_cursorRow < 0 || _cursorRow >= Rows) return;
                var row = _viewport[_cursorRow];

                for (int i = 0; i < count; i++)
                {
                    int col = _cursorCol + i;
                    if (col >= Cols) break;

                    row.Cells[col] = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground);
                }
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            OnInvalidate?.Invoke();
        }

        public void InsertLines(int count)
        {
            try { System.IO.File.AppendAllText("resize_debug.log", $"[InsertLines] Count:{count} CursorRow:{_cursorRow}\n"); } catch { }
            Lock.EnterWriteLock();
            try
            {
                if (_cursorRow < 0 || _cursorRow >= Rows) return;

                int top = _cursorRow;
                int bottom = Rows - 1;
                // Clip count to available space
                int n = Math.Min(count, bottom - top + 1);

                if (n <= 0) return;

                // Shift lines DOWN
                // To insert N lines at TOP, we must move lines starting at TOP down by N.
                // We iterate backwards from (bottom - n) to top to avoid overwriting.
                for (int i = bottom - n; i >= top; i--)
                {
                    _viewport[i + n] = _viewport[i];
                }

                // Fill the gap created at TOP with new blank lines
                for (int i = 0; i < n; i++)
                {
                    _viewport[top + i] = new TerminalRow(Cols, CurrentForeground, CurrentBackground);
                }
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            OnInvalidate?.Invoke();
        }

        public void DeleteLines(int count)
        {
            try { System.IO.File.AppendAllText("resize_debug.log", $"[DeleteLines] Count:{count} CursorRow:{_cursorRow}\n"); } catch { }
            Lock.EnterWriteLock();
            try
            {
                if (_cursorRow < 0 || _cursorRow >= Rows) return;

                int top = _cursorRow;
                int bottom = Rows - 1;
                // Clip count to available space
                int n = Math.Min(count, bottom - top + 1);

                if (n <= 0) return;

                // Shift lines UP
                // To delete N lines at TOP, we shift content from (top + n) UP to top.
                // We iterate forwards.
                for (int i = top; i <= bottom - n; i++)
                {
                    _viewport[i] = _viewport[i + n];
                }

                // Fill the gap created at BOTTOM with new blank lines
                for (int i = 0; i < n; i++)
                {
                    _viewport[bottom - n + 1 + i] = new TerminalRow(Cols, CurrentForeground, CurrentBackground);
                }
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            OnInvalidate?.Invoke();
        }

        public void SetCursorPosition(int col, int row)
        {
            Lock.EnterWriteLock();
            try
            {
                _cursorCol = Math.Clamp(col, 0, Cols - 1);
                _cursorRow = Math.Clamp(row, 0, Rows - 1);
            }
            finally
            {
                Lock.ExitWriteLock();
            }
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

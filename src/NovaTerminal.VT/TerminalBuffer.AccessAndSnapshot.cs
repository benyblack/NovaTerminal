using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NovaTerminal.Core.Replay;
using NovaTerminal.Core.Storage;

namespace NovaTerminal.Core
{
    public partial class TerminalBuffer
    {
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

        public TerminalRow? GetRowAbsolute(int absRow)
        {
            AssertLockHeld();
            if (absRow < 0) return null;

            if (_isAltScreen)
            {
                if (absRow >= Rows) return null;
                return _viewport[absRow];
            }

            // Scrollback rows are now paged, they don't exist as persistent TerminalRow objects.
            if (absRow < _scrollback.Count) return null;
            
            int viewportRow = absRow - _scrollback.Count;
            if (viewportRow < 0 || viewportRow >= Rows) return null;
            return _viewport[viewportRow];
        }

        public TerminalCell GetCellAbsolute(int col, int absRow)
        {
            AssertLockHeld();
            if (absRow < 0 || col < 0 || col >= Cols) return TerminalCell.Default;

            if (_isAltScreen)
            {
                if (absRow >= Rows) return TerminalCell.Default;
                return _viewport[absRow].Cells[col];
            }

            if (absRow < _scrollback.Count)
            {
                // Reading from paged scrollback
                return _scrollback.GetRow(absRow)[col];
            }
            else
            {
                // Reading from viewport
                int viewportRow = absRow - _scrollback.Count;
                if (viewportRow < 0 || viewportRow >= Rows) return TerminalCell.Default;
                return _viewport[viewportRow].Cells[col];
            }
        }

        public string GetGraphemeAbsolute(int col, int absRow)
        {
            AssertLockHeld();
            if (absRow < 0 || col < 0 || col >= Cols) return " ";
            
            if (!_isAltScreen && absRow < _scrollback.Count)
            {
                // Scrollback doesn't support extended text yet (Step 5)
                return _scrollback.GetRow(absRow)[col].Character.ToString();
            }

            var row = GetRowAbsolute(absRow);
            if (row == null) return " ";
            var cell = row.Cells[col];
            return (cell.HasExtendedText ? row.GetExtendedText(col) : null) ?? cell.Character.ToString();
        }

        public string? GetHyperlinkAbsolute(int col, int absRow)
        {
            bool lockTaken = EnterReadLockIfNeeded();
            try
            {
                if (absRow < 0 || col < 0 || col >= Cols) return null;
                
                if (!_isAltScreen && absRow < _scrollback.Count)
                {
                    // Scrollback doesn't support hyperlinks yet (Step 5)
                    return null;
                }

                var row = GetRowAbsolute(absRow);
                return row?.GetHyperlink(col);
            }
            finally { ExitReadLockIfNeeded(Lock, lockTaken); }
        }

        public string GetGrapheme(int col, int viewRow)
        {
            AssertLockHeld();
            if (viewRow < 0 || viewRow >= Rows) return " ";
            var row = _viewport[viewRow];
            if (col < 0 || col >= Cols) return " ";
            var cell = row.Cells[col];
            return (cell.HasExtendedText ? row.GetExtendedText(col) : null) ?? cell.Character.ToString();
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

        private void CaptureCursorStateNoLock(CursorState target)
        {
            target.Row = _cursorRow;
            target.Col = _cursorCol;
            target.Foreground = CurrentForeground;
            target.Background = CurrentBackground;
            target.FgIndex = CurrentFgIndex;
            target.BgIndex = CurrentBgIndex;
            target.IsDefaultForeground = IsDefaultForeground;
            target.IsDefaultBackground = IsDefaultBackground;
            target.IsInverse = IsInverse;
            target.IsBold = IsBold;
            target.IsFaint = IsFaint;
            target.IsItalic = IsItalic;
            target.IsUnderline = IsUnderline;
            target.IsBlink = IsBlink;
            target.IsStrikethrough = IsStrikethrough;
            target.IsHidden = IsHidden;
            target.IsPendingWrap = _isPendingWrap;
        }

        private void ApplyCursorStateNoLock(CursorState source)
        {
            _cursorRow = Math.Clamp(source.Row, 0, Rows - 1);
            _cursorCol = Math.Clamp(source.Col, 0, Cols - 1);
            CurrentForeground = source.Foreground;
            CurrentBackground = source.Background;
            CurrentFgIndex = source.FgIndex;
            CurrentBgIndex = source.BgIndex;
            IsDefaultForeground = source.IsDefaultForeground;
            IsDefaultBackground = source.IsDefaultBackground;
            IsInverse = source.IsInverse;
            IsBold = source.IsBold;
            IsFaint = source.IsFaint;
            IsItalic = source.IsItalic;
            IsUnderline = source.IsUnderline;
            IsBlink = source.IsBlink;
            IsStrikethrough = source.IsStrikethrough;
            IsHidden = source.IsHidden;
            _isPendingWrap = source.IsPendingWrap;
        }

        private void SaveActiveScreenStateNoLock()
        {
            var target = _isAltScreen ? _screenCursorStates.Alt : _screenCursorStates.Main;
            CaptureCursorStateNoLock(target);
        }

        private void RestoreScreenStateNoLock(bool altScreen)
        {
            var source = altScreen ? _screenCursorStates.Alt : _screenCursorStates.Main;
            ApplyCursorStateNoLock(source);
        }

        private void ClearAltScreenNoLock()
        {
            for (int i = 0; i < Rows; i++)
            {
                var row = _viewport[i];
                row.IsWrapped = false;
                for (int c = 0; c < Cols; c++)
                {
                    row.Cells[c] = TerminalCell.Default;
                }

                row.ClearExtendedText();
                row.ClearHyperlinks();
                row.TouchRevision();
            }
        }

        private void ResetCursorStateToDefaultsNoLock()
        {
            _cursorRow = 0;
            _cursorCol = 0;
            _isPendingWrap = false;
            CurrentForeground = Theme.Foreground;
            CurrentBackground = Theme.Background;
            CurrentFgIndex = -1;
            CurrentBgIndex = -1;
            IsDefaultForeground = true;
            IsDefaultBackground = true;
            IsInverse = false;
            IsBold = false;
            IsFaint = false;
            IsItalic = false;
            IsUnderline = false;
            IsBlink = false;
            IsStrikethrough = false;
            IsHidden = false;
        }

        private void SyncThemeDefaultsInCursorStateNoLock(CursorState state)
        {
            if (state.IsDefaultForeground)
            {
                state.Foreground = Theme.Foreground;
            }

            if (state.IsDefaultBackground)
            {
                state.Background = Theme.Background;
            }
        }

        private TerminalRow[] ResizeDetachedScreenBufferNoLock(TerminalRow[] source)
        {
            var resized = new TerminalRow[Rows];
            for (int i = 0; i < Rows; i++)
            {
                resized[i] = new TerminalRow(Cols, Theme.Foreground, Theme.Background);

                if (i >= source.Length)
                {
                    continue;
                }

                int copyCols = Math.Min(Cols, source[i].Cells.Length);
                for (int c = 0; c < copyCols; c++)
                {
                    resized[i].Cells[c] = source[i].Cells[c];
                }

                resized[i].IsWrapped = source[i].IsWrapped;
            }

            return resized;
        }
        // Saved Cursor State (DEC SC / DEC RC)
        public void SaveCursor()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                var target = _isAltScreen ? _savedCursors.Alt : _savedCursors.Main;
                CaptureCursorStateNoLock(target);
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
        }

        public void RestoreCursor()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                var source = _isAltScreen ? _savedCursors.Alt : _savedCursors.Main;
                ApplyCursorStateNoLock(source);
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
        }

        public void EraseLineToEnd()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (_cursorRow < 0 || _cursorRow >= Rows) return;
                var row = _viewport[_cursorRow];
                for (int i = _cursorCol; i < Cols; i++)
                {
                    row.Cells[i] = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground);
                    row.SetExtendedText(i, null);
                    row.SetHyperlink(i, null);
                }
                row.TouchRevision();
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
            OnInvalidate?.Invoke();
        }

        public void EraseLineFromStart()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (_cursorRow < 0 || _cursorRow >= Rows) return;
                var row = _viewport[_cursorRow];
                for (int i = 0; i <= _cursorCol; i++)
                {
                    if (i >= Cols) break;
                    row.Cells[i] = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground);
                    row.SetExtendedText(i, null);
                    row.SetHyperlink(i, null);
                }
                row.TouchRevision();
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
            OnInvalidate?.Invoke();
        }

        public void EraseLineAll()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (_cursorRow < 0 || _cursorRow >= Rows) return;
                ClearRowInternal(_cursorRow);
            }
            finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
            OnInvalidate?.Invoke();
        }

        public void EraseLineAll(int rowIndex)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (rowIndex < 0 || rowIndex >= Rows) return;
                ClearRowInternal(rowIndex);
            }
            finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
            OnInvalidate?.Invoke();
        }

        private void ClearRowInternal(int rowIndex)
        {
            var row = _viewport[rowIndex];
            var empty = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground);
            for (int i = 0; i < Cols; i++)
            {
                row.Cells[i] = empty;
            }
            row.ClearExtendedText();
            row.ClearHyperlinks();
            row.TouchRevision();
        }

        public void EraseCharacters(int count)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (_cursorRow < 0 || _cursorRow >= Rows) return;
                var row = _viewport[_cursorRow];

                for (int i = 0; i < count; i++)
                {
                    int col = _cursorCol + i;
                    if (col >= Cols) break;

                    row.Cells[col] = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground);
                    row.SetExtendedText(col, null);
                    row.SetHyperlink(col, null);
                }
                row.TouchRevision();
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
            OnInvalidate?.Invoke();
        }

        public void InsertLines(int count)
        {
            TerminalLogger.Log($"[InsertLines] Count:{count} CursorRow:{_cursorRow}");
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (_cursorRow < ScrollTop || _cursorRow > ScrollBottom) return;

                int top = _cursorRow;
                int bottom = ScrollBottom;
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

                // Update images: Shift images starting at top DOWN by n
                int absTop = _isAltScreen ? top : (_scrollback.Count + top);
                int absBottom = _isAltScreen ? bottom : (_scrollback.Count + bottom);
                for (int i = _images.Count - 1; i >= 0; i--)
                {
                    var img = _images[i];
                    // If image starts in or below the insertion point, shift it down
                    if (img.IsSticky && img.CellY >= absTop && img.CellY <= absBottom)
                    {
                        img.CellY += n;
                        if (img.CellY > absBottom)
                        {
                            _images.RemoveAt(i);
                        }
                    }
                }

                // Fill the gap created at TOP with new blank lines
                for (int i = 0; i < n; i++)
                {
                    _viewport[top + i] = new TerminalRow(Cols, CurrentForeground, CurrentBackground);
                }

                for (int i = top; i <= bottom; i++) _viewport[i].TouchRevision();
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
            OnInvalidate?.Invoke();
        }

        public void DeleteLines(int count)
        {
            TerminalLogger.Log($"[DeleteLines] Count:{count} CursorRow:{_cursorRow}");
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (_cursorRow < ScrollTop || _cursorRow > ScrollBottom) return;

                int top = _cursorRow;
                int bottom = ScrollBottom;
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

                // Update images: Shift images below the deleted range UP by n
                int absTop = _isAltScreen ? top : (_scrollback.Count + top);
                int absBottom = _isAltScreen ? bottom : (_scrollback.Count + bottom);
                int absDeletedEnd = absTop + n;

                for (int i = _images.Count - 1; i >= 0; i--)
                {
                    var img = _images[i];
                    // If image overlaps with or is below the deleted range
                    if (img.IsSticky && img.CellY + img.CellHeight > absTop && img.CellY <= absBottom)
                    {
                        if (img.CellY < absDeletedEnd)
                        {
                            // Image starts in the deleted range
                            _images.RemoveAt(i);
                        }
                        else
                        {
                            // Image is below the deleted range, shift it UP
                            img.CellY -= n;
                        }
                    }
                }

                // Fill the gap created at BOTTOM with new blank lines
                for (int i = 0; i < n; i++)
                {
                    _viewport[bottom - n + 1 + i] = new TerminalRow(Cols, CurrentForeground, CurrentBackground);
                }

                for (int i = top; i <= bottom; i++) _viewport[i].TouchRevision();
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
            OnInvalidate?.Invoke();
        }

        public void SetCursorPosition(int col, int row)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                _cursorCol = Math.Clamp(col, 0, Cols - 1);
                _cursorRow = Math.Clamp(row, 0, Rows - 1);
                _isPendingWrap = false;
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
        }

        /// <summary>
        /// Switch to alternate screen buffer (used by vim, htop, less, etc.)
        /// </summary>
        public void EnterAltScreen(bool clearAlt, bool saveCursorForExit)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (_isAltScreen) return;

                SaveActiveScreenStateNoLock();

                if (saveCursorForExit)
                {
                    CaptureCursorStateNoLock(_savedCursors.Main);
                    _restoreMainCursorOnAltExit = true;
                }

                _mainScreen = _viewport;  // Save current viewport as main screen

                // Reset scrolling region to full screen when switching screens
                ScrollTop = 0;
                ScrollBottom = Rows - 1;

                // CRITICAL FIX: Ensure Alt Screen buffer matches current dimensions
                // If we resized while in Main Screen, _altScreen might be stale (wrong size).
                if (_altScreen == null || _altScreen.Length != Rows || (_altScreen.Length > 0 && _altScreen[0].Cells.Length != Cols))
                {
                    _altScreen = new TerminalRow[Rows];
                    for (int i = 0; i < Rows; i++)
                    {
                        _altScreen[i] = new TerminalRow(Cols, Theme.Foreground, Theme.Background);
                    }
                }

                _isAltScreen = true;
                _viewport = _altScreen;    // Switch to alt screen

                if (clearAlt)
                {
                    ClearAltScreenNoLock();
                    ResetCursorStateToDefaultsNoLock();
                    CaptureCursorStateNoLock(_screenCursorStates.Alt);
                }
                else
                {
                    RestoreScreenStateNoLock(altScreen: true);
                }

                // Reset row-diff cache so the next CaptureRenderSnapshot compares the alt-screen
                // rows fresh rather than against stale main-screen row IDs.
                _hasSnapshotState = false;
                Invalidate();
                OnScreenSwitched?.Invoke(true); // Notify that we switched to alt screen
            }
            finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        }

        public void SwitchToAltScreen()
        {
            EnterAltScreen(clearAlt: true, saveCursorForExit: false);
        }

        /// <summary>
        /// Switch back to main screen buffer
        /// </summary>
        public void SwitchToMainScreen(bool restoreSavedCursorIfArmed = true)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (!_isAltScreen) return;

                SaveActiveScreenStateNoLock();
                _altScreen = _viewport;    // Save current viewport as alt screen
                _isAltScreen = false;

                if (_mainScreen.Length != Rows || (_mainScreen.Length > 0 && _mainScreen[0].Cells.Length != Cols))
                {
                    _mainScreen = ResizeDetachedScreenBufferNoLock(_mainScreen);
                }

                _viewport = _mainScreen;   // Restore main screen

                // Reset scrolling region to full screen when switching back to main screen
                ScrollTop = 0;
                ScrollBottom = Rows - 1;

                RestoreScreenStateNoLock(altScreen: false);
                if (restoreSavedCursorIfArmed && _restoreMainCursorOnAltExit)
                {
                    ApplyCursorStateNoLock(_savedCursors.Main);
                }

                _restoreMainCursorOnAltExit = false;

                // Reset row-diff cache so the next CaptureRenderSnapshot compares the main-screen
                // rows fresh rather than against stale alt-screen row IDs.
                _hasSnapshotState = false;
                Invalidate();
                OnScreenSwitched?.Invoke(false); // Notify that we switched back to main screen
            }
            finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        }

        /// <summary>
        /// Set scrolling region (DECSTBM) for vim splits, tmux, etc.
        /// </summary>
        public void SetScrollingRegion(int top, int bottom)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                int newTop = Math.Clamp(top, 0, Rows - 1);
                int newBottom = Math.Clamp(bottom, 0, Rows - 1);

                // DECSTBM requires a region spanning at least two lines.
                // Ignore invalid updates instead of silently resetting to full screen.
                if (newTop >= newBottom)
                {
                    return;
                }

                ScrollTop = newTop;
                ScrollBottom = newBottom;
            }
            finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        }
        public List<SearchMatch> FindMatches(string query, bool useRegex, bool caseSensitive)
        {
            if (string.IsNullOrEmpty(query)) return new List<SearchMatch>();

            var matches = new List<SearchMatch>();
            Lock.EnterReadLock();
            try
            {
                System.Text.RegularExpressions.Regex? regex = null;
                if (useRegex)
                {
                    try
                    {
                        var options = caseSensitive ? System.Text.RegularExpressions.RegexOptions.None : System.Text.RegularExpressions.RegexOptions.IgnoreCase;
                        regex = new System.Text.RegularExpressions.Regex(query, options);
                    }
                    catch
                    {
                        // Invalid regex - return empty or treat as literal? 
                        // For now, return empty to indicate error/no match
                        return matches;
                    }
                }

                int totalRows = _scrollback.Count + Rows;
                for (int r = 0; r < totalRows; r++)
                {
                    // Build row string
                    ReadOnlySpan<TerminalCell> cells;
                    TerminalRow? rowObj = null;
                    if (r < _scrollback.Count)
                    {
                        cells = _scrollback.GetRow(r);
                    }
                    else
                    {
                        rowObj = _viewport[r - _scrollback.Count];
                        cells = rowObj.Cells;
                    }

                    // Build row string with mapping for wide/complex characters
                    var sb = new StringBuilder();
                    var colMapping = new List<int>(); // Maps string char index to buffer column

                    for (int c = 0; c < Cols; c++)
                    {
                        var cell = cells[c];
                        if (cell.IsWideContinuation) continue;

                        string? text = (rowObj != null && cell.HasExtendedText) ? rowObj.GetExtendedText(c) : null;
                        text ??= cell.Character.ToString();
                        int startIdx = sb.Length;
                        sb.Append(text);
                        for (int k = 0; k < text.Length; k++) colMapping.Add(c);
                    }
                    string lineText = sb.ToString();

                    if (useRegex && regex != null)
                    {
                        // Regex Search
                        foreach (System.Text.RegularExpressions.Match m in regex.Matches(lineText))
                        {
                            if (m.Success)
                            {
                                int startCol = colMapping[m.Index];
                                int endCol = colMapping[m.Index + m.Length - 1];
                                matches.Add(new SearchMatch(r, startCol, endCol));
                            }
                        }
                    }
                    else
                    {
                        // Text Search
                        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                        int index = lineText.IndexOf(query, comparison);
                        while (index != -1)
                        {
                            int startCol = colMapping[index];
                            int endCol = colMapping[index + query.Length - 1];
                            matches.Add(new SearchMatch(r, startCol, endCol));
                            index = lineText.IndexOf(query, index + 1, comparison);
                        }
                    }
                }
            }
            finally
            {
                Lock.ExitReadLock();
            }
            return matches;
        }

        public int GetGraphemeWidth(string textElement)
        {
            return UnicodeWidth.GetGraphemeWidth(textElement);
        }

        public RenderRowSnapshot GetRowSnapshot(int absRow, int bufferCols)
        {
            AssertLockHeld();
            // This MUST be called under the buffer read/write lock.
            var row = GetRowAbsolute(absRow);
            if (row == null)
            {
                return new RenderRowSnapshot
                {
                    AbsRow = absRow,
                    Revision = 0,
                    Cols = bufferCols,
                    Cells = System.Array.Empty<RenderCellSnapshot>()
                };
            }

            var snapshot = new RenderRowSnapshot
            {
                AbsRow = absRow,
                Revision = row.Revision,
                Cols = bufferCols,
                Cells = new RenderCellSnapshot[bufferCols],
                RowId = row.Id
            };

            PopulateRenderCellsFromRow_NoLock(row, bufferCols, snapshot.Cells);

            return snapshot;
        }

        private static void PopulateRenderCellsFromRow_NoLock(TerminalRow row, int bufferCols, RenderCellSnapshot[] destination)
        {
            int colsToWrite = Math.Min(bufferCols, destination.Length);
            for (int c = 0; c < colsToWrite; c++)
            {
                var cell = (c < row.Cells.Length) ? row.Cells[c] : TerminalCell.Default;
                destination[c] = new RenderCellSnapshot
                {
                    Character = cell.Character,
                    Text = cell.HasExtendedText ? row.GetExtendedText(c) : null,
                    Foreground = cell.Foreground,
                    Background = cell.Background,
                    IsInverse = cell.IsInverse,
                    IsBold = cell.IsBold,
                    IsDefaultForeground = cell.IsDefaultForeground,
                    IsDefaultBackground = cell.IsDefaultBackground,
                    IsWide = cell.IsWide,
                    IsWideContinuation = cell.IsWideContinuation,
                    IsHidden = cell.IsHidden,
                    IsFaint = cell.IsFaint,
                    IsItalic = cell.IsItalic,
                    IsUnderline = cell.IsUnderline,
                    IsBlink = cell.IsBlink,
                    IsStrikethrough = cell.IsStrikethrough,
                    FgIndex = cell.FgIndex,
                    BgIndex = cell.BgIndex
                };
            }
        }

        public List<RenderImageSnapshot> GetVisibleImagesSnapshot(int absDisplayStart, int bufferRows)
        {
            AssertLockHeld();
            // This MUST be called under the buffer read/write lock.
            var list = new List<RenderImageSnapshot>();
            int absEnd = absDisplayStart + bufferRows;

            foreach (var img in _images)
            {
                // Simple visibility check
                if (img.CellY + img.CellHeight > absDisplayStart && img.CellY < absEnd)
                {
                    list.Add(new RenderImageSnapshot
                    {
                        CellX = img.CellX,
                        CellY = img.CellY,
                        CellWidth = img.CellWidth,
                        CellHeight = img.CellHeight,
                        ImageHandle = img.ImageHandle,
                        IsSticky = img.IsSticky
                    });
                }
            }
            return list;
        }

        public void ApplySnapshot(ReplaySnapshot snapshot)
        {
            Lock.EnterWriteLock();
            try
            {
                // Core properties
                _cursorCol = Math.Clamp(snapshot.CursorCol, 0, Math.Max(0, Cols - 1));
                _cursorRow = Math.Clamp(snapshot.CursorRow, 0, Math.Max(0, Rows - 1));
                _isAltScreen = snapshot.IsAltScreen;
                ScrollTop = Math.Clamp(snapshot.ScrollTop, 0, Math.Max(0, Rows - 1));
                ScrollBottom = Math.Clamp(snapshot.ScrollBottom, 0, Math.Max(0, Rows - 1));
                if (ScrollTop > ScrollBottom)
                {
                    ScrollTop = 0;
                    ScrollBottom = Math.Max(0, Rows - 1);
                }

                Modes.IsAutoWrapMode = snapshot.IsAutoWrapMode;
                Modes.IsApplicationCursorKeys = snapshot.IsApplicationCursorKeys;
                Modes.IsOriginMode = snapshot.IsOriginMode;
                Modes.IsBracketedPasteMode = snapshot.IsBracketedPasteMode;
                Modes.IsCursorVisible = snapshot.IsCursorVisible;

                CurrentForeground = TermColor.FromUint(snapshot.CurrentForeground);
                CurrentBackground = TermColor.FromUint(snapshot.CurrentBackground);
                CurrentFgIndex = snapshot.CurrentFgIndex;
                CurrentBgIndex = snapshot.CurrentBgIndex;
                IsDefaultForeground = snapshot.IsDefaultForeground;
                IsDefaultBackground = snapshot.IsDefaultBackground;
                IsInverse = snapshot.IsInverse;
                IsBold = snapshot.IsBold;
                IsFaint = snapshot.IsFaint;
                IsItalic = snapshot.IsItalic;
                IsUnderline = snapshot.IsUnderline;
                IsBlink = snapshot.IsBlink;
                IsStrikethrough = snapshot.IsStrikethrough;
                IsHidden = snapshot.IsHidden;

                // Cells
                if (!string.IsNullOrEmpty(snapshot.CellsBase64))
                {
                    byte[] cellBytes = Convert.FromBase64String(snapshot.CellsBase64);
                    var cellSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, TerminalCell>(cellBytes.AsSpan());

                    int expectedCells = snapshot.Cols * snapshot.Rows;
                    if (cellSpan.Length >= expectedCells)
                    {
                        int rowsToCopy = Math.Min(snapshot.Rows, _viewport.Length);
                        int colsToCopy = Math.Min(snapshot.Cols, Cols);

                        // Update viewport
                        for (int r = 0; r < rowsToCopy; r++)
                        {
                            var row = _viewport[r];
                            Array.Copy(cellSpan.Slice(r * snapshot.Cols, colsToCopy).ToArray(), row.Cells, colsToCopy);

                            if (snapshot.RowWraps != null && r < snapshot.RowWraps.Length)
                            {
                                row.IsWrapped = snapshot.RowWraps[r];
                            }
                            row.TouchRevision();
                            row.ClearExtendedText();
                            row.ClearHyperlinks();
                        }
                    }
                }

                // Extended Text (Emoji)
                if (snapshot.ExtendedText != null)
                {
                    foreach (var kvp in snapshot.ExtendedText)
                    {
                        int r = kvp.Key / snapshot.Cols;
                        int c = kvp.Key % snapshot.Cols;
                        if (r < _viewport.Length && c < Cols)
                        {
                            _viewport[r].SetExtendedText(c, kvp.Value);
                        }
                    }
                }

                _isPendingWrap = false;
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            Invalidate();
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private void AssertLockHeld()
        {
            if (!Lock.IsReadLockHeld && !Lock.IsWriteLockHeld)
            {
                throw new System.InvalidOperationException("Buffer lock MUST be held for this operation.");
            }
        }
    }
}

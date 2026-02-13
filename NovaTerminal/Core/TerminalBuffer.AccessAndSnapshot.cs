using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NovaTerminal.Core.Replay;

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
            if (absRow < _scrollback.Count) return _scrollback[absRow];
            int viewportRow = absRow - _scrollback.Count;
            if (viewportRow < 0 || viewportRow >= Rows) return null;
            return _viewport[viewportRow];
        }

        public TerminalCell GetCellAbsolute(int col, int absRow)
        {
            AssertLockHeld();
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

        public string GetGraphemeAbsolute(int col, int absRow)
        {
            AssertLockHeld();
            if (absRow < 0) return " ";
            var row = GetRowAbsolute(absRow);
            if (row == null || col < 0 || col >= Cols) return " ";
            var cell = row.Cells[col];
            return (cell.HasExtendedText ? row.GetExtendedText(col) : null) ?? cell.Character.ToString();
        }

        public string? GetHyperlinkAbsolute(int col, int absRow)
        {
            bool lockTaken = EnterReadLockIfNeeded();
            try
            {
                if (absRow < 0 || col < 0 || col >= Cols) return null;
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
        // Saved Cursor State (DEC SC / DEC RC)
        public void SaveCursor()
        {
            var target = _isAltScreen ? _savedCursors.Alt : _savedCursors.Main;
            target.Row = CursorRow;
            target.Col = CursorCol;
            target.Foreground = CurrentForeground;
            target.Background = CurrentBackground;
            target.FgIndex = CurrentFgIndex;
            target.BgIndex = CurrentBgIndex;
            target.IsDefaultForeground = IsDefaultForeground;
            target.IsDefaultBackground = IsDefaultBackground;
            target.IsInverse = IsInverse;
            target.IsBold = IsBold;
            target.IsHidden = IsHidden;
            target.IsPendingWrap = _isPendingWrap;
        }

        public void RestoreCursor()
        {
            var source = _isAltScreen ? _savedCursors.Alt : _savedCursors.Main;
            CursorRow = Math.Clamp(source.Row, 0, Rows - 1);
            CursorCol = Math.Clamp(source.Col, 0, Cols - 1);
            CurrentForeground = source.Foreground;
            CurrentBackground = source.Background;
            CurrentFgIndex = source.FgIndex;
            CurrentBgIndex = source.BgIndex;
            IsDefaultForeground = source.IsDefaultForeground;
            IsDefaultBackground = source.IsDefaultBackground;
            IsInverse = source.IsInverse;
            IsBold = source.IsBold;
            IsHidden = source.IsHidden;
            _isPendingWrap = source.IsPendingWrap;
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
            try { System.IO.File.AppendAllText(AppPaths.ResizeDebugLogPath, $"[InsertLines] Count:{count} CursorRow:{_cursorRow}\n"); } catch { }
            bool lockTaken = EnterWriteLockIfNeeded();
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
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
            OnInvalidate?.Invoke();
        }

        public void DeleteLines(int count)
        {
            try { System.IO.File.AppendAllText(AppPaths.ResizeDebugLogPath, $"[DeleteLines] Count:{count} CursorRow:{_cursorRow}\n"); } catch { }
            bool lockTaken = EnterWriteLockIfNeeded();
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
        public void SwitchToAltScreen()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (_isAltScreen) return;

                _isAltScreen = true;
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

                _viewport = _altScreen;    // Switch to alt screen

                // Clear alt screen cells (don't create new rows, reuse if possible)
                for (int i = 0; i < Rows; i++)
                {
                    // Ensure the row has the correct columns (in case of uneven resize if reusing objects? no, we recreated above if mismatch)
                    // If we didn't recreate above, it means dimensions matched.

                    // Reset to default
                    for (int c = 0; c < Cols; c++)
                    {
                        _viewport[i].Cells[c] = TerminalCell.Default;
                    }
                    _viewport[i].TouchRevision();
                }

                _cursorRow = 0;
                _cursorCol = 0;
                Invalidate();
                OnScreenSwitched?.Invoke(true); // Notify that we switched to alt screen
            }
            finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        }

        /// <summary>
        /// Switch back to main screen buffer
        /// </summary>
        public void SwitchToMainScreen()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (!_isAltScreen) return;

                _isAltScreen = false;
                _altScreen = _viewport;    // Save current viewport as alt screen
                _viewport = _mainScreen;   // Restore main screen

                // Reset scrolling region to full screen when switching back to main screen
                ScrollTop = 0;
                ScrollBottom = Rows - 1;

                // Ensure cursor is within bounds after switching back to main screen
                _cursorRow = Math.Clamp(_cursorRow, 0, Rows - 1);
                _cursorCol = Math.Clamp(_cursorCol, 0, Cols - 1);

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
                ScrollTop = Math.Clamp(top, 0, Rows - 1);
                ScrollBottom = Math.Clamp(bottom, 0, Rows - 1);

                // Ensure valid region
                if (ScrollTop > ScrollBottom)
                {
                    ScrollTop = 0;
                    ScrollBottom = Rows - 1;
                }
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
                    var row = (r < _scrollback.Count) ? _scrollback[r] : _viewport[r - _scrollback.Count];

                    // Build row string with mapping for wide/complex characters
                    var sb = new StringBuilder();
                    var colMapping = new List<int>(); // Maps string char index to buffer column

                    for (int c = 0; c < Cols; c++)
                    {
                        var cell = row.Cells[c];
                        if (cell.IsWideContinuation) continue;

                        string text = (cell.HasExtendedText ? row.GetExtendedText(c) : null) ?? cell.Character.ToString();
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

        internal int GetGraphemeWidth(string textElement)
        {
            if (string.IsNullOrEmpty(textElement)) return 0;

            // Common case: single char
            if (textElement.Length == 1)
            {
                return GetRuneWidth(new Rune(textElement[0]));
            }

            int totalBaseWidth = 0;
            bool hasEmoji = false;
            bool hasZwj = false;
            bool hasModifier = false;

            foreach (var rune in textElement.EnumerateRunes())
            {
                int val = rune.Value;
                // ZWJ (U+200D) or Variations/Modifiers
                if (val == 0x200D) { hasZwj = true; continue; }
                if (val >= 0x1F3FB && val <= 0x1F3FF) { hasModifier = true; continue; }
                if (IsCombining(rune)) continue;

                int w = GetRuneWidth(rune);
                if (totalBaseWidth == 0) totalBaseWidth = w;
                else if (!hasZwj) totalBaseWidth += w; // Only sum widths if NOT a ZWJ sequence

                if (w == 2) hasEmoji = true;
            }

            // Normal rule: Graphemes are at least the sum of their base parts.
            // For ZWJ sequences or modified emojis, we treat them as 2 cells if they contain emojis.
            if (hasZwj || hasModifier)
            {
                return hasEmoji ? 2 : Math.Max(1, totalBaseWidth);
            }

            if (totalBaseWidth == 0) return 0;
            return totalBaseWidth;
        }

        private int GetRuneWidth(Rune rune)
        {
            // IMPORTANT: Combiners and Joining characters MUST have 0 width 
            // to avoid moving the cursor if they fail to attach.
            if (IsCombining(rune)) return 0;

            int cp = rune.Value;

            // Zero-width / control / format (safety)
            if (cp < 32 || (cp >= 0x7F && cp <= 0x9F)) return 0;

            // Hangul Jamo (Leading)
            if (cp >= 0x1100 && cp <= 0x115F) return 2;

            // Symbols / Dingbats / Emoticons (Most are 2 cells)
            if (cp >= 0x2329 && cp <= 0x232A) return 2;
            if (cp >= 0x2600 && cp <= 0x27BF) return 2;

            // CJK / Hangul / Fullwidth
            if (cp >= 0x2E80 && cp <= 0xA4CF && cp != 0x303F) return 2;
            if (cp >= 0xAC00 && cp <= 0xD7A3) return 2;
            if (cp >= 0xF900 && cp <= 0xFAFF) return 2;
            if (cp >= 0xFE10 && cp <= 0xFE6F) return 2;
            if (cp >= 0xFF00 && cp <= 0xFFEF) return 2;

            // Primary Emoji & Supplementary CJK Blocks
            if (cp >= 0x1F000 && cp <= 0x1FBFF) return 2;
            if (cp >= 0x20000 && cp <= 0x3FFFF) return 2;

            return 1;
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
                Cells = new RenderCellSnapshot[bufferCols]
            };

            for (int c = 0; c < bufferCols; c++)
            {
                var cell = (c < row.Cells.Length) ? row.Cells[c] : TerminalCell.Default;
                snapshot.Cells[c] = new RenderCellSnapshot
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

            return snapshot;
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
                        Image = img.Image,
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

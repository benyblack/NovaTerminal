using System;
using System.Globalization;
using System.Linq;
using System.Text;
using NovaTerminal.Core.Storage;

namespace NovaTerminal.Core
{
    public partial class TerminalBuffer
    {
        public void WriteChar(char c)
        {
            bool tookLock = false;
            if (!Lock.IsWriteLockHeld)
            {
                Lock.EnterWriteLock();
                tookLock = true;
            }

            try
            {
                WriteCharCore(c);
            }
            finally
            {
                if (tookLock)
                {
                    Lock.ExitWriteLock();
                }
            }
            Invalidate();
        }

        private void WriteCharCore(char c)
        {
            // Only treat as control if it's not a grapheme component (ZWJ, Variation Selectors)
            if (char.IsControl(c) && !char.IsSurrogate(c) && c != '\u200D' && !(c >= '\uFE00' && c <= '\uFE0F'))
            {
                _highSurrogateBuffer = null;
                HandleControlCode(c);
                _isAfterZwj = false;
                _lastCharCol = -1;
            }
            else
            {
                if (char.IsHighSurrogate(c))
                {
                    _highSurrogateBuffer = c;
                    return;
                }

                string grapheme;
                if (_highSurrogateBuffer.HasValue && char.IsLowSurrogate(c))
                {
                    grapheme = new string(new[] { _highSurrogateBuffer.Value, c });
                    _highSurrogateBuffer = null;
                }
                else
                {
                    _highSurrogateBuffer = null;
                    grapheme = c.ToString();
                }

                WriteGraphemeInternal(grapheme);
            }
        }

        private void FlushGrapheme()
        {
            // No-op in new simplified logic, but kept for compatibility if called elsewhere
            _highSurrogateBuffer = null;
        }

        private void HandleControlCode(char c)
        {
            // Cursor logic extracted from old WriteChar
            if (c == '\r')
            {
                _cursorCol = 0;
                _prevCursorCol = _cursorCol;
                _prevCursorRow = _cursorRow;
                _isPendingWrap = false;
            }
            else if (c == '\n' || c == '\u000B' || c == '\u000C') // LF, VT, FF
            {
                if (_cursorRow >= 0 && _cursorRow < Rows) _viewport[_cursorRow].IsWrapped = false;

                // VT100: LF moves to the same horizontal position on the next line.
                // Reset to column 0 ONLY if New Line Mode (LNM) is enabled.
                if (Modes.IsLineFeedNewLineMode)
                {
                    _cursorCol = 0;
                }

                _cursorRow++;
                if (_cursorRow >= Rows) { ScrollUpInternal(); _cursorRow = Rows - 1; }
                _isPendingWrap = false;
            }
            else if (c == '\b')
            {
                if (_cursorCol > 0)
                {
                    // If we were in pending wrap state at the right margin,
                    // backspace moves us back one cell from that margin.
                    _cursorCol--;
                }
                // Handle backing over a wide char? (Should jump 2? standard terminals vary)
                // For now, simple backspace.
                _isPendingWrap = false;
            }
            else if (c == '\t')
            {
                _isPendingWrap = false;
                int spaces = 4 - (_cursorCol % 4);
                for (int i = 0; i < spaces; i++) WriteContentCore(" ");
            }
        }

        public void WriteContent(string text, bool ignored = false)
        {
            if (string.IsNullOrEmpty(text)) return;

            bool tookLock = false;
            if (!Lock.IsWriteLockHeld)
            {
                Lock.EnterWriteLock();
                tookLock = true;
            }

            try
            {
                WriteContentCore(text);
            }
            finally
            {
                if (tookLock)
                {
                    Lock.ExitWriteLock();
                }
            }
            Invalidate();
        }

        private void WriteContentCore(string text)
        {
            FlushGrapheme(); // Ensure any single char buffered by WriteChar is handled
            var enumerator = StringInfo.GetTextElementEnumerator(text);
            while (enumerator.MoveNext())
            {
                WriteGraphemeInternal(enumerator.GetTextElement());
            }
        }

        private void WriteGraphemeInternal(string grapheme)
        {
            if (string.IsNullOrEmpty(grapheme)) return;

            // Guard against lone surrogates from broken UTF-8 (e.g. Yazi on WSL via ConPTY).
            // EnumerateRunes().First() internally calls new Rune(char) which throws on surrogates.
            Rune firstRune;
            if (grapheme.Length == 1)
            {
                if (!Rune.TryCreate(grapheme[0], out firstRune))
                    firstRune = new Rune('?'); // Replace invalid with ? placeholder
            }
            else
            {
                firstRune = grapheme.EnumerateRunes().FirstOrDefault(new Rune('?'));
            }
            bool isCombining = IsCombining(firstRune);


            // ATTACHMENT LOGIC:

            // If it's a combining mark OR we just had a ZWJ, try to attach to the previous cell.
            // We allow this even if _lastCharCol is -1, as the look-back logic can find the base.
            if ((isCombining || _isAfterZwj) && _cursorRow == _lastCharRow)
            {
                int attachCol = -1;

                // 1. Try sequential marker first
                if (_lastCharCol >= 0 && _cursorCol >= _lastCharCol && _cursorCol <= _lastCharCol + 8)
                {
                    // Verify if this is a suitable base for skin tones (look back for a non-space)
                    int searchCol = _cursorCol - 1;
                    while (searchCol >= 0)
                    {
                        var target = _viewport[_cursorRow].Cells[searchCol];

                        if (target.IsWideContinuation) { searchCol--; continue; }
                        if (target.HasExtendedText || (target.Character != ' ' && target.Character != '\0'))
                        {
                            attachCol = searchCol;
                            break;
                        }
                        searchCol--;
                        if (searchCol < _lastCharCol - 4) break; // Don't look too far back
                    }
                }

                // 2. Look-back fallback (only if sequential marker didn't find anything)
                if (attachCol < 0 && _cursorCol > 0)
                {
                    var prev = _viewport[_cursorRow].Cells[_cursorCol - 1];
                    if (prev.IsWideContinuation && _cursorCol > 1)
                    {
                        attachCol = _cursorCol - 2;
                    }
                    else if (prev.HasExtendedText || (prev.Character != ' ' && prev.Character != '\0'))
                    {
                        attachCol = _cursorCol - 1;
                    }
                }

                if (attachCol >= 0)
                {
                    var row = _viewport[_cursorRow];
                    ref var cell = ref row.Cells[attachCol];
                    if (!cell.IsWideContinuation)
                    {
                        string existing = row.GetExtendedText(attachCol) ?? cell.Character.ToString();
                        string merged = existing + grapheme;
                        row.SetExtendedText(attachCol, merged);
                        cell.HasExtendedText = true;
                        cell.IsDirty = true; // Force redraw
                        row.TouchRevision();

                        // Re-evaluate width of the merged cluster
                        int newWidth = GetGraphemeWidth(merged);

                        // ALWAYS enforce wide flag and continuation for width >= 2
                        if (newWidth >= 2)
                        {
                            cell.IsWide = true;
                            if (attachCol + 1 < Cols)
                            {
                                // Ensure next cell is a continuation
                                ref var nextCell = ref row.Cells[attachCol + 1];
                                if (!nextCell.IsWideContinuation)
                                {
                                    SyncPackedState();
                                    nextCell = new TerminalCell(' ', _packedFg, _packedBg, (ushort)(_packedFlags | (ushort)TerminalCellFlags.WideContinuation));
                                    row.SetExtendedText(attachCol + 1, null); // Ensure no old text remains
                                    row.SetHyperlink(attachCol + 1, row.GetHyperlink(attachCol));
                                }

                                // If the cursor was waiting at the next cell, and we just expanded into it, push the cursor forward.
                                if (_cursorCol == attachCol + 1)
                                {
                                    _cursorCol++;
                                    if (_cursorCol > Cols) _cursorCol = Cols;
                                }
                            }
                        }
                    }


                    _isAfterZwj = IsLastRuneZwj(grapheme);
                    Invalidate();
                    return; // CRITICAL: Stop here, don't write again to current cursor
                }
            } // End of attachment attempt logic

            // NORMAL WRITE LOGIC:
            int width = GetGraphemeWidth(grapheme);

            // Handle Pending Wrap State (M1.3)
            if (_isPendingWrap && !isCombining && !_isAfterZwj)
            {
                if (_cursorRow >= 0 && _cursorRow < Rows) _viewport[_cursorRow].IsWrapped = true;
                _cursorCol = 0;
                _cursorRow++;
                if (_cursorRow >= Rows) { ScrollUpInternal(); _cursorRow = Rows - 1; }
                _isPendingWrap = false;
            }

            // Handle auto-wrap
            if (Modes.IsAutoWrapMode)
            {
                if (_cursorCol + width > Cols)
                {
                    if (_cursorRow >= 0 && _cursorRow < Rows) _viewport[_cursorRow].IsWrapped = true;
                    _cursorCol = 0;
                    _cursorRow++;
                    if (_cursorRow >= Rows) { ScrollUpInternal(); _cursorRow = Rows - 1; }
                    _isPendingWrap = false;
                }
            }
            else
            {
                if (_cursorCol + width > Cols) _cursorCol = Cols - width; // Clamp to end
            }

            // Write to buffer
            if (_cursorRow >= 0 && _cursorRow < Rows && _cursorCol >= 0 && _cursorCol < Cols)
            {
                if (Modes.IsInsertMode) InsertCharactersInternal(width);

                var row = _viewport[_cursorRow];

                SyncPackedState();

                // Clear continuations and old extended text
                for (int i = 0; i < width && _cursorCol + i < Cols; i++)
                {
                    row.Cells[_cursorCol + i] = new TerminalCell(' ', _packedFg, _packedBg, _packedFlags);
                    row.SetExtendedText(_cursorCol + i, null);
                    row.SetHyperlink(_cursorCol + i, null);
                }

                ushort flags = _packedFlags;
                if (width >= 2) flags |= (ushort)TerminalCellFlags.Wide;
                if (grapheme.Length > 1 || (grapheme.Length == 1 && char.IsSurrogate(grapheme[0]))) flags |= (ushort)TerminalCellFlags.HasExtendedText;

                row.Cells[_cursorCol] = new TerminalCell(grapheme[0], _packedFg, _packedBg, flags);
                if ((flags & (ushort)TerminalCellFlags.HasExtendedText) != 0)
                {
                    row.SetExtendedText(_cursorCol, grapheme);
                }
                row.SetHyperlink(_cursorCol, _currentHyperlink);

                if (width >= 2 && _cursorCol + 1 < Cols)
                {
                    int maxCont = Math.Min(width, Cols - _cursorCol);
                    for (int i = 1; i < maxCont; i++)
                    {
                        row.Cells[_cursorCol + i] = new TerminalCell(' ', _packedFg, _packedBg, (ushort)(_packedFlags | (ushort)TerminalCellFlags.WideContinuation));
                        row.SetExtendedText(_cursorCol + i, null);
                        row.SetHyperlink(_cursorCol + i, _currentHyperlink);
                    }
                }

                row.TouchRevision();
                _lastCharCol = _cursorCol;
                _lastCharRow = _cursorRow;
                _cursorCol += width;
            }

            // Update Pending Wrap State
            if (Modes.IsAutoWrapMode && _cursorCol >= Cols)
            {
                _isPendingWrap = true;
                _cursorCol = Cols - 1;
            }
            if (!Modes.IsAutoWrapMode && _cursorCol >= Cols)
            {
                _cursorCol = Cols - 1;
            }

            if (_cursorCol > _maxColThisRow) _maxColThisRow = _cursorCol;
            _prevCursorCol = _cursorCol;
            _prevCursorRow = _cursorRow;
            this._isAfterZwj = IsLastRuneZwj(grapheme);
        }

        private bool IsLastRuneZwj(string grapheme)
        {
            if (string.IsNullOrEmpty(grapheme)) return false;
            // ZWJ is U+200D
            foreach (var rune in grapheme.EnumerateRunes())
            {
                if (rune.Value == 0x200D) return true; // Any ZWJ in the grapheme makes it "joining"
            }
            return false;
        }

        private bool IsCombining(Rune rune)
        {
            // ASCII characters are never combining. 
            // This fixes '^' (U+005E) being treated as ModifierSymbol -> Combining (Width 0).
            if (rune.Value < 0x80) return false;

            var cat = Rune.GetUnicodeCategory(rune);

            // Standard combining marks and modifiers
            if (cat == UnicodeCategory.NonSpacingMark ||
                cat == UnicodeCategory.SpacingCombiningMark ||
                cat == UnicodeCategory.EnclosingMark ||
                cat == UnicodeCategory.ModifierSymbol)
                return true;

            // Emoji specific joining and variation characters
            var val = rune.Value;
            if (val >= 0x200B && val <= 0x200F) return true; // ZWSP, ZWNJ, ZWJ, etc
            if (val >= 0xFE00 && val <= 0xFE0F) return true; // Variation Selectors
            if (val >= 0x1F3FB && val <= 0x1F3FF) return true; // Skin Tone Modifiers

            // Tag sequences (for flags etc)
            if (val >= 0xE0020 && val <= 0xE007F) return true;

            return false;
        }

        /// <summary>
        /// Scrolls the viewport up by one line.
        /// Respects the scrolling region (ScrollTop/ScrollBottom).
        /// Only adds to scrollback if scrolling the entire screen.
        /// </summary>
        public void ScrollUp()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                ScrollUpInternal();
            }
            finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
            Invalidate(); // Notify outside lock (optional, Invalidate delegates)
        }

        private void ScrollUpInternal()
        {
            // Check if we are scrolling the explicit region or full screen
            bool isFullScreenScroll = (ScrollTop == 0 && ScrollBottom == Rows - 1);

            // 1. If full screen and main screen, add to scrollback
            if (isFullScreenScroll && !_isAltScreen)
            {
                long prevEvicted = _scrollback.TotalRowsEvicted;
                
                // This copies the cell array into the page-based store;
                // No TerminalRow object is stored in scrollback anymore.
                _scrollback.AppendRow(_viewport[0].Cells, _viewport[0].IsWrapped);

                long newlyEvicted = _scrollback.TotalRowsEvicted - prevEvicted;

                // If we evicted rows (pages), all following rows shifted down in absolute index
                // So we must decrement CellY for ALL images by the number of evicted rows.
                if (newlyEvicted > 0)
                {
                    for (int i = _images.Count - 1; i >= 0; i--)
                    {
                        var img = _images[i];
                        img.CellY -= (int)newlyEvicted;
                        // If the image's entire area is now before the new index 0, prune it.
                        if (img.CellY + img.CellHeight <= 0)
                        {
                            _images.RemoveAt(i);
                        }
                    }
                }
            }
            else
            {
                // Regional scroll-up or Alternate screen scroll (no scrollback growth)
                // Images that are partially or fully in the region need to be shifted UP (CellY--)
                int absTop = _isAltScreen ? ScrollTop : (_scrollback.Count + ScrollTop);
                int absBottom = _isAltScreen ? ScrollBottom : (_scrollback.Count + ScrollBottom);

                for (int i = _images.Count - 1; i >= 0; i--)
                {
                    var img = _images[i];
                    // If image overlaps with the scrolling region, it moves.
                    // Accurate overlap: Ends after top AND Starts before bottom.
                    if (img.IsSticky && img.CellY + img.CellHeight > absTop && img.CellY <= absBottom)
                    {
                        img.CellY--;

                        // If image moved entirely above the region (and it was a regional scroll),
                        // we might prune it if it's no longer visible in the region.
                        // However, standard sticky images just move. Pruning only on MaxHistory is safer.
                        if (img.CellY + img.CellHeight <= absTop && !isFullScreenScroll)
                        {
                            // Optional: _images.RemoveAt(i); 
                        }
                    }
                }
            }

            // 2. Shift rows up within the region
            for (int i = ScrollTop; i < ScrollBottom; i++)
            {
                _viewport[i] = _viewport[i + 1];
            }

            // 3. Create new blank line at the bottom of the region
            _viewport[ScrollBottom] = new TerminalRow(Cols, Theme.Foreground, Theme.Background);
            // Revisions are already handled by being a NEW row (Revision 0 is different from old row)
            // But if we want to be explicit:
            // _viewport[ScrollBottom].TouchRevision();

            // All shifted rows in the region should have their revision incremented 
            // if we are using Absolute Row Index + Revision as the key.
            // If the row object itself is moved to a new index, it MUST be re-rendered 
            // if we cache by viewport index.
            for (int i = ScrollTop; i <= ScrollBottom; i++) _viewport[i].TouchRevision();
        }

        /// <summary>
        /// Scrolls the viewport down by one line.
        /// Respects the scrolling region (ScrollTop/ScrollBottom).
        /// Lines scrolled off the bottom are lost.
        /// </summary>
        public void ScrollDown()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                ScrollDownInternal();
            }
            finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
            Invalidate();
        }

        private void ScrollDownInternal()
        {
            // Update images: Shift images in the region DOWN (CellY++)
            int absTop = _isAltScreen ? ScrollTop : (_scrollback.Count + ScrollTop);
            int absBottom = _isAltScreen ? ScrollBottom : (_scrollback.Count + ScrollBottom);

            for (int i = _images.Count - 1; i >= 0; i--)
            {
                var img = _images[i];
                // If image overlaps with the scrolling region, it moves.
                if (img.IsSticky && img.CellY + img.CellHeight > absTop && img.CellY < absBottom)
                {
                    img.CellY++;
                    // If the image moves below the region, remove it
                    if (img.CellY > absBottom)
                    {
                        _images.RemoveAt(i);
                    }
                }
            }

            // Shift rows down within the region
            for (int i = ScrollBottom; i > ScrollTop; i--)
            {
                _viewport[i] = _viewport[i - 1];
            }

            // Create new blank line at the top of the region
            _viewport[ScrollTop] = new TerminalRow(Cols, Theme.Foreground, Theme.Background);

            for (int i = ScrollTop; i <= ScrollBottom; i++) _viewport[i].TouchRevision();
        }

        public void InsertCharacters(int count)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                InsertCharactersInternal(count);
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
            Invalidate();
        }

        private void InsertCharactersInternal(int count)
        {
            // Lock must be held by caller
            if (_cursorRow < 0 || _cursorRow >= Rows) return;

            int endCol = Math.Min(_cursorCol + count, Cols);
            var row = _viewport[_cursorRow];

            // Shift characters to right
            // Start from end, move char at (c - count) to c
            for (int c = Cols - 1; c >= endCol; c--)
            {
                row.Cells[c] = row.Cells[c - count];
            }

            // Fill gap with default empty cells
            var empty = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground, false, CurrentFgIndex, CurrentBgIndex, false, false, false, false, false, false);
            for (int c = _cursorCol; c < endCol; c++)
            {
                row.Cells[c] = empty;
            }
            row.TouchRevision();
        }

        public void DeleteCharacters(int count)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                DeleteCharactersInternal(count);
            }
            finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
            Invalidate();
        }

        private void DeleteCharactersInternal(int count)
        {
            if (_cursorRow < 0 || _cursorRow >= Rows) return;

            int endCol = Cols - count;
            var row = _viewport[_cursorRow];

            // Shift characters to left
            for (int c = _cursorCol; c < endCol; c++)
            {
                row.Cells[c] = row.Cells[c + count];
            }

            // Update images on this row: shift those to the right of cursor left
            int absY = _isAltScreen ? _cursorRow : (_scrollback.Count + _cursorRow);
            for (int i = _images.Count - 1; i >= 0; i--)
            {
                var img = _images[i];
                if (img.IsSticky && img.CellY == absY && img.CellX >= _cursorCol)
                {
                    img.CellX -= count;
                    if (img.CellX < _cursorCol)
                    {
                        // If it shifted but still starts before cursor? 
                        // Actually, it should probably be removed if its original range was partially deleted.
                        // But for simplicity, if it moves left of cursor, we let it stay or part-clip it.
                        // Most terminals don't handle this perfectly. Let's just shift it.
                    }
                }
            }

            // Fill gap at end with empty cells
            var empty = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground, false, CurrentFgIndex, CurrentBgIndex);
            for (int c = endCol; c < Cols; c++)
            {
                row.Cells[c] = empty;
            }
            row.TouchRevision();
        }
    }
}

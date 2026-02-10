using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NovaTerminal.Core
{
    public class TerminalBuffer
    {
        private class SavedCursorState
        {
            public int Row;
            public int Col;
            public Color Foreground = Colors.LightGray;
            public Color Background = Colors.Black;
            public bool IsInverse;
            public bool IsBold;
        }

        private readonly SavedCursorState _mainSavedCursor = new();
        private readonly SavedCursorState _altSavedCursor = new();
        // Active viewport - what ConPTY writes to (fixed size)
        private TerminalRow[] _viewport;

        // Alternate screen buffer support (for vim, htop, less, etc.)
        private TerminalRow[] _mainScreen;
        private TerminalRow[] _altScreen;
        private List<TerminalRow> _mainScreenScrollback = new List<TerminalRow>(); // Preserve main screen scrollback
        private bool _isAltScreen = false;
        public bool IsAltScreenActive => _isAltScreen;

        // Scrollback buffer - historical lines that scrolled off the top
        private List<TerminalRow> _scrollback = new List<TerminalRow>();
        public int MaxHistory { get; set; } = 10000;

        // Graphics support
        private readonly List<TerminalImage> _images = new();
        public IReadOnlyList<TerminalImage> Images => _images;

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

        // Internal access for rendering to avoid pseudo-recursion
        internal int InternalCursorCol => _cursorCol;
        internal int InternalCursorRow => _cursorRow;
        internal int GetVisualCursorRowInternal(int scrollOffset) => _cursorRow + scrollOffset;

        // Internal access for properties to avoid recursive locking
        internal int InternalTotalLines => _isAltScreen ? Rows : (_scrollback.Count + Rows);

        public IReadOnlyList<TerminalRow> ScrollbackRows => _scrollback;
        public IReadOnlyList<TerminalRow> ViewportRows => _viewport;
        public int TotalLines => _isAltScreen ? Rows : (_scrollback.Count + Rows);

        // Track previous position for auto-clear heuristic
        private int _prevCursorCol = 0;
        private int _prevCursorRow = 0;
        private int _maxColThisRow = 0; // Track furthest column written on current row

        public Color CurrentForeground { get; set; } = Colors.LightGray;
        public Color CurrentBackground { get; set; } = Colors.Black;
        public short CurrentFgIndex { get; set; } = -1;
        public short CurrentBgIndex { get; set; } = -1;
        public bool IsDefaultForeground { get; set; } = true;
        public bool IsDefaultBackground { get; set; } = true;
        public TerminalTheme Theme { get; set; } = TerminalTheme.Dark;
        public bool IsInverse { get; set; }
        public bool IsBold { get; set; }
        public bool IsHidden { get; set; }

        // Mouse reporting modes (for TUI apps like vim, htop)
        public bool MouseModeX10 { get; set; }          // ?1000 - X10 mouse reporting
        public bool MouseModeButtonEvent { get; set; }  // ?1002 - Button event tracking
        public bool MouseModeAnyEvent { get; set; }     // ?1003 - Any event tracking
        public bool MouseModeSGR { get; set; }          // ?1006 - SGR extended mode

        // Input modes
        public bool IsApplicationCursorKeys { get; set; } // ?1 - DECCKM (Application Cursor Keys)
        public bool IsAutoWrapMode { get; set; } = true;  // ?7 - DECAWM (Auto Wrap Mode)

        // Scrolling region support (for vim splits, tmux)
        public int ScrollTop { get; set; } = 0;
        public int ScrollBottom { get; set; }

        public event Action? OnInvalidate;
        public event Action<bool>? OnScreenSwitched; // true for alt screen, false for main screen

        // Thread safety
        public readonly System.Threading.ReaderWriterLockSlim Lock = new System.Threading.ReaderWriterLockSlim(System.Threading.LockRecursionPolicy.NoRecursion);

        public TerminalBuffer(int cols, int rows)
        {
            Cols = cols;
            Rows = rows;
            ScrollBottom = rows - 1;  // Initialize scrolling region to full screen

            CurrentForeground = Theme.Foreground;
            CurrentBackground = Theme.Background;
            IsDefaultForeground = true;
            IsDefaultBackground = true;

            _viewport = new TerminalRow[rows];
            for (int i = 0; i < rows; i++)
            {
                _viewport[i] = new TerminalRow(cols, Theme.Foreground, Theme.Background);
            }

            // Initialize alternate screen buffer
            _mainScreen = _viewport;  // Main screen is the default viewport
            _altScreen = new TerminalRow[rows];
            for (int i = 0; i < rows; i++)
            {
                _altScreen[i] = new TerminalRow(cols, Theme.Foreground, Theme.Background);
            }

            _cursorRow = 0;
            _cursorCol = 0;
            ScrollTop = 0;
            ScrollBottom = rows - 1;
        }

        public void AddImage(TerminalImage image)
        {
            Lock.EnterWriteLock();
            try
            {
                TerminalLogger.Log($"[TERMINAL_BUFFER] AddImage: CellX={image.CellX}, CellY={image.CellY}, W={image.CellWidth}, H={image.CellHeight}");
                _images.Add(image);
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            Invalidate();
        }

        public void ClearImages()
        {
            Lock.EnterWriteLock();
            try
            {
                foreach (var img in _images)
                {
                    // ImageRegistry.Instance.RemoveImage(img.ImageId); // Optional: If we want shared bitmaps
                }
                _images.Clear();
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            Invalidate();
        }

        public void Clear(bool resetCursor = true)
        {
            Lock.EnterWriteLock();
            try
            {
                _scrollback.Clear();
                _images.Clear();
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

        public void Reset()
        {
            // Full Reset (RIS)
            Clear(true);
            Lock.EnterWriteLock();
            try
            {
                ScrollTop = 0;
                ScrollBottom = Rows - 1;
                IsAutoWrapMode = true;
                IsApplicationCursorKeys = false;

                // Reset SGR
                IsInverse = false;
                IsBold = false;
                IsDefaultForeground = true;
                IsDefaultBackground = true;
                CurrentForeground = Theme.Foreground;
                CurrentBackground = Theme.Background;
                CurrentFgIndex = -1;
                CurrentBgIndex = -1;

                // Reset Mouse Modes
                MouseModeX10 = false;
                MouseModeButtonEvent = false;
                MouseModeAnyEvent = false;
                MouseModeSGR = false;

                SwitchToMainScreen();
                // _tabs.Clear(); // tabs not implemented yet
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            OnInvalidate?.Invoke();
        }

        public void UpdateThemeColors(TerminalTheme oldTheme)
        {
            Lock.EnterWriteLock();
            try
            {
                void UpdateCell(ref TerminalCell cell)
                {
                    // Indices take precedence
                    if (cell.FgIndex >= 0 && cell.FgIndex <= 15)
                    {
                        cell.Foreground = Theme.GetAnsiColor(cell.FgIndex, cell.FgIndex >= 8); // Simple mapping
                    }
                    else if (cell.IsDefaultForeground)
                    {
                        cell.Foreground = Theme.Foreground;
                    }

                    if (cell.BgIndex >= 0 && cell.BgIndex <= 15)
                    {
                        cell.Background = Theme.GetAnsiColor(cell.BgIndex, cell.BgIndex >= 8);
                    }
                    else if (cell.IsDefaultBackground)
                    {
                        cell.Background = Theme.Background;
                    }
                }

                for (int r = 0; r < Rows; r++)
                {
                    for (int c = 0; c < Cols; c++)
                    {
                        UpdateCell(ref _viewport[r].Cells[c]);
                    }
                }

                foreach (var row in _scrollback)
                {
                    for (int c = 0; c < row.Cells.Length; c++)
                    {
                        UpdateCell(ref row.Cells[c]);
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



        private char? _highSurrogateBuffer = null;

        public void WriteChar(char c)
        {
            Lock.EnterWriteLock();
            try
            {
                // Handle Control Codes immediately (unless pending surrogate?)
                // Actually, control codes can break a surrogate sequence, so we should check them first OR
                // treat them as flushing the buffer.
                // Simpler: If we have a high surrogate, ONLY the next char being a low surrogate is valid.
                // Anything else flushes the high surrogate as a 'replacement char' or isolated char, then processes new char.

                string? textToWrite = null;
                bool isWide = false;

                if (_highSurrogateBuffer.HasValue)
                {
                    if (char.IsLowSurrogate(c))
                    {
                        // Form complete pair
                        textToWrite = new string(new[] { _highSurrogateBuffer.Value, c });
                        _highSurrogateBuffer = null;

                        // Check width (Naive: Emoji/CJK ranges or use Rune)
                        // Allow simplistic check: assume non-ascii pairs might be wide?
                        // Better: Use a dedicated library or heuristic. For now, assume emojis are wide-ish.
                        // Let's assume IsWide = true for now for surrogates to test emoji
                        isWide = true; // Most surrogate pairs (emoji, CJK extension) are wide.
                    }
                    else
                    {
                        // Invalid sequence. Flush the high surrogate as best effort (or replacement)
                        // Then process current 'c'.
                        // For now, just drop/ignore the broken high surrogate to avoid complexities
                        _highSurrogateBuffer = null;
                        // Process 'c' as normal below
                    }
                }

                if (textToWrite == null)
                {
                    if (char.IsHighSurrogate(c))
                    {
                        _highSurrogateBuffer = c;
                        return; // Wait for low surrogate
                    }

                    // Not a surrogate, or a broke sequence start
                    // Control codes
                    if (c == '\r' || c == '\n' || c == '\b' || c == '\t' || c == '\a')
                    {
                        HandleControlCode(c);
                        // Do not return here - let execution fall through so finally block runs (unlocking)
                        // and then OnInvalidate() runs.
                    }
                    else if (c >= 0x20 && !char.IsSurrogate(c))
                    {
                        textToWrite = c.ToString();
                        // Accurate width detection using Rune
                        var rune = new Rune(c);
                        isWide = GetRuneWidth(rune) == 2;
                    }
                }
                else
                {
                    // textToWrite was set from surrogate pair
                    if (Rune.TryCreate(textToWrite[0], textToWrite[1], out var rune))
                    {
                        isWide = GetRuneWidth(rune) == 2;
                    }
                    else
                    {
                        isWide = true; // Fallback for complex graphemes
                    }
                }

                if (textToWrite != null)
                {
                    WriteContent(textToWrite, isWide);
                }
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            OnInvalidate?.Invoke();
        }

        private void HandleControlCode(char c)
        {
            // Cursor logic extracted from old WriteChar
            if (c == '\r')
            {
                _cursorCol = 0;
                _prevCursorCol = _cursorCol;
                _prevCursorRow = _cursorRow;
            }
            else if (c == '\n')
            {
                if (_cursorRow >= 0 && _cursorRow < Rows) _viewport[_cursorRow].IsWrapped = false;
                _cursorCol = 0;
                _cursorRow++;
                if (_cursorRow >= Rows) { ScrollUpInternal(); _cursorRow = Rows - 1; }
            }
            else if (c == '\b')
            {
                if (_cursorCol > 0) _cursorCol--;
                // Handle backing over a wide char? (Should jump 2? standard terminals vary)
                // For now, simple backspace.
            }
            else if (c == '\t')
            {
                int spaces = 4 - (_cursorCol % 4);
                for (int i = 0; i < spaces; i++) WriteContent(" ", false);
            }
        }

        internal void WriteContent(string text, bool isWide)
        {
            // 1. Wrap if needed
            int width = isWide ? 2 : 1;

            // Handle auto-wrap
            if (!IsAutoWrapMode && _cursorCol + width > Cols)
            {
                _cursorCol = Cols - width; // Clamp to end
            }

            if (IsAutoWrapMode && _cursorCol + width > Cols)
            {
                if (_cursorRow >= 0 && _cursorRow < Rows) _viewport[_cursorRow].IsWrapped = true;
                _cursorCol = 0;
                _cursorRow++;
                if (_cursorRow >= Rows) { ScrollUpInternal(); _cursorRow = Rows - 1; }
            }

            // 2. Write
            if (_cursorRow >= 0 && _cursorRow < Rows && _cursorCol >= 0 && _cursorCol < Cols)
            {
                // If isWide, we need space for 2 cells.
                if (isWide && _cursorCol + 1 < Cols)
                {
                    _viewport[_cursorRow].Cells[_cursorCol] = new TerminalCell(text, CurrentForeground, CurrentBackground, IsInverse, IsBold, IsDefaultForeground, IsDefaultBackground, IsHidden, CurrentFgIndex, CurrentBgIndex, true);
                    _viewport[_cursorRow].Cells[_cursorCol + 1] = new TerminalCell(' ', CurrentForeground, CurrentBackground, IsInverse, IsBold, IsDefaultForeground, IsDefaultBackground, IsHidden, CurrentFgIndex, CurrentBgIndex) { IsWideContinuation = true };
                    _cursorCol += 2;
                }
                else
                {
                    // Note: if isWide is true but we are at the last column and AutoWrap is off, 
                    // width was 2 but we only write 1 cell. The renderer handles IsWide=true by expanding,
                    // so we still pass isWide to the cell if it's the intended width.
                    _viewport[_cursorRow].Cells[_cursorCol] = new TerminalCell(text, CurrentForeground, CurrentBackground, IsInverse, IsBold, IsDefaultForeground, IsDefaultBackground, IsHidden, CurrentFgIndex, CurrentBgIndex, isWide);
                    _cursorCol++;
                }

                // If IsWide but no space (last col), we might force wrap or clipped?
                // For simplified logic: if generic wide char hits exact last col, wrap happens above.
                // If it fits, we write.
            }

            if (_cursorCol > _maxColThisRow) _maxColThisRow = _cursorCol;
            _prevCursorCol = _cursorCol;
            _prevCursorRow = _cursorRow;
        }

        /// <summary>
        /// Scrolls the viewport up by one line, moving top line to scrollback
        /// </summary>
        /// <summary>
        /// Scrolls the viewport up by one line.
        /// Respects the scrolling region (ScrollTop/ScrollBottom).
        /// Only adds to scrollback if scrolling the entire screen.
        /// </summary>
        public void ScrollUp()
        {
            Lock.EnterWriteLock();
            try
            {
                ScrollUpInternal();
            }
            finally { Lock.ExitWriteLock(); }
            Invalidate(); // Notify outside lock (optional, Invalidate delegates)
        }

        private void ScrollUpInternal()
        {
            // Check if we are scrolling the explicit region or full screen
            bool isFullScreenScroll = (ScrollTop == 0 && ScrollBottom == Rows - 1);

            // 1. If full screen and main screen, add to scrollback
            if (isFullScreenScroll && !_isAltScreen)
            {
                _scrollback.Add(_viewport[0]);
                if (_scrollback.Count > MaxHistory)
                {
                    _scrollback.RemoveAt(0);

                    // When physically removing row 0, all following rows shift down in index
                    // So we must decrement CellY for ALL images.
                    for (int i = _images.Count - 1; i >= 0; i--)
                    {
                        var img = _images[i];
                        img.CellY--;
                        // If the image's entire area is now before the new index 0, prune it.
                        if (img.CellY + img.CellHeight <= 0)
                        {
                            _images.RemoveAt(i);
                        }
                    }
                }
                // Note: If NOT pruning, we don't shift CellY. The absolute index of 
                // viewport[0] didn't change (it just became the last index of scrollback).
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
        }

        /// <summary>
        /// Scrolls the viewport down by one line.
        /// Respects the scrolling region (ScrollTop/ScrollBottom).
        /// Lines scrolled off the bottom are lost.
        /// </summary>
        public void ScrollDown()
        {
            Lock.EnterWriteLock();
            try
            {
                ScrollDownInternal();
            }
            finally { Lock.ExitWriteLock(); }
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
        }

        public void InsertCharacters(int count)
        {
            Lock.EnterWriteLock();
            try
            {
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
                var empty = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground, false, CurrentFgIndex, CurrentBgIndex);
                for (int c = _cursorCol; c < endCol; c++)
                {
                    row.Cells[c] = empty;
                }
            }
            finally { Lock.ExitWriteLock(); }
            Invalidate(); // Use Invalidate() which handles the event call
        }

        public void DeleteCharacters(int count)
        {
            Lock.EnterWriteLock();
            try
            {
                DeleteCharactersInternal(count);
            }
            finally { Lock.ExitWriteLock(); }
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

                if (newCols == oldCols)
                {
                    // FAST PATH: Width hasn't changed, no wrapping logic needed.
                    // Just adjust Rows and redistribution.
                    Rows = newRows;

                    if (_isAltScreen)
                    {
                        var oldAlt = _viewport;
                        _viewport = new TerminalRow[newRows];
                        for (int i = 0; i < newRows; i++)
                        {
                            if (i < oldAlt.Length) _viewport[i] = oldAlt[i];
                            else _viewport[i] = new TerminalRow(newCols, Theme.Foreground, Theme.Background);
                        }
                        _cursorRow = Math.Clamp(_cursorRow, 0, newRows - 1);
                    }
                    else
                    {
                        // Main screen: Still needs redistribution to maintain scrollback vs viewport split
                        // but we don't need the expensive logical line reconstruction.

                        // Combine all current rows
                        var all = new List<TerminalRow>(_scrollback.Count + oldRows);
                        all.AddRange(_scrollback);
                        all.AddRange(_viewport);

                        _scrollback.Clear();
                        _viewport = new TerminalRow[newRows];

                        int total = all.Count;
                        int vpStart = Math.Max(0, total - newRows);

                        for (int i = 0; i < vpStart; i++) _scrollback.Add(all[i]);
                        for (int i = 0; i < newRows; i++)
                        {
                            if (vpStart + i < total) _viewport[i] = all[vpStart + i];
                            else _viewport[i] = new TerminalRow(newCols, Theme.Foreground, Theme.Background);
                        }

                        // Clamping
                        _cursorRow = Math.Clamp(_cursorRow + (oldRows - newRows), 0, newRows - 1); // rough guess, or smarter mapping
                                                                                                   // Actually, _cursorRow is relative to Top of Viewport. 
                                                                                                   // If we GROW the viewport DOWN, _cursorRow stay same.
                                                                                                   // If we GROW the viewport UP (anchored to bottom), _cursorRow changes.
                                                                                                   // Our current 'vpStart' anchors to BOTTOM of content.

                        _cursorRow = Math.Clamp(_cursorRow, 0, Rows - 1);
                        _cursorCol = Math.Clamp(_cursorCol, 0, Cols);
                    }

                    ScrollTop = 0;
                    ScrollBottom = Rows - 1;
                    return;
                }

                Cols = newCols;
                Rows = newRows;

                if (_isAltScreen)
                {
                    // 1. Resize Alt Screen (Current Viewport)
                    // TUI apps will redraw, so we just need a valid buffer of the new size.
                    // We preserve what fits top-left to avoid flashing empty if redraw is slow.
                    var oldAlt = _viewport;
                    _viewport = new TerminalRow[newRows];
                    for (int i = 0; i < newRows; i++)
                    {
                        _viewport[i] = new TerminalRow(newCols, Theme.Foreground, Theme.Background);
                        if (i < oldAlt.Length)
                        {
                            int copyCols = Math.Min(oldCols, newCols);
                            for (int c = 0; c < copyCols; c++)
                            {
                                _viewport[i].Cells[c] = oldAlt[i].Cells[c];
                            }
                        }
                    }

                    // Cursor clamping for Alt Screen
                    _cursorRow = Math.Clamp(_cursorRow, 0, newRows - 1);
                    _cursorCol = Math.Clamp(_cursorCol, 0, newCols);

                    // 2. Reflow Main Screen (Background)
                    // We temporarily swap current viewport to MainScreen to let Reflow process it.
                    var activeAlt = _viewport;
                    _viewport = _mainScreen;
                    try
                    {
                        // Reflow Main Screen (updates _viewport and _scrollback)
                        Reflow(oldCols, oldRows, newCols, newRows);
                        _mainScreen = _viewport; // Update stored main screen reference
                    }
                    finally
                    {
                        _viewport = activeAlt; // Restore Alt Screen
                    }
                }
                else
                {
                    // Normal Main Screen Resize
                    Reflow(oldCols, oldRows, newCols, newRows);

                    // Cursor clamping for Main Screen
                    _cursorRow = Math.Clamp(_cursorRow, 0, Rows - 1);
                    _cursorCol = Math.Clamp(_cursorCol, 0, Cols);
                }

                // Reset scrolling region to full screen on resize (standard terminal behavior)
                ScrollTop = 0;
                ScrollBottom = Rows - 1;

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

            int absMainSavedIdx = _scrollback.Count + _mainSavedCursor.Row;
            int mainSavedLogicalIdx = -1;
            int mainSavedInLogicalOffset = -1;

            int absAltSavedIdx = _scrollback.Count + _altSavedCursor.Row;
            int altSavedLogicalIdx = -1;
            int altSavedInLogicalOffset = -1;

            // 2. Physical Extraction with Padding Trim

            // Calculate how many viewport rows have content
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
                if (!isEmpty || i <= _cursorRow || i == _mainSavedCursor.Row || i == _altSavedCursor.Row) lastActiveVpRow = i;
            }

            int vpRowsToTake = lastActiveVpRow + 1;
            int totalPhysRows = _scrollback.Count + vpRowsToTake;

            // Rent array to avoid LOH/large allocation
            var allPhysicalRows = System.Buffers.ArrayPool<TerminalRow>.Shared.Rent(totalPhysRows);

            // 3. Metadata-Aware Logical Reconstruction
            var logicalLines = new List<(List<TerminalCell> Cells, bool IsWrapped, int StartPhysIdx)>(totalPhysRows);

            try
            {
                // Fill rented array
                for (int i = 0; i < _scrollback.Count; i++) allPhysicalRows[i] = _scrollback[i];
                for (int i = 0; i < vpRowsToTake; i++) allPhysicalRows[_scrollback.Count + i] = _viewport[i];

                List<TerminalCell>? currentLogical = null;
                int currentStartPhys = -1;

                // Iterate using totalPhysRows count
                for (int i = 0; i < totalPhysRows; i++)
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

                    if (i == absMainSavedIdx)
                    {
                        mainSavedLogicalIdx = logicalLines.Count;
                        mainSavedInLogicalOffset = currentLogical.Count + _mainSavedCursor.Col;
                    }

                    if (i == absAltSavedIdx)
                    {
                        altSavedLogicalIdx = logicalLines.Count;
                        altSavedInLogicalOffset = currentLogical.Count + _altSavedCursor.Col;
                    }

                    int validLen = physRow.Cells.Length;
                    if (!physRow.IsWrapped)
                    {
                        // Smart trimming: Calculate last relevant content index
                        // Include cells that are non-space OR have non-default background
                        int lastContentIdx = -1;
                        for (int scan = 0; scan < physRow.Cells.Length; scan++)
                        {
                            var cell = physRow.Cells[scan];
                            if ((cell.Character != ' ' && cell.Character != '\0') || !cell.IsDefaultBackground)
                            {
                                lastContentIdx = scan;
                            }
                        }

                        // Determine valid length based on content
                        if (lastContentIdx >= 0)
                        {
                            validLen = lastContentIdx + 1;
                        }
                        else
                        {
                            validLen = 0;
                        }

                        // Special case: Preserve padding up to the cursor if it's on this row
                        if (i == absCursorPhysicalIdx && _cursorCol > validLen)
                        {
                            validLen = _cursorCol;
                        }
                    }

                    // Improved sparse row detection: Find the LARGEST contiguous gap
                    // This preserves middle content (e.g. "Left ... Middle ... Right")
                    bool isSparseRowRepositioned = false;
                    if (!physRow.IsWrapped && i >= Math.Max(0, absCursorPhysicalIdx - 2) && i <= absCursorPhysicalIdx)
                    {
                        // Find largest gap strictly BETWEEN content
                        // We need to know if the gap is followed by content, otherwise it's just trailing space
                        // Scan logic:
                        // 1. Identify all gaps.
                        // 2. Identify the gap that is:
                        //    a) Large (> 10)
                        //    b) Followed by content (not end of line)
                        //    c) The largest such gap in the row

                        int bestGapStart = -1;
                        int bestGapLength = 0;

                        int currentScanStart = -1;
                        int currentScanLength = 0;

                        // First we need to find the "end of row content" to ignore trailing spaces
                        int lastContentIndex = -1;
                        for (int scan = physRow.Cells.Length - 1; scan >= 0; scan--)
                        {
                            var cell = physRow.Cells[scan];
                            if (cell.Character != ' ' && cell.Character != '\0')
                            {
                                lastContentIndex = scan;
                                break;
                            }
                        }

                        if (lastContentIndex > 0)
                        {
                            // ONLY Scan up to lastContentIndex
                            // This ensures any gap we find implies there is content AFTER it.
                            for (int scan = 0; scan <= lastContentIndex; scan++)
                            {
                                var cell = physRow.Cells[scan];
                                bool isSpace = (cell.Character == ' ' || cell.Character == '\0');

                                if (isSpace)
                                {
                                    if (currentScanStart == -1) currentScanStart = scan;
                                    currentScanLength++;
                                }
                                else
                                {
                                    if (currentScanStart != -1)
                                    {
                                        if (currentScanLength > bestGapLength)
                                        {
                                            bestGapLength = currentScanLength;
                                            bestGapStart = currentScanStart;
                                        }
                                        currentScanStart = -1;
                                        currentScanLength = 0;
                                    }
                                }
                            }
                            // Check gap if content resumes exactly at lastContentIndex? handled by loop
                            // The loop stops AT lastContentIndex. If the character at lastContentIndex is content,
                            // the else block triggers and we check the gap before it. Correct.
                        }

                        // Determine threshold for gap
                        // Standard: 10 spaces
                        // Special: 2 spaces IF the content touches the right edge (implies a shrunk right-prompt)
                        bool isRightPinned = lastContentIndex == physRow.Cells.Length - 1;
                        int gapThreshold = isRightPinned ? 2 : 10;

                        if (bestGapLength >= gapThreshold)
                        {
                            // We found a split!
                            // Left+Middle = 0 .. bestGapStart (exclusive)
                            // Gap = bestGapStart .. bestGapStart + bestGapLength
                            // Right = bestGapStart + bestGapLength .. lastContentIndex (inclusive)

                            int gapStart = bestGapStart;
                            int gapEnd = bestGapStart + bestGapLength;
                            int rightStart = gapEnd;
                            int rightEnd = lastContentIndex;

                            // Extract Left+Middle
                            for (int k = 0; k < gapStart; k++)
                            {
                                currentLogical.Add(physRow.Cells[k]);
                            }

                            // Extract Right Part
                            var rightCells = new List<TerminalCell>();
                            for (int k = rightStart; k <= rightEnd; k++)
                            {
                                rightCells.Add(physRow.Cells[k]);
                            }

                            // Calculate new position
                            int rightBlockWidth = rightCells.Count;
                            int newRightPos = newCols - rightBlockWidth;

                            int currentPos = currentLogical.Count; // This is effectively gapStart

                            if (newRightPos > currentPos + 2 && (newRightPos + rightBlockWidth) <= newCols)
                            {
                                // Fill spaces
                                var spaceFill = new TerminalCell(' ', Theme.Foreground, Theme.Background, false, false, true, true);
                                for (int s = currentPos; s < newRightPos; s++)
                                {
                                    currentLogical.Add(spaceFill);
                                }
                                // Add right content
                                currentLogical.AddRange(rightCells);
                            }
                            else
                            {
                                // Truncate/Squish
                                var spaceFill = new TerminalCell(' ', Theme.Foreground, Theme.Background, false, false, true, true);
                                currentLogical.Add(spaceFill);
                                currentLogical.Add(spaceFill);

                                int available = newCols - currentLogical.Count;
                                if (available > 0)
                                {
                                    int take = Math.Min(available, rightCells.Count);
                                    int startOffset = rightCells.Count - take;
                                    for (int k = startOffset; k < rightCells.Count; k++)
                                        currentLogical.Add(rightCells[k]);
                                }
                            }
                            isSparseRowRepositioned = true;
                        }

                    }

                    // Normal processing if not sparse row or repositioning failed
                    if (!isSparseRowRepositioned)
                    {
                        for (int k = 0; k < validLen; k++) currentLogical.Add(physRow.Cells[k]);
                    }

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
            }
            finally
            {
                System.Buffers.ArrayPool<TerminalRow>.Shared.Return(allPhysicalRows);
            }


            // 5. Distribution logic
            _scrollback.Clear();
            _viewport = new TerminalRow[newRows];
            // Pre-allocate for the typical case of 1.2x expansion due to wrapping
            var allFlowedRows = new List<TerminalRow>((int)(logicalLines.Count * 1.2));

            int newCursorPhysRow = -1;
            int newCursorPhysCol = -1;
            int newMainSavedPhysRow = -1;
            int newMainSavedPhysCol = -1;
            int newAltSavedPhysRow = -1;
            int newAltSavedPhysCol = -1;
            int historyRowCount = 0; // Tracks physical rows generated from original history
            var newStartFlowIndices = new int[logicalLines.Count];

            // 5b. Anchor Images to Logical Positions before Reflow
            var imageAnchors = new List<(TerminalImage Image, int LogicalLineIdx, int OffsetInLogicalLine)>();
            for (int imgIdx = 0; imgIdx < _images.Count; imgIdx++)
            {
                var img = _images[imgIdx];
                // Find which logical line contains img.CellY
                for (int idx = 0; idx < logicalLines.Count; idx++)
                {
                    var start = logicalLines[idx].StartPhysIdx;
                    var end = (idx + 1 < logicalLines.Count) ? logicalLines[idx + 1].StartPhysIdx : allPhysicalRows.Length;
                    if (img.CellY >= start && img.CellY < end)
                    {
                        int rowOffset = img.CellY - start;
                        int offsetInLine = rowOffset * oldCols + img.CellX;
                        imageAnchors.Add((img, idx, offsetInLine));
                        break;
                    }
                }
            }

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
                    if (i == mainSavedLogicalIdx) { newMainSavedPhysRow = allFlowedRows.Count; newMainSavedPhysCol = 0; }
                    if (i == altSavedLogicalIdx) { newAltSavedPhysRow = allFlowedRows.Count; newAltSavedPhysCol = 0; }
                    allFlowedRows.Add(new TerminalRow(newCols, Theme.Foreground, Theme.Background));
                }
                else
                {
                    int processed = 0;
                    while (processed < lineCells.Count)
                    {
                        int remaining = lineCells.Count - processed;
                        int take = Math.Min(remaining, newCols);

                        // Prevent splitting a wide character across lines
                        if (take < remaining && take > 0 && lineCells[processed + take - 1].IsWide)
                        {
                            take--; // This row will end with a space, wide char moves to next row
                        }

                        // If take is 0 but we have remaining (newCols is 1 and we have a wide char),
                        // we're forced to just take it and let it be clipped, otherwise infinite loop.
                        if (take == 0 && remaining > 0) take = 1;

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

                        if (i == mainSavedLogicalIdx)
                        {
                            if (mainSavedInLogicalOffset >= processed && mainSavedInLogicalOffset < processed + newCols)
                            {
                                newMainSavedPhysRow = allFlowedRows.Count;
                                newMainSavedPhysCol = mainSavedInLogicalOffset - processed;
                            }
                        }

                        if (i == altSavedLogicalIdx)
                        {
                            if (altSavedInLogicalOffset >= processed && altSavedInLogicalOffset < processed + newCols)
                            {
                                newAltSavedPhysRow = allFlowedRows.Count;
                                newAltSavedPhysCol = altSavedInLogicalOffset - processed;
                            }
                        }

                        var row = new TerminalRow(newCols, Theme.Foreground, Theme.Background);
                        for (int c = 0; c < take; c++) row.Cells[c] = lineCells[processed + c];

                        // Style-Aware Padding
                        if (take < newCols)
                        {
                            // We use TRUE default style for padding, NOT the last character's style.
                            // This prevents "Background Leakage" (e.g. blue/green bars) when resizing.
                            var def = new TerminalCell(' ', Theme.Foreground, Theme.Background, false, false, true, true);
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

                newStartFlowIndices[i] = startFlowIndex;
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

            int discardedRows = 0;
            if (_scrollback.Count > MaxHistory)
            {
                discardedRows = _scrollback.Count - MaxHistory;
                _scrollback.RemoveRange(0, discardedRows);
                sbCount -= discardedRows;
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

            // 8. Reposition Images
            for (int i = _images.Count - 1; i >= 0; i--)
            {
                var img = _images[i];
                // Find anchor
                var anchor = imageAnchors.FirstOrDefault(a => a.Image == img);
                if (anchor.Image != null)
                {
                    int newY = newStartFlowIndices[anchor.LogicalLineIdx] + (anchor.OffsetInLogicalLine / newCols);
                    img.CellY = newY - discardedRows;
                    img.CellX = anchor.OffsetInLogicalLine % newCols;

                    // Prune if shifted out of history bounds
                    if (img.CellY + img.CellHeight < 0)
                    {
                        _images.RemoveAt(i);
                    }
                }
            }

            // 7b. Restore Saved Cursors
            if (newMainSavedPhysRow != -1)
            {
                if (newMainSavedPhysRow < sbCount) _mainSavedCursor.Row = 0;
                else _mainSavedCursor.Row = newMainSavedPhysRow - sbCount;
                _mainSavedCursor.Col = Math.Clamp(newMainSavedPhysCol, 0, newCols);
            }
            if (newAltSavedPhysRow != -1)
            {
                if (newAltSavedPhysRow < sbCount) _altSavedCursor.Row = 0;
                else _altSavedCursor.Row = newAltSavedPhysRow - sbCount;
                _altSavedCursor.Col = Math.Clamp(newAltSavedPhysCol, 0, newCols);
            }

            // 8. Conditional Cursor Row Clearing (REFINED)
            // Clear ONLY truly empty padding rows on horizontal resize, not actual wrapped content.
            // This prevents duplication in CMD while preserving oh-my-posh sparse prompts in PowerShell.
            // 
            // Rationale:
            // - Horizontal resize: Width changes cause line rewrapping. Some shells may duplicate prompts.
            // - We only clear rows that are confirmed empty, not rows with actual content.
            // - This preserves oh-my-posh right-aligned content that wraps to the next row.
            if (newCols != oldCols)
            {
                if (_cursorRow >= 0 && _cursorRow < newRows && _cursorRow + 1 < newRows)
                {
                    var nextRow = _viewport[_cursorRow + 1];

                    // Only clear if:
                    // 1. Row is NOT wrapped (not a continuation of a wrapped line)
                    // 2. Row is completely empty (no non-space content)
                    if (!nextRow.IsWrapped)
                    {
                        // Check if row has any actual content
                        bool hasContent = false;
                        for (int c = 0; c < nextRow.Cells.Length; c++)
                        {
                            if (nextRow.Cells[c].Character != ' ' && nextRow.Cells[c].Character != '\0')
                            {
                                hasContent = true;
                                break;
                            }
                        }

                        // Only clear if truly empty (no content)
                        if (!hasContent)
                        {
                            _viewport[_cursorRow + 1] = new TerminalRow(newCols, Theme.Foreground, Theme.Background);
                        }
                    }
                }
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
                // Use clean default background for new space
                var fillCell = new TerminalCell(' ', Theme.Foreground, Theme.Background,
                                                false, false, true, true);

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
        public void SaveCursor()
        {
            var target = _isAltScreen ? _altSavedCursor : _mainSavedCursor;
            target.Row = CursorRow;
            target.Col = CursorCol;
            target.Foreground = CurrentForeground;
            target.Background = CurrentBackground;
            target.IsInverse = IsInverse;
            target.IsBold = IsBold;
        }

        public void RestoreCursor()
        {
            var source = _isAltScreen ? _altSavedCursor : _mainSavedCursor;
            CursorRow = Math.Clamp(source.Row, 0, Rows - 1);
            CursorCol = Math.Clamp(source.Col, 0, Cols - 1);
            CurrentForeground = source.Foreground;
            CurrentBackground = source.Background;
            IsInverse = source.IsInverse;
            IsBold = source.IsBold;
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

        public void EraseLineAll(int rowIndex)
        {
            Lock.EnterWriteLock();
            try
            {
                if (rowIndex < 0 || rowIndex >= Rows) return;
                var row = _viewport[rowIndex];
                for (int i = 0; i < Cols; i++)
                {
                    row.Cells[i] = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground);
                }
            }
            finally { Lock.ExitWriteLock(); }
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

        /// <summary>
        /// Switch to alternate screen buffer (used by vim, htop, less, etc.)
        /// </summary>
        public void SwitchToAltScreen()
        {
            Lock.EnterWriteLock();
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
                }

                _cursorRow = 0;
                _cursorCol = 0;
                Invalidate();
                OnScreenSwitched?.Invoke(true); // Notify that we switched to alt screen
            }
            finally { Lock.ExitWriteLock(); }
        }

        /// <summary>
        /// Switch back to main screen buffer
        /// </summary>
        public void SwitchToMainScreen()
        {
            Lock.EnterWriteLock();
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
            finally { Lock.ExitWriteLock(); }
        }

        /// <summary>
        /// Set scrolling region (DECSTBM) for vim splits, tmux, etc.
        /// </summary>
        public void SetScrollingRegion(int top, int bottom)
        {
            Lock.EnterWriteLock();
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
            finally { Lock.ExitWriteLock(); }
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

                        string text = cell.Text ?? cell.Character.ToString();
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

        private int GetRuneWidth(Rune rune)
        {
            int cp = rune.Value;
            // Simple check for CJK/Emoji ranges:
            // 0x1100-0x115F: Hangul Jamo
            // 0x2329-0x232A: Quotes
            // 0x2E80-0xA4CF: CJK Radicals, Symbols, Punctuation, Ideographs
            // 0xAC00-0xD7A3: Hangul Syllables
            // 0xF900-0xFAFF: CJK Compatibility Ideographs
            // 0xFE10-0xFE19: Vertical Forms
            // 0xFE30-0xFE6F: CJK Compatibility Forms
            // 0xFF00-0xFF60: Fullwidth Forms
            // 0xFFE0-0xFFE6: Fullwidth Forms
            // 0x20000-0x3FFFF: Supplementary Planes (mostly CJK)

            if (cp >= 0x1100 && cp <= 0x115F) return 2;
            if (cp == 0x2329 || cp == 0x232A) return 2;
            if (cp >= 0x2E80 && cp <= 0xA4CF && cp != 0x303F) return 2;
            if (cp >= 0xAC00 && cp <= 0xD7A3) return 2;
            if (cp >= 0xF900 && cp <= 0xFAFF) return 2;
            if (cp >= 0xFE10 && cp <= 0xFE19) return 2;
            if (cp >= 0xFE30 && cp <= 0xFE6F) return 2;
            if (cp >= 0xFF00 && cp <= 0xFF60) return 2;
            if (cp >= 0xFFE0 && cp <= 0xFFE6) return 2;
            if (cp >= 0x1F000 && cp <= 0x1FAFF) return 2;
            if (cp >= 0x20000 && cp <= 0x3FFFF) return 2;

            return 1;
        }
    }
}

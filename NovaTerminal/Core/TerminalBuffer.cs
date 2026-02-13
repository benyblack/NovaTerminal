
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Globalization;

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("NovaTerminal.Tests")]

namespace NovaTerminal.Core
{
    public class TerminalBuffer
    {
        private readonly SavedCursorStates _savedCursors = new();
        // Active viewport - what ConPTY writes to (fixed size)
        private TerminalRow[] _viewport;

        // Alternate screen buffer support (for vim, htop, less, etc.)
        private TerminalRow[] _mainScreen;
        private TerminalRow[] _altScreen;
        private CircularBuffer<TerminalRow> _mainScreenScrollback; // Preserve main screen scrollback
        private bool _isAltScreen = false;
        public bool IsAltScreenActive => _isAltScreen;

        // Scrollback buffer - historical lines that scrolled off the top
        private CircularBuffer<TerminalRow> _scrollback;
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
                try
                {
                    _cursorCol = value;
                    _isPendingWrap = false;
                }
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
                try
                {
                    _cursorRow = value;
                    _isPendingWrap = false;
                }
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

        private char? _highSurrogateBuffer = null;
        private int _lastCharCol = -1;
        private int _lastCharRow = -1;
        private bool _isAfterZwj = false;
        private uint _packedFg;
        private uint _packedBg;
        private ushort _packedFlags;
        private bool _isStyleDirty = true;

        private void SyncPackedState()
        {
            if (!_isStyleDirty) return;

            _packedFg = IsDefaultForeground ? Theme.Foreground.ToUint() : (CurrentFgIndex >= 0 ? (uint)CurrentFgIndex : CurrentForeground.ToUint());
            _packedBg = IsDefaultBackground ? Theme.Background.ToUint() : (CurrentBgIndex >= 0 ? (uint)CurrentBgIndex : CurrentBackground.ToUint());

            ushort f = (ushort)TerminalCellFlags.Dirty;
            if (IsBold) f |= (ushort)TerminalCellFlags.Bold;
            if (IsItalic) f |= (ushort)TerminalCellFlags.Italic;
            if (IsInverse) f |= (ushort)TerminalCellFlags.Inverse;
            if (IsUnderline) f |= (ushort)TerminalCellFlags.Underline;
            if (IsStrikethrough) f |= (ushort)TerminalCellFlags.Strikethrough;
            if (IsBlink) f |= (ushort)TerminalCellFlags.Blink;
            if (IsFaint) f |= (ushort)TerminalCellFlags.Faint;
            if (IsHidden) f |= (ushort)TerminalCellFlags.Hidden;
            if (IsDefaultForeground) f |= (ushort)TerminalCellFlags.DefaultForeground;
            if (IsDefaultBackground) f |= (ushort)TerminalCellFlags.DefaultBackground;
            if (CurrentFgIndex >= 0) f |= (ushort)TerminalCellFlags.PaletteForeground;
            if (CurrentBgIndex >= 0) f |= (ushort)TerminalCellFlags.PaletteBackground;

            _packedFlags = f;
            _isStyleDirty = false;
        }

        private TermColor _currentForeground = TermColor.LightGray;
        public TermColor CurrentForeground { get => _currentForeground; set { _currentForeground = value; _isStyleDirty = true; _isDefaultForeground = false; } }

        private TermColor _currentBackground = TermColor.Black;
        public TermColor CurrentBackground { get => _currentBackground; set { _currentBackground = value; _isStyleDirty = true; _isDefaultBackground = false; } }

        private short _currentFgIndex = -1;
        public short CurrentFgIndex { get => _currentFgIndex; set { _currentFgIndex = value; _isStyleDirty = true; if (value >= 0) _isDefaultForeground = false; } }

        private short _currentBgIndex = -1;
        public short CurrentBgIndex { get => _currentBgIndex; set { _currentBgIndex = value; _isStyleDirty = true; if (value >= 0) _isDefaultBackground = false; } }

        private bool _isDefaultForeground = true;
        public bool IsDefaultForeground { get => _isDefaultForeground; set { _isDefaultForeground = value; _isStyleDirty = true; } }

        private bool _isDefaultBackground = true;
        public bool IsDefaultBackground { get => _isDefaultBackground; set { _isDefaultBackground = value; _isStyleDirty = true; } }

        public TerminalTheme Theme { get; set; } = new TerminalTheme();

        private bool _isInverse;
        public bool IsInverse { get => _isInverse; set { _isInverse = value; _isStyleDirty = true; } }

        private bool _isBold;
        public bool IsBold { get => _isBold; set { _isBold = value; _isStyleDirty = true; } }

        private bool _isFaint;
        public bool IsFaint { get => _isFaint; set { _isFaint = value; _isStyleDirty = true; } }

        private bool _isItalic;
        public bool IsItalic { get => _isItalic; set { _isItalic = value; _isStyleDirty = true; } }

        private bool _isUnderline;
        public bool IsUnderline { get => _isUnderline; set { _isUnderline = value; _isStyleDirty = true; } }

        private bool _isBlink;
        public bool IsBlink { get => _isBlink; set { _isBlink = value; _isStyleDirty = true; } }

        private bool _isStrikethrough;
        public bool IsStrikethrough { get => _isStrikethrough; set { _isStrikethrough = value; _isStyleDirty = true; } }

        private bool _isHidden;
        public bool IsHidden { get => _isHidden; set { _isHidden = value; _isStyleDirty = true; } }

        // Pending Wrap State (M1.3)
        private bool _isPendingWrap;
        public bool IsPendingWrap
        {
            get => _isPendingWrap;
            set => _isPendingWrap = value;
        }


        // Terminal mode state
        public readonly ModeState Modes = new();

        public bool IsCursorVisible
        {
            get => Modes.IsCursorVisible;
            set => Modes.IsCursorVisible = value;
        }

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

            // Initialize scrollback buffers
            _scrollback = new CircularBuffer<TerminalRow>(MaxHistory);
            _mainScreenScrollback = new CircularBuffer<TerminalRow>(MaxHistory);

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

            // Mouse modes should only change via DEC private mode sequences,
            // not from screen clearing operations (htop clears screen after enabling mouse)

            Invalidate();
        }

        public void ScreenAlignmentPattern()
        {
            Lock.EnterWriteLock();
            try
            {
                // DECALN: Fill screen with 'E'
                var cell = new TerminalCell('E', Theme.Foreground, Theme.Background, false, false, true, true);

                for (int r = 0; r < Rows; r++)
                {
                    // Ensure we have a valid row
                    if (_viewport[r] == null) _viewport[r] = new TerminalRow(Cols, Theme.Foreground, Theme.Background);

                    var row = _viewport[r];
                    for (int c = 0; c < Cols; c++)
                    {
                        row.Cells[c] = cell;
                    }
                    row.TouchRevision();
                }

                // Reset cursor to home
                _cursorCol = 0;
                _cursorRow = 0;
                _isPendingWrap = false; // Reset wrap state
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            Invalidate();
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
                Modes.IsAutoWrapMode = true;
                Modes.IsApplicationCursorKeys = false;

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
                Modes.MouseModeX10 = false;
                Modes.MouseModeButtonEvent = false;
                Modes.MouseModeAnyEvent = false;
                Modes.MouseModeSGR = false;

                SwitchToMainScreen();
                // _tabs.Clear(); // tabs not implemented yet
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            Invalidate();
        }

        public void UpdateThemeColors(TerminalTheme oldTheme)
        {
            Lock.EnterWriteLock();
            try
            {
                // Sync current buffer defaults to new theme
                if (IsDefaultForeground) CurrentForeground = Theme.Foreground;
                if (IsDefaultBackground) CurrentBackground = Theme.Background;

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
                    _viewport[r].TouchRevision();
                }

                foreach (var row in _scrollback)
                {
                    for (int c = 0; c < row.Cells.Length; c++)
                    {
                        UpdateCell(ref row.Cells[c]);
                    }
                    row.TouchRevision();
                }

                foreach (var row in _mainScreen) row.TouchRevision();
                foreach (var row in _altScreen) row.TouchRevision();
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            Invalidate();
        }

        // Synchronized Output State (?2026)
        private bool _isSynchronizedOutput = false;
        private DateTime _lastSyncStart;
        private readonly TimeSpan _maxSyncDuration = TimeSpan.FromMilliseconds(200);

        public bool IsSynchronizedOutput => _isSynchronizedOutput;

        public void BeginSync()
        {
            Lock.EnterWriteLock();
            try
            {
                _isSynchronizedOutput = true;
                _lastSyncStart = DateTime.UtcNow;
            }
            finally { Lock.ExitWriteLock(); }
        }

        public void EndSync()
        {
            Lock.EnterWriteLock();
            try
            {
                if (!_isSynchronizedOutput) return;
                _isSynchronizedOutput = false;
            }
            finally { Lock.ExitWriteLock(); }

            // Trigger deferred invalidation immediately
            OnInvalidate?.Invoke();
        }

        public void Invalidate()
        {
            // If synchronized, check for timeout safety
            if (_isSynchronizedOutput)
            {
                if ((DateTime.UtcNow - _lastSyncStart) > _maxSyncDuration)
                {
                    // Timeout exceeded, force flush
                    EndSync();
                }
                else
                {
                    // Defer invalidation
                    return;
                }
            }

            OnInvalidate?.Invoke();
        }

        /// <summary>
        /// Checks if any mouse reporting mode is active.
        /// </summary>
        public bool IsMouseReportingActive()
        {
            return Modes.MouseModeX10 || Modes.MouseModeButtonEvent || Modes.MouseModeAnyEvent;
        }





        public void WriteChar(char c)
        {
            Lock.EnterWriteLock();
            try
            {
                // Only treat as control if it's not a grapheme component (ZWJ, Variation Selectors)
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
            finally
            {
                Lock.ExitWriteLock();
            }
            Invalidate();
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
            else if (c == '\n')
            {
                if (_cursorRow >= 0 && _cursorRow < Rows) _viewport[_cursorRow].IsWrapped = false;
                _cursorCol = 0;
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
                for (int i = 0; i < spaces; i++) WriteContent(" ", false);
            }
        }

        internal void WriteContent(string text, bool ignored = false)
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

            Rune firstRune = grapheme.EnumerateRunes().First();
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
                }

                ushort flags = _packedFlags;
                if (width >= 2) flags |= (ushort)TerminalCellFlags.Wide;
                if (grapheme.Length > 1 || (grapheme.Length == 1 && char.IsSurrogate(grapheme[0]))) flags |= (ushort)TerminalCellFlags.HasExtendedText;

                row.Cells[_cursorCol] = new TerminalCell(grapheme[0], _packedFg, _packedBg, flags);
                if ((flags & (ushort)TerminalCellFlags.HasExtendedText) != 0)
                {
                    row.SetExtendedText(_cursorCol, grapheme);
                }

                if (width >= 2 && _cursorCol + 1 < Cols)
                {
                    int maxCont = Math.Min(width, Cols - _cursorCol);
                    for (int i = 1; i < maxCont; i++)
                    {
                        row.Cells[_cursorCol + i] = new TerminalCell(' ', _packedFg, _packedBg, (ushort)(_packedFlags | (ushort)TerminalCellFlags.WideContinuation));
                        row.SetExtendedText(_cursorCol + i, null);
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
                // CircularBuffer automatically evicts oldest when at capacity
                bool wasAtCapacity = _scrollback.Count >= MaxHistory;
                _scrollback.Add(_viewport[0]);

                // If we evicted a row, all following rows shifted down in absolute index
                // So we must decrement CellY for ALL images.
                if (wasAtCapacity)
                {
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

            for (int i = ScrollTop; i <= ScrollBottom; i++) _viewport[i].TouchRevision();
        }

        public void InsertCharacters(int count)
        {
            Lock.EnterWriteLock();
            try
            {
                InsertCharactersInternal(count);
            }
            finally
            {
                Lock.ExitWriteLock();
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
            row.TouchRevision();
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

                        int initialSbCount = _scrollback.Count;
                        _scrollback.Clear();
                        _viewport = new TerminalRow[newRows];

                        int total = all.Count;
                        int vpStart;
                        if (newRows < oldRows)
                        {
                            // Shrink height: push top of viewport to scrollback
                            vpStart = initialSbCount + (oldRows - newRows);
                        }
                        else
                        {
                            // Grow height: anchor to top of current viewport (add padding at bottom)
                            vpStart = initialSbCount;
                        }
                        vpStart = Math.Max(0, Math.Min(vpStart, total));

                        for (int i = 0; i < vpStart; i++) _scrollback.Add(all[i]);
                        for (int i = 0; i < newRows; i++)
                        {
                            if (vpStart + i < total) _viewport[i] = all[vpStart + i];
                            else _viewport[i] = new TerminalRow(newCols, Theme.Foreground, Theme.Background);
                        }

                        // Clamping and relative adjustment
                        if (newRows < oldRows)
                        {
                            // If we shrank, the cursor moves up relative to the viewport top
                            _cursorRow -= (oldRows - newRows);
                        }
                        // If we grew, _cursorRow stays same (anchored to top)

                        _cursorRow = Math.Clamp(_cursorRow, 0, newRows - 1);
                        _cursorCol = Math.Clamp(_cursorCol, 0, newCols);
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

                    // 2. Resize Main Screen (Background)
                    // We temporarily swap current viewport to MainScreen to let Resize process it.
                    var activeAlt = _viewport;
                    _viewport = _mainScreen;
                    try
                    {
                        if (oldCols == newCols && oldRows != newRows)
                        {
                            Reshape(newRows);
                        }
                        else
                        {
                            Reflow(oldCols, oldRows, newCols, newRows);
                        }
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
                    if (oldCols == newCols && oldRows != newRows)
                    {
                        Reshape(newRows);
                    }
                    else
                    {
                        Reflow(oldCols, oldRows, newCols, newRows);
                    }

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

        /// <summary>
        /// Optimized vertical resize (height change only).
        /// Re-wrapping text (Reflow) is destructive and unnecessary when width is constant.
        /// Unconditionally Reflowing can cause layout corruption in TUIs (like Midnight Commander).
        /// </summary>
        private void Reshape(int newRows)
        {
            int oldRows = _viewport.Length;
            if (newRows == oldRows) return;

            var newViewport = new TerminalRow[newRows];

            if (newRows > oldRows)
            {
                // GROW: Pull lines from scrollback logic
                // We need (newRows - oldRows) extra lines at the top.
                int linesNeeded = newRows - oldRows;
                int linesAvailable = _scrollback.Count;
                int linesToPull = Math.Min(linesNeeded, linesAvailable);

                // 1. Pull lines from Scrollback
                for (int i = 0; i < linesToPull; i++)
                {
                    // Get 'linesToPull' lines from the END of scrollback
                    // Order: If we pull 2 lines (idx 98, 99).
                    // NewViewport[0] = 98. NewViewport[1] = 99.
                    newViewport[i] = _scrollback[_scrollback.Count - linesToPull + i];
                }

                // 2. Remove pulled lines from scrollback
                // CircularBuffer doesn't support RemoveRange from the end
                // We need to rebuild the scrollback without the last N lines
                if (linesToPull > 0)
                {
                    var tempScrollback = new CircularBuffer<TerminalRow>(MaxHistory);
                    int keepCount = _scrollback.Count - linesToPull;
                    for (int i = 0; i < keepCount; i++)
                    {
                        tempScrollback.Add(_scrollback[i]);
                    }
                    _scrollback = tempScrollback;
                }

                // 3. Copy OLD Viewport
                for (int i = 0; i < oldRows; i++)
                {
                    newViewport[linesToPull + i] = _viewport[i];
                }

                // 4. Fill remaining empty space at bottom
                // (If we didn't have enough history to fill the top)
                int filledRows = linesToPull + oldRows;
                for (int i = filledRows; i < newRows; i++)
                {
                    newViewport[i] = new TerminalRow(Cols, Theme.Foreground, Theme.Background);
                }

                // Adjust cursor: Content moved DOWN by 'linesToPull'
                _cursorRow += linesToPull;
            }
            else
            {
                // SHRINK: Push lines to scrollback logic
                int linesToPush = oldRows - newRows;

                // 1. Push top lines to scrollback
                for (int i = 0; i < linesToPush; i++)
                {
                    _scrollback.Add(_viewport[i]);
                }

                // 2. Copy remaining lines to new viewport
                for (int i = 0; i < newRows; i++)
                {
                    newViewport[i] = _viewport[linesToPush + i];
                }

                // Adjust cursor: Content moved UP by 'linesToPush'
                _cursorRow -= linesToPush;
            }

            _viewport = newViewport;
        }

        private void Reflow(int oldCols, int oldRows, int newCols, int newRows)
        {
            try
            {
                if (newCols <= 0 || newRows <= 0) return;

                // 1. Capture Cursor Content Pre-Resize
                int absCursorPhysicalIdx = _scrollback.Count + _cursorRow;
                int cursorLogicalIdx = -1;
                int cursorInLogicalOffset = -1;

                int absMainSavedIdx = _scrollback.Count + _savedCursors.Main.Row;
                int mainSavedLogicalIdx = -1;
                int mainSavedInLogicalOffset = -1;

                int absAltSavedIdx = _scrollback.Count + _savedCursors.Alt.Row;
                int altSavedLogicalIdx = -1;
                int altSavedInLogicalOffset = -1;

                // 2. Physical Extraction with Padding Trim

                // Calculate how many viewport rows have content
                int lastActiveVpRow = -1;
                int actualVpLen = _viewport.Length;
                for (int i = 0; i < Math.Min(oldRows, actualVpLen); i++)
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
                    if (!isEmpty || i <= _cursorRow || i == _savedCursors.Main.Row || i == _savedCursors.Alt.Row) lastActiveVpRow = i;
                }

                int vpRowsToTake = lastActiveVpRow + 1;
                int totalPhysRows = _scrollback.Count + vpRowsToTake;

                // Rent array to avoid LOH/large allocation
                var allPhysicalRows = System.Buffers.ArrayPool<TerminalRow>.Shared.Rent(totalPhysRows);

                // 3. Metadata-Aware Logical Reconstruction
                var logicalLines = new List<(List<(TerminalCell Cell, string? ExtendedText)> Cells, bool IsWrapped, int StartPhysIdx)>(totalPhysRows);

                try
                {
                    // Fill rented array
                    for (int i = 0; i < _scrollback.Count; i++) allPhysicalRows[i] = _scrollback[i];
                    for (int i = 0; i < vpRowsToTake; i++)
                    {
                        if (i < actualVpLen) allPhysicalRows[_scrollback.Count + i] = _viewport[i];
                        else allPhysicalRows[_scrollback.Count + i] = new TerminalRow(oldCols, Theme.Foreground, Theme.Background);
                    }

                    List<(TerminalCell Cell, string? ExtendedText)>? currentLogical = null;
                    int currentStartPhys = -1;

                    // Iterate using totalPhysRows count
                    for (int i = 0; i < totalPhysRows; i++)
                    {
                        var physRow = allPhysicalRows[i];

                        if (currentLogical == null)
                        {
                            currentLogical = new List<(TerminalCell Cell, string? ExtendedText)>();
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
                            mainSavedInLogicalOffset = currentLogical.Count + _savedCursors.Main.Col;
                        }

                        if (i == absAltSavedIdx)
                        {
                            altSavedLogicalIdx = logicalLines.Count;
                            altSavedInLogicalOffset = currentLogical.Count + _savedCursors.Alt.Col;
                        }

                        int validLen = physRow.Cells.Length;

                        // HEURISTIC: TUI Border Protection
                        // If a line is marked as Wrapped, but it ends with a box-drawing character OR a colored background,
                        // it is likely a fixed-width TUI element that should NOT flow into the next line on resize.
                        bool ignoreWrap = false;
                        if (physRow.IsWrapped)
                        {
                            // 1. Check for TUI Background Fill (at the very edge)
                            // TUI apps often fill the background with a specific color (e.g. blue for MC).
                            // If the last cell has a non-default background AND is a SPACE, it likely hit the edge of a panel.
                            // We must NOT trigger this for regular text (e.g. "Hint" with black BG), or it won't reflow.
                            if (physRow.Cells.Length > 0)
                            {
                                // Scan backwards for the last actual content (skipping newly allocated nulls from resize)
                                TerminalCell lastCell = default;
                                bool found = false;
                                for (int k = physRow.Cells.Length - 1; k >= 0; k--)
                                {
                                    if (physRow.Cells[k].Character != '\0')
                                    {
                                        lastCell = physRow.Cells[k];
                                        found = true;
                                        break;
                                    }
                                }

                                if (found && lastCell.Character == ' ' && !lastCell.IsDefaultBackground)
                                {
                                    ignoreWrap = true;
                                }
                                else
                                {
                                    // 2. Check for Border Characters (ignoring trailing spaces)
                                    for (int k = physRow.Cells.Length - 1; k >= 0; k--)
                                    {
                                        var c = physRow.Cells[k];
                                        char ch = c.Character;
                                        if (ch != ' ' && ch != '\0')
                                        {
                                            // Vertical bars, corners, etc.
                                            // U+2500 to U+257F are Box Drawing. 
                                            // U+2580 to U+259F are Block Elements (Full Block, Shades, etc.) used for scrollbars/shadows.
                                            // U+FF00 to U+FFEF are Halfwidth and Fullwidth Forms (includes Fullwidth Pipe U+FF5C).
                                            // '|' is standard vertical bar (U+007C).
                                            // '+' and '-' can be ASCII borders.
                                            // '>' is often used by MC to indicate horizontal scroll overflow.
                                            if (ch == '|' || ch == '+' || ch == '-' || ch == '>' ||
                                               (ch >= '\u2500' && ch <= '\u257F') ||
                                               (ch >= '\u2580' && ch <= '\u259F') ||
                                               (ch >= '\uFF00' && ch <= '\uFFEF'))
                                            {
                                                ignoreWrap = true;
                                            }
                                            // Special Case: MC Headers like ".[^]" often end with ']' or '^'.
                                            // These are text characters, so we can't protect them globally (would break text wrapping).
                                            // However, in TUI headers, they typically have a specific background color.
                                            // Also protect Arrows '↑' (U+2191) and '↓' (U+2193) which are sometimes used as sort indicators.
                                            else if ((ch == ']' || ch == '[' || ch == '^' || ch == '\u2191' || ch == '\u2193') && !c.IsDefaultBackground)
                                            {
                                                ignoreWrap = true;
                                            }
                                            break;
                                        }
                                    }
                                }
                            }
                        }

                        if (!physRow.IsWrapped || ignoreWrap)
                        {
                            // Smart trimming: Calculate last relevant content index
                            // Include cells that are non-space OR have non-default background
                            int lastContentIdx = -1;
                            for (int scan = 0; scan < physRow.Cells.Length; scan++)
                            {
                                var cell = physRow.Cells[scan];
                                if ((cell.Character != ' ' && cell.Character != '\0') || !cell.IsDefaultBackground || cell.HasExtendedText)
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
                                    currentLogical.Add((physRow.Cells[k], physRow.GetExtendedText(k)));
                                }

                                // Extract Right Part
                                var rightCells = new List<(TerminalCell Cell, string? ExtendedText)>();
                                for (int k = rightStart; k <= rightEnd; k++)
                                {
                                    rightCells.Add((physRow.Cells[k], physRow.GetExtendedText(k)));
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
                                        currentLogical.Add((spaceFill, null));
                                    }
                                    // Add right content
                                    currentLogical.AddRange(rightCells);
                                }
                                else
                                {
                                    // Truncate/Squish
                                    var spaceFill = new TerminalCell(' ', Theme.Foreground, Theme.Background, false, false, true, true);
                                    currentLogical.Add((spaceFill, null));
                                    currentLogical.Add((spaceFill, null));

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
                            for (int k = 0; k < validLen; k++)
                                currentLogical.Add((physRow.Cells[k], physRow.GetExtendedText(k)));
                        }

                        if (!physRow.IsWrapped || ignoreWrap)
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
                    if (img == null) continue;

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
                            if (take < remaining && take > 0 && lineCells[processed + take - 1].Cell.IsWide)
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
                            for (int c = 0; c < take; c++)
                            {
                                var entry = lineCells[processed + c];
                                row.Cells[c] = entry.Cell;
                                row.SetExtendedText(c, entry.ExtendedText);
                            }

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

                int initialScrollbackCount = _scrollback.Count;
                for (int i = 0; i < sbCount; i++) _scrollback.Add(allFlowedRows[i]);

                // CircularBuffer auto-evicts when at capacity
                // Calculate how many rows were discarded due to auto-eviction
                int discardedRows = Math.Max(0, (initialScrollbackCount + sbCount) - _scrollback.Count);
                sbCount -= discardedRows;

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
                    if (img == null) continue;

                    // Find anchor
                    var anchor = imageAnchors.FirstOrDefault(a => a.Image == img);
                    if (anchor.Image != null && anchor.LogicalLineIdx >= 0 && anchor.LogicalLineIdx < newStartFlowIndices.Length)
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
                    if (newMainSavedPhysRow < sbCount) _savedCursors.Main.Row = 0;
                    else _savedCursors.Main.Row = newMainSavedPhysRow - sbCount;
                    _savedCursors.Main.Col = Math.Clamp(newMainSavedPhysCol, 0, newCols);
                }
                if (newAltSavedPhysRow != -1)
                {
                    if (newAltSavedPhysRow < sbCount) _savedCursors.Alt.Row = 0;
                    else _savedCursors.Alt.Row = newAltSavedPhysRow - sbCount;
                    _savedCursors.Alt.Col = Math.Clamp(newAltSavedPhysCol, 0, newCols);
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

                // Explicitly check for cursor column overflow (Reflow safety)
                _cursorCol = Math.Clamp(_cursorCol, 0, newCols);
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText("error.log", "\n--- Reflow Exception at " + DateTime.Now + " ---\n" + ex.ToString() + "\n"); } catch { }
                // Failsafe: if reflow crashes, we just reset the buffer to a clean state to avoid permanent hang
                _scrollback.Clear();
                _viewport = new TerminalRow[newRows];
                for (int i = 0; i < newRows; i++) _viewport[i] = new TerminalRow(newCols, Theme.Foreground, Theme.Background);
                _cursorRow = 0;
                _cursorCol = 0;
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
            Lock.EnterWriteLock();
            try
            {
                if (_cursorRow < 0 || _cursorRow >= Rows) return;
                var row = _viewport[_cursorRow];
                for (int i = _cursorCol; i < Cols; i++)
                {
                    row.Cells[i] = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground);
                    row.SetExtendedText(i, null);
                }
                row.TouchRevision();
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
                    if (i >= Cols) break;
                    row.Cells[i] = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground);
                    row.SetExtendedText(i, null);
                }
                row.TouchRevision();
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
                ClearRowInternal(_cursorRow);
            }
            finally { Lock.ExitWriteLock(); }
            OnInvalidate?.Invoke();
        }

        public void EraseLineAll(int rowIndex)
        {
            Lock.EnterWriteLock();
            try
            {
                if (rowIndex < 0 || rowIndex >= Rows) return;
                ClearRowInternal(rowIndex);
            }
            finally { Lock.ExitWriteLock(); }
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
            row.TouchRevision();
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
                    row.SetExtendedText(col, null);
                }
                row.TouchRevision();
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
                _isPendingWrap = false;
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
                    _viewport[i].TouchRevision();
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

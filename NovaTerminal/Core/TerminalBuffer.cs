
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("NovaTerminal.Tests")]

namespace NovaTerminal.Core
{
    public partial class TerminalBuffer
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
                bool lockTaken = EnterReadLockIfNeeded();
                try { return _cursorCol; }
                finally { ExitReadLockIfNeeded(Lock, lockTaken); }
            }
            set
            {
                bool lockTaken = EnterWriteLockIfNeeded();
                try
                {
                    _cursorCol = value;
                    _isPendingWrap = false;
                }
                finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
            }
        }

        public int CursorRow
        {
            get
            {
                bool lockTaken = EnterReadLockIfNeeded();
                try { return _cursorRow; }
                finally { ExitReadLockIfNeeded(Lock, lockTaken); }
            }
            set
            {
                bool lockTaken = EnterWriteLockIfNeeded();
                try
                {
                    _cursorRow = value;
                    _isPendingWrap = false;
                }
                finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
            }
        } // Row within viewport (0 to Rows-1)

        // Internal access for rendering to avoid pseudo-recursion
        internal int InternalCursorCol => _cursorCol;
        internal int InternalCursorRow => _cursorRow;
        internal int GetVisualCursorRowInternal(int scrollOffset) => _cursorRow + scrollOffset;

        // Internal access for properties to avoid recursive locking
        internal int InternalTotalLines => _isAltScreen ? Rows : (_scrollback.Count + Rows);

        public IReadOnlyList<TerminalRow> ScrollbackRows => _scrollback;
        public IReadOnlyList<TerminalRow> ViewportRows => _viewport;
        public int TotalLines => _isAltScreen ? Rows : (_scrollback.Count + Rows);

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

    }
}

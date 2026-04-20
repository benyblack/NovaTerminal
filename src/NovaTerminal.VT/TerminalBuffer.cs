
using System;
using System.Collections.Generic;
using NovaTerminal.Core.Storage;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("NovaTerminal.Tests")]
[assembly: InternalsVisibleTo("NovaTerminal.Replay")]

namespace NovaTerminal.Core
{
    public partial class TerminalBuffer
    {
        private readonly SavedCursorStates _savedCursors = new();
        private readonly SavedCursorStates _screenCursorStates = new();
        private bool _restoreMainCursorOnAltExit;
        private bool[] _tabStops;
        // Active viewport - what ConPTY writes to (fixed size)
        private TerminalRow[] _viewport;

        // Alternate screen buffer support (for vim, htop, less, etc.)
        private TerminalRow[] _mainScreen;
        private TerminalRow[] _altScreen;
        private bool _isAltScreen = false;
        public bool IsAltScreenActive => _isAltScreen;

        // Scrollback buffer - historical lines that scrolled off the top
        private ScrollbackPages _scrollback;
        private static readonly TerminalPagePool _sharedPagePool = new();
        private int _maxHistory = 10000;
        private long _maxScrollbackBytes;

        public int MaxHistory
        {
            get => _maxHistory;
            set
            {
                _maxHistory = Math.Max(1, value);
                RecomputeScrollbackBudgetForCurrentWidth();
            }
        }

        public long MaxScrollbackBytes
        {
            get => _maxScrollbackBytes;
            set
            {
                _maxScrollbackBytes = Math.Max(ComputeScrollbackBudgetBytes(Cols, 1), value);
                if (_scrollback != null)
                {
                    _scrollback.MaxScrollbackBytes = _maxScrollbackBytes;
                    _scrollback.TryEvictUntilWithinBudget();
                }
            }
        }

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
        public int InternalCursorCol => _cursorCol;
        public int InternalCursorRow => _cursorRow;
        internal int GetVisualCursorRowInternal(int scrollOffset) => _cursorRow + scrollOffset;

        // Internal access for properties to avoid recursive locking
        public int InternalTotalLines => _isAltScreen ? Rows : (_scrollback.Count + Rows);

        public ScrollbackPages Scrollback => _scrollback;
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
            _tabStops = CreateDefaultTabStops(cols);
            ScrollBottom = rows - 1;  // Initialize scrolling region to full screen
            _maxScrollbackBytes = ComputeScrollbackBudgetBytes(cols, _maxHistory);

            CurrentForeground = Theme.Foreground;
            CurrentBackground = Theme.Background;
            IsDefaultForeground = true;
            IsDefaultBackground = true;

            // Initialize scrollback buffer
            _scrollback = new ScrollbackPages(cols, _sharedPagePool, MaxScrollbackBytes);

            _viewport = new TerminalRow[rows];
            for (int i = 0; i < rows; i++)
            {
                _viewport[i] = new TerminalRow(cols, Theme.Foreground, Theme.Background);
            }

            _sharedPagePool.Preheat(TerminalPageConstants.PreheatPagesPerInstance, cols);

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

            _savedCursors.Main.Row = _cursorRow;
            _savedCursors.Main.Col = _cursorCol;
            _savedCursors.Main.Foreground = CurrentForeground;
            _savedCursors.Main.Background = CurrentBackground;
            _savedCursors.Main.IsDefaultForeground = IsDefaultForeground;
            _savedCursors.Main.IsDefaultBackground = IsDefaultBackground;
            _savedCursors.Alt.Row = _savedCursors.Main.Row;
            _savedCursors.Alt.Col = _savedCursors.Main.Col;
            _savedCursors.Alt.Foreground = _savedCursors.Main.Foreground;
            _savedCursors.Alt.Background = _savedCursors.Main.Background;
            _savedCursors.Alt.IsDefaultForeground = _savedCursors.Main.IsDefaultForeground;
            _savedCursors.Alt.IsDefaultBackground = _savedCursors.Main.IsDefaultBackground;

            _screenCursorStates.Main.Row = _savedCursors.Main.Row;
            _screenCursorStates.Main.Col = _savedCursors.Main.Col;
            _screenCursorStates.Main.Foreground = _savedCursors.Main.Foreground;
            _screenCursorStates.Main.Background = _savedCursors.Main.Background;
            _screenCursorStates.Main.IsDefaultForeground = _savedCursors.Main.IsDefaultForeground;
            _screenCursorStates.Main.IsDefaultBackground = _savedCursors.Main.IsDefaultBackground;

            _screenCursorStates.Alt.Row = _savedCursors.Main.Row;
            _screenCursorStates.Alt.Col = _savedCursors.Main.Col;
            _screenCursorStates.Alt.Foreground = _savedCursors.Main.Foreground;
            _screenCursorStates.Alt.Background = _savedCursors.Main.Background;
            _screenCursorStates.Alt.IsDefaultForeground = _savedCursors.Main.IsDefaultForeground;
            _screenCursorStates.Alt.IsDefaultBackground = _savedCursors.Main.IsDefaultBackground;
        }

        private static long ComputeScrollbackBudgetBytes(int cols, int maxHistory)
        {
            return Math.Max(1, cols) * (long)Math.Max(1, maxHistory) * TerminalPageConstants.CellBytes;
        }

        private void RecomputeScrollbackBudgetForCurrentWidth()
        {
            _maxScrollbackBytes = ComputeScrollbackBudgetBytes(Cols, _maxHistory);
            if (_scrollback != null)
            {
                _scrollback.MaxScrollbackBytes = _maxScrollbackBytes;
                _scrollback.TryEvictUntilWithinBudget();
            }
        }

    }
}


using System;
using System.Collections.Generic;
using NovaTerminal.VT.Storage;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("NovaTerminal.App.Tests")]
[assembly: InternalsVisibleTo("NovaTerminal.Replay")]

namespace NovaTerminal.VT
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

        // ── Reflow-tracked shell-integration marks ───────────────────────────────
        //
        // Why the buffer holds these rather than the consumer that captured them: a
        // reflowing (width-changing) resize rebuilds the whole absolute-row coordinate
        // space, and the *only* place that knows where a given cell moved to is the
        // reflow itself. A holder outside the buffer can do nothing but notice that its
        // Generation went stale and throw the mark away — which is exactly the bug this
        // slot exists to fix: PSReadLine repaints the input line on a resize but does not
        // re-run the prompt function, so no fresh OSC 133;B ever arrives and the mark
        // stays dead for the rest of that command line.
        //
        // Registering the mark here lets TerminalBuffer.ReflowEngine re-anchor it the same
        // way it already re-anchors the cursor, the saved cursors and inline images: by
        // logical-line index plus offset-in-logical-line. The mark comes out of the reflow
        // with fresh Row/Column/AbsoluteRow *and* the fresh Generation, so the generation
        // contract is untouched — every reader still refuses a mark whose epoch does not
        // match, and there simply is no stale epoch to refuse any more.
        //
        // A mark the reflow cannot place (its line was trimmed away, the offset landed
        // past the reflowed line, its row aged out) is cleared rather than guessed at,
        // which lands the consumer back on today's behaviour: "no mark", never a wrong one.
        private readonly object _trackedMarkGate = new();
        private ShellIntegrationMark? _commandStartMark;
        private ShellIntegrationMark? _commandOutputStartMark;
        private bool _isAcceptingCommandInput;

        /// <summary>
        /// The newest <c>OSC 133;B</c> mark (prompt end / start of the user's input), or
        /// <see langword="null"/>. Re-anchored across a reflowing resize; see the field
        /// comments above. Independently locked, so a reader never sees a torn record and
        /// never has to hold <see cref="Lock"/> just to read it.
        /// </summary>
        public ShellIntegrationMark? CommandStartMark
        {
            get { lock (_trackedMarkGate) { return _commandStartMark; } }
            set { lock (_trackedMarkGate) { _commandStartMark = value; } }
        }

        /// <summary>
        /// Where the running command's output region begins (captured at <c>OSC 133;C</c>),
        /// or <see langword="null"/>. Tracked for the same reason and by the same machinery
        /// as <see cref="CommandStartMark"/>: a resize between <c>C</c> and <c>D</c> would
        /// otherwise cost the failing command's captured output.
        /// </summary>
        public ShellIntegrationMark? CommandOutputStartMark
        {
            get { lock (_trackedMarkGate) { return _commandOutputStartMark; } }
            set { lock (_trackedMarkGate) { _commandOutputStartMark = value; } }
        }

        /// <summary>
        /// Whether the shell is sitting in its line editor waiting for the user: opened by
        /// <c>OSC 133;B</c>, closed by <c>OSC 133;C</c>, <c>OSC 133;D</c>, an alt-screen switch, or
        /// a session replacement.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the other half of <see cref="CommandStartMark"/>, and it lives here for the same
        /// reason. Whether the cells between the mark and the cursor are a command line the user is
        /// editing or the output of a command that already ran is a fact about the shell's
        /// lifecycle, carried only by the OSC 133 stream - so both halves are facts about this
        /// session's byte stream, and a consumer that reads one without the other gets an answer it
        /// cannot trust.
        /// </para>
        /// <para>
        /// It used to live on Command Assist's <c>AssistSessionContext</c>, written after a hop onto
        /// the pane's serialized event dispatcher while the mark was written synchronously by the
        /// parser. Under a busy dispatcher the two disagreed - a fresh mark, a stale-closed gate -
        /// and a submitted command was dropped from history outright, silently and permanently
        /// (#448). Here the parser applies both in the same statement block, so no queue can reorder
        /// them and no consumer can be handed half of the answer.
        /// </para>
        /// <para>
        /// Not a coordinate, so unlike the marks it needs no reflow re-anchoring: a resize that
        /// cannot re-place the mark leaves this alone, and the consumer's "no mark" answer is what
        /// refuses the read. Guarded by <see cref="_trackedMarkGate"/> rather than
        /// <see cref="Lock"/> for the same reason the marks are - the UI thread reads it on every
        /// Enter and must not queue behind a parse.
        /// </para>
        /// </remarks>
        public bool IsAcceptingCommandInput
        {
            get { lock (_trackedMarkGate) { return _isAcceptingCommandInput; } }
        }

        /// <summary><c>OSC 133;B</c>: the prompt finished printing and the line editor is the user's.</summary>
        /// <remarks>
        /// Refused while the alt screen is up, so the invariant "the gate is never open during an
        /// alt screen" holds whichever order the two facts arrive in: a full-screen TUI drawing its
        /// own prompt may legally emit <c>133;B</c>, and that must not open a window onto the TUI's
        /// grid. Re-opening an already-open gate is idempotent by design - prompt frameworks repaint
        /// constantly and every repaint carries the mark.
        /// </remarks>
        public void OpenCommandInputWindow()
        {
            lock (_trackedMarkGate)
            {
                if (_isAltScreen)
                {
                    return;
                }

                _isAcceptingCommandInput = true;
            }
        }

        /// <summary><c>OSC 133;C</c> / <c>OSC 133;D</c>: the line editor is closed.</summary>
        /// <remarks>
        /// <c>D</c> closes it as well as <c>C</c> because a shell can reach <c>D</c> with no
        /// intervening <c>C</c>, and leaving the gate open for a command's whole run is exactly the
        /// failure it exists to prevent.
        /// </remarks>
        public void CloseCommandInputWindow()
        {
            lock (_trackedMarkGate)
            {
                _isAcceptingCommandInput = false;
            }
        }

        /// <summary>Drops both tracked marks and closes the command-input window. Used when a
        /// session is replaced.</summary>
        public void ClearTrackedShellMarks()
        {
            lock (_trackedMarkGate)
            {
                _commandStartMark = null;
                _commandOutputStartMark = null;
                _isAcceptingCommandInput = false;
            }
        }

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

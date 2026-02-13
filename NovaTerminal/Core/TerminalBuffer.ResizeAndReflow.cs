using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NovaTerminal.Core
{
    public partial class TerminalBuffer
    {
        public void Write(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            EnterBatchWrite();
            try
            {
                foreach (char c in text)
                {
                    WriteCharCore(c);
                }
            }
            finally
            {
                ExitBatchWrite();
            }
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
            var context = new ReflowEngineContext(this);
            context.Execute(oldCols, oldRows, newCols, newRows);
            context.Apply(this);
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
    }
}

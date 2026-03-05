using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NovaTerminal.Core.Storage;

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
                        
                        var lastCell = new TerminalCell(' ', Theme.Foreground, Theme.Background, false, false, true, true);
                        if (oldAlt.Length > 0 && oldCols > 0)
                        {
                            var src = oldAlt[oldAlt.Length - 1].Cells[oldCols - 1];
                            lastCell = new TerminalCell(' ', src.Fg, src.Bg, src.Flags);
                        }

                        for (int i = 0; i < newRows; i++)
                        {
                            if (i < oldAlt.Length) 
                            {
                                _viewport[i] = oldAlt[i];
                            }
                            else 
                            {
                                _viewport[i] = new TerminalRow(newCols);
                                for (int cx = 0; cx < newCols; cx++) _viewport[i].Cells[cx] = lastCell;
                            }
                        }
                        _cursorRow = Math.Clamp(_cursorRow, 0, newRows - 1);
                    }
                    else
                    {
                        // Main screen: Still needs redistribution to maintain scrollback vs viewport split
                        
                        if (newRows < oldRows)
                        {
                            // Shrink height: push top of viewport to scrollback
                            int diff = oldRows - newRows;
                            long prevEvicted = _scrollback.TotalRowsEvicted;

                            for (int i = 0; i < diff; i++)
                            {
                                _scrollback.AppendRow(_viewport[i].Cells);
                            }

                            var newVp = new TerminalRow[newRows];
                            Array.Copy(_viewport, diff, newVp, 0, newRows);
                            _viewport = newVp;
                            _cursorRow -= diff;

                            long newlyEvicted = _scrollback.TotalRowsEvicted - prevEvicted;
                            if (newlyEvicted > 0)
                            {
                                for (int i = _images.Count - 1; i >= 0; i--)
                                {
                                    var img = _images[i];
                                    img.CellY -= (int)newlyEvicted;
                                    if (img.CellY + img.CellHeight <= 0) _images.RemoveAt(i);
                                }
                            }
                        }
                        else if (newRows > oldRows)
                        {
                            // Grow height: anchor to top of current viewport (add padding at bottom)
                            var newVp = new TerminalRow[newRows];
                            Array.Copy(_viewport, 0, newVp, 0, oldRows);
                            for (int i = oldRows; i < newRows; i++)
                            {
                                newVp[i] = new TerminalRow(newCols, Theme.Foreground, Theme.Background);
                            }
                            _viewport = newVp;
                        }

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
                    
                    var lastCell = new TerminalCell(' ', Theme.Foreground, Theme.Background, false, false, true, true);
                    if (oldAlt.Length > 0 && oldCols > 0)
                    {
                        var src = oldAlt[oldAlt.Length - 1].Cells[oldCols - 1];
                        lastCell = new TerminalCell(' ', src.Fg, src.Bg, src.Flags);
                    }

                    for (int i = 0; i < newRows; i++)
                    {
                        var rowCell = lastCell;
                        if (i < oldAlt.Length && oldCols > 0)
                        {
                            var src = oldAlt[i].Cells[oldCols - 1];
                            rowCell = new TerminalCell(' ', src.Fg, src.Bg, src.Flags);
                        }

                        _viewport[i] = new TerminalRow(newCols);
                        for (int cx = 0; cx < newCols; cx++) _viewport[i].Cells[cx] = rowCell;

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
                // GROW: Pull lines from scrollback
                int linesNeeded = newRows - oldRows;
                int pulled = 0;

                // We want to fill the TOP of the new viewport with lines from history
                for (int i = linesNeeded - 1; i >= 0; i--)
                {
                    var row = new TerminalRow(Cols, Theme.Foreground, Theme.Background);
                    if (_scrollback.TryPopLastRow(row.Cells))
                    {
                        newViewport[i] = row;
                        pulled++;
                    }
                    else
                    {
                        newViewport[i] = new TerminalRow(Cols, Theme.Foreground, Theme.Background);
                    }
                }

                Array.Copy(_viewport, 0, newViewport, linesNeeded, oldRows);
                _cursorRow += linesNeeded;
                
                // Shift images DOWN
                for (int i = 0; i < _images.Count; i++)
                {
                    _images[i].CellY += linesNeeded;
                }
            }
            else
            {
                // SHRINK: Push lines to scrollback
                int linesToPush = oldRows - newRows;
                long prevEvicted = _scrollback.TotalRowsEvicted;

                for (int i = 0; i < linesToPush; i++)
                {
                    _scrollback.AppendRow(_viewport[i].Cells);
                }

                Array.Copy(_viewport, linesToPush, newViewport, 0, newRows);
                _cursorRow -= linesToPush;

                int newlyDiscarded = (int)(_scrollback.TotalRowsEvicted - prevEvicted);

                // Shift images UP
                for (int i = _images.Count - 1; i >= 0; i--)
                {
                    var img = _images[i];
                    img.CellY -= (linesToPush + newlyDiscarded);
                    if (img.CellY + img.CellHeight <= 0) _images.RemoveAt(i);
                }
            }

            _viewport = newViewport;
            _cursorRow = Math.Clamp(_cursorRow, 0, newRows - 1);
            _cursorCol = Math.Clamp(_cursorCol, 0, Cols);
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

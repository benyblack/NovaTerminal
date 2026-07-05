using NovaTerminal.VT.Storage;

namespace NovaTerminal.VT
{
    public partial class TerminalBuffer
    {
        public void AddImage(TerminalImage image)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                _images.Add(image);
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
            Invalidate();
        }

        public void ClearImages()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
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
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
            Invalidate();
        }

        /// <summary>
        /// Clears both the visible screen and the scrollback history.
        /// Used by RIS (full reset) and explicit user-invoked "clear buffer" actions.
        /// VT erase sequences must NOT call this: ED 2 (CSI 2 J) only clears the
        /// screen (<see cref="ClearScreen"/>) and ED 3 (CSI 3 J) only clears the
        /// scrollback (<see cref="ClearScrollbackHistory"/>).
        /// </summary>
        public void Clear(bool resetCursor = true)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                _scrollback.Clear();
                _images.Clear(); // full clear: scrollback-anchored images lose their anchor too
                ClearScreenInternal(resetCursor);

                // Attribute reset belongs to the full-clear path only (RIS / explicit UI
                // clear). ED 2 must leave SGR state untouched — a TUI that enables
                // bold/inverse and repaints via CSI 2 J keeps its attributes.
                IsInverse = false;
                IsBold = false;
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }

            Invalidate();
        }

        /// <summary>
        /// Erases the visible screen only (ED 2 semantics). Scrollback history is preserved.
        /// </summary>
        public void ClearScreen(bool resetCursor = false)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                ClearScreenInternal(resetCursor);
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }

            Invalidate();
        }

        /// <summary>
        /// Clears the scrollback history only (ED 3 / "Erase Saved Lines" semantics).
        /// The visible screen is left untouched.
        /// </summary>
        public void ClearScrollbackHistory()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                int clearedRows = _scrollback.Count;
                _scrollback.Clear();

                // On the main screen, image CellY is absolute (scrollback + viewport), so
                // removing scrollback rows shifts every anchor down — same convention as the
                // eviction shift in ScrollUpInternal. Images now entirely above row 0 lived
                // in the cleared history and are pruned. Alt-screen coordinates are
                // viewport-relative and unaffected.
                if (clearedRows > 0 && !_isAltScreen)
                {
                    for (int i = _images.Count - 1; i >= 0; i--)
                    {
                        var img = _images[i];
                        img.CellY -= clearedRows;
                        if (img.CellY + img.CellHeight <= 0)
                        {
                            _images.RemoveAt(i);
                        }
                    }
                }
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }

            Invalidate();
        }

        private void ClearScreenInternal(bool resetCursor)
        {
            // Remove images intersecting the visible screen. On the main screen CellY is
            // absolute (scrollback + viewport; see ScrollUpInternal/InsertLines), so images
            // that live entirely in scrollback are preserved — ED 2 must not touch history.
            // An image straddling the scrollback/viewport boundary is removed entirely,
            // since a partial erase isn't representable.
            int viewportTopAbs = _isAltScreen ? 0 : _scrollback.Count;
            for (int i = _images.Count - 1; i >= 0; i--)
            {
                if (_images[i].CellY + _images[i].CellHeight > viewportTopAbs)
                {
                    _images.RemoveAt(i);
                }
            }

            // CRITICAL: Erase cells IN-PLACE rather than replacing row objects.
            // TUI apps like Yazi rely on partial redraws — they only redraw rows that changed.
            // If we replace all row objects (new IDs), Yazi's next frame skips unchanged rows,
            // leaving blank row objects on screen instead of re-drawn content.
            for (int i = 0; i < Rows; i++)
            {
                ClearRowInternal(i);
            }

            if (resetCursor)
            {
                _cursorCol = 0;
                _cursorRow = 0;
            }

            // Mouse modes should only change via DEC private mode sequences,
            // not from screen clearing operations (htop clears screen after enabling mouse)
        }

        public void ScreenAlignmentPattern()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
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
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
            Invalidate();
        }

        public void Reset()
        {
            // Full Reset (RIS)
            Clear(true);
            bool lockTaken = EnterWriteLockIfNeeded();
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

                _restoreMainCursorOnAltExit = false;
                SwitchToMainScreen(restoreSavedCursorIfArmed: false);
                ResetTabStopsToDefaultsNoLock();
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
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
                SyncThemeDefaultsInCursorStateNoLock(_savedCursors.Main);
                SyncThemeDefaultsInCursorStateNoLock(_savedCursors.Alt);
                SyncThemeDefaultsInCursorStateNoLock(_screenCursorStates.Main);
                SyncThemeDefaultsInCursorStateNoLock(_screenCursorStates.Alt);

                void UpdateCell(ref TerminalCell cell)
                {
                    // Preserve palette/default flags across theme switches.
                    // Using Foreground/Background setters here would clear those flags.
                    short fgIdx = cell.FgIndex;
                    short bgIdx = cell.BgIndex;

                    // Indices take precedence
                    if (fgIdx >= 0 && fgIdx <= 15)
                    {
                        cell.IsPaletteForeground = true;
                        cell.IsDefaultForeground = false;
                        cell.Fg = (uint)fgIdx;
                    }
                    else if (cell.IsDefaultForeground)
                    {
                        cell.IsPaletteForeground = false;
                        cell.IsDefaultForeground = true;
                        cell.Fg = Theme.Foreground.ToUint();
                    }

                    if (bgIdx >= 0 && bgIdx <= 15)
                    {
                        cell.IsPaletteBackground = true;
                        cell.IsDefaultBackground = false;
                        cell.Bg = (uint)bgIdx;
                    }
                    else if (cell.IsDefaultBackground)
                    {
                        cell.IsPaletteBackground = false;
                        cell.IsDefaultBackground = true;
                        cell.Bg = Theme.Background.ToUint();
                    }

                    cell.IsDirty = true;
                }

                for (int r = 0; r < Rows; r++)
                {
                    for (int c = 0; c < Cols; c++)
                    {
                        UpdateCell(ref _viewport[r].Cells[c]);
                    }
                    _viewport[r].TouchRevision();
                }

                _scrollback.UpdateThemeDefaults(oldTheme, Theme);

                foreach (var row in _mainScreen) row.TouchRevision();
                foreach (var row in _altScreen) row.TouchRevision();
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            Invalidate();
        }

        /// <summary>
        /// Checks if any mouse reporting mode is active.
        /// </summary>
        public bool IsMouseReportingActive()
        {
            return Modes.MouseModeX10 || Modes.MouseModeButtonEvent || Modes.MouseModeAnyEvent;
        }
    }
}

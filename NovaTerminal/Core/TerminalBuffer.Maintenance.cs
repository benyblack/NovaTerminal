namespace NovaTerminal.Core
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

        public void Clear(bool resetCursor = true)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
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
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }

            // Mouse modes should only change via DEC private mode sequences,
            // not from screen clearing operations (htop clears screen after enabling mouse)

            Invalidate();
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

                SwitchToMainScreen();
                // _tabs.Clear(); // tabs not implemented yet
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

        /// <summary>
        /// Checks if any mouse reporting mode is active.
        /// </summary>
        public bool IsMouseReportingActive()
        {
            return Modes.MouseModeX10 || Modes.MouseModeButtonEvent || Modes.MouseModeAnyEvent;
        }
    }
}

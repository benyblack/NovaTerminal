namespace NovaTerminal.Core
{
    public partial class TerminalBuffer
    {
        private static bool[] CreateDefaultTabStops(int cols)
        {
            var stops = new bool[Math.Max(0, cols)];
            for (int col = 0; col < stops.Length; col++)
            {
                stops[col] = IsDefaultTabStopColumn(col);
            }

            return stops;
        }

        private static bool IsDefaultTabStopColumn(int col)
        {
            return col > 0 && (col % 8) == 0;
        }

        private void ResetTabStopsToDefaultsNoLock()
        {
            _tabStops = CreateDefaultTabStops(Cols);
        }

        private void ResizeTabStopsNoLock(int oldCols, int newCols)
        {
            if (newCols == oldCols)
            {
                return;
            }

            var resized = new bool[Math.Max(0, newCols)];
            int sharedCols = System.Math.Min(oldCols, newCols);
            for (int col = 0; col < sharedCols; col++)
            {
                resized[col] = col < _tabStops.Length && _tabStops[col];
            }

            for (int col = sharedCols; col < resized.Length; col++)
            {
                resized[col] = IsDefaultTabStopColumn(col);
            }

            _tabStops = resized;
        }

        private int FindNextTabStopNoLock(int startExclusive)
        {
            for (int col = System.Math.Max(0, startExclusive + 1); col < _tabStops.Length; col++)
            {
                if (_tabStops[col])
                {
                    return col;
                }
            }

            return Cols > 0 ? Cols - 1 : 0;
        }

        private int FindPreviousTabStopNoLock(int startExclusive)
        {
            for (int col = System.Math.Min(startExclusive - 1, _tabStops.Length - 1); col >= 0; col--)
            {
                if (_tabStops[col])
                {
                    return col;
                }
            }

            return 0;
        }

        public void HorizontalTab(int count = 1)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                int remaining = System.Math.Max(1, count);
                int col = System.Math.Clamp(_cursorCol, 0, System.Math.Max(0, Cols - 1));
                while (remaining-- > 0)
                {
                    col = FindNextTabStopNoLock(col);
                }

                _cursorCol = col;
                _isPendingWrap = false;
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
        }

        public void BackwardTab(int count = 1)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                int remaining = System.Math.Max(1, count);
                int col = System.Math.Clamp(_cursorCol, 0, System.Math.Max(0, Cols - 1));
                while (remaining-- > 0)
                {
                    col = FindPreviousTabStopNoLock(col);
                }

                _cursorCol = col;
                _isPendingWrap = false;
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
        }

        public void SetTabStopAtCursor()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (_cursorCol >= 0 && _cursorCol < _tabStops.Length)
                {
                    _tabStops[_cursorCol] = true;
                }
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
        }

        public void ClearTabStopAtCursor()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (_cursorCol >= 0 && _cursorCol < _tabStops.Length)
                {
                    _tabStops[_cursorCol] = false;
                }
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
        }

        public void ClearAllTabStops()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                System.Array.Clear(_tabStops, 0, _tabStops.Length);
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace NovaTerminal.Core
{
    public partial class TerminalBuffer
    {
        private int _batchWriteDepth;
        private bool _batchInvalidatePending;
        private static readonly TimeSpan _maxSyncDuration = TimeSpan.FromMilliseconds(200);
        private int[] _lastSnapshotAbsRows = Array.Empty<int>();
        private long[] _lastSnapshotRowIds = Array.Empty<long>();
        private uint[] _lastSnapshotRowRevisions = Array.Empty<uint>();
        private int _lastSnapshotCols = -1;
        private bool _hasSnapshotState;

        public bool IsSynchronizedOutput => _isSynchronizedOutput;

        private bool EnterReadLockIfNeeded()
        {
            if (Lock.IsWriteLockHeld || Lock.IsReadLockHeld) return false;
            Lock.EnterReadLock();
            return true;
        }

        private static void ExitReadLockIfNeeded(System.Threading.ReaderWriterLockSlim rwLock, bool lockTaken)
        {
            if (lockTaken) rwLock.ExitReadLock();
        }

        private bool EnterWriteLockIfNeeded()
        {
            if (Lock.IsWriteLockHeld) return false;
            Lock.EnterWriteLock();
            return true;
        }

        private static void ExitWriteLockIfNeeded(System.Threading.ReaderWriterLockSlim rwLock, bool lockTaken)
        {
            if (lockTaken) rwLock.ExitWriteLock();
        }

        public void EnterBatchWrite()
        {
            Lock.EnterWriteLock();
            _batchWriteDepth++;
        }

        public void ExitBatchWrite()
        {
            if (!Lock.IsWriteLockHeld || _batchWriteDepth <= 0)
            {
                throw new InvalidOperationException("ExitBatchWrite called without an active batch write lock.");
            }

            _batchWriteDepth--;
            bool shouldFlushInvalidate = _batchWriteDepth == 0 && _batchInvalidatePending;
            if (_batchWriteDepth == 0)
            {
                _batchInvalidatePending = false;
            }

            Lock.ExitWriteLock();

            if (shouldFlushInvalidate)
            {
                Invalidate();
            }
        }

        public void BeginSync()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                _isSynchronizedOutput = true;
                _lastSyncStart = DateTime.UtcNow;
            }
            finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        }

        public void EndSync()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (!_isSynchronizedOutput) return;
                _isSynchronizedOutput = false;
            }
            finally { ExitWriteLockIfNeeded(Lock, lockTaken); }

            // Trigger deferred invalidation immediately
            OnInvalidate?.Invoke();
        }

        public void FlushSynchronizedOutputTimeout()
        {
            bool shouldInvalidate = false;
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (_isSynchronizedOutput &&
                    (DateTime.UtcNow - _lastSyncStart) > _maxSyncDuration)
                {
                    _isSynchronizedOutput = false;
                    shouldInvalidate = true;
                }
            }
            finally { ExitWriteLockIfNeeded(Lock, lockTaken); }

            if (shouldInvalidate)
            {
                OnInvalidate?.Invoke();
            }
        }

        public void Invalidate()
        {
            // Defer all invalidation while a parser batch holds the write lock.
            if (_batchWriteDepth > 0)
            {
                _batchInvalidatePending = true;
                return;
            }

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

        public TerminalRenderSnapshot CaptureRenderSnapshot(RenderSnapshotRequest req, out long readLockMs)
        {
            int viewportRows = Math.Max(0, req.ViewportRows);
            int viewportCols = Math.Max(0, req.ViewportCols);
            int totalLines = 0;
            int absDisplayStart = 0;
            int cursorRow = 0;
            int cursorCol = 0;
            CursorStyle cursorStyle = CursorStyle.Block;
            RenderThemeSnapshot theme = default;
            RenderRowSnapshot[] rowsData = viewportRows > 0 ? new RenderRowSnapshot[viewportRows] : Array.Empty<RenderRowSnapshot>();
            RenderImageSnapshot[] images = Array.Empty<RenderImageSnapshot>();
            DirtySpan[] dirtySpans = Array.Empty<DirtySpan>();

            var lockSw = Stopwatch.StartNew();
            Lock.EnterReadLock();
            try
            {
                totalLines = InternalTotalLines;
                absDisplayStart = Math.Max(0, totalLines - viewportRows - req.ScrollOffset);
                cursorRow = _cursorRow;
                cursorCol = _cursorCol;
                cursorStyle = Modes.CursorStyle;
                theme = CreateRenderThemeSnapshot_NoLock();

                EnsureSnapshotStateCapacity_NoLock(viewportRows);

                var dirtyTmp = viewportRows > 0 ? new DirtySpan[viewportRows] : Array.Empty<DirtySpan>();
                int dirtyCount = 0;

                for (int r = 0; r < viewportRows; r++)
                {
                    int absRow = absDisplayStart + r;
                    var row = GetRowAbsolute(absRow);
                    RenderRowSnapshot rowSnapshot;
                    long rowId = 0;
                    uint rowRevision = 0;

                    if (row != null)
                    {
                        rowSnapshot = GetRowSnapshot(absRow, viewportCols);
                        rowId = rowSnapshot.RowId;
                        rowRevision = rowSnapshot.Revision;
                    }
                    else
                    {
                        rowSnapshot = new RenderRowSnapshot
                        {
                            AbsRow = absRow,
                            Revision = 0,
                            Cols = 0,
                            Cells = Array.Empty<RenderCellSnapshot>(),
                            RowId = 0
                        };
                    }

                    rowsData[r] = rowSnapshot;

                    bool rowDirty = !_hasSnapshotState ||
                                    _lastSnapshotCols != viewportCols ||
                                    _lastSnapshotAbsRows[r] != absRow ||
                                    _lastSnapshotRowIds[r] != rowId ||
                                    _lastSnapshotRowRevisions[r] != rowRevision;
                    if (rowDirty && viewportCols > 0)
                    {
                        dirtyTmp[dirtyCount++] = new DirtySpan
                        {
                            Row = r,
                            ColStart = 0,
                            ColEnd = viewportCols - 1
                        };
                    }

                    _lastSnapshotAbsRows[r] = absRow;
                    _lastSnapshotRowIds[r] = rowId;
                    _lastSnapshotRowRevisions[r] = rowRevision;
                }

                _lastSnapshotCols = viewportCols;
                _hasSnapshotState = true;

                if (dirtyCount > 0)
                {
                    dirtySpans = new DirtySpan[dirtyCount];
                    Array.Copy(dirtyTmp, dirtySpans, dirtyCount);
                }

                var visibleImages = GetVisibleImagesSnapshot(absDisplayStart, viewportRows);
                images = visibleImages.Count > 0 ? visibleImages.ToArray() : Array.Empty<RenderImageSnapshot>();
            }
            finally
            {
                Lock.ExitReadLock();
                lockSw.Stop();
                readLockMs = lockSw.ElapsedMilliseconds;
            }

            SelectionRowSnapshot[] selectionRows = BuildSelectionRows(req.Selection, absDisplayStart, viewportRows, viewportCols);
            SearchHighlightSnapshot[] searchHighlights = BuildSearchHighlights(req.SearchMatches, req.ActiveSearchIndex, absDisplayStart, viewportRows);

            return new TerminalRenderSnapshot
            {
                ViewportRows = viewportRows,
                ViewportCols = viewportCols,
                TotalLines = totalLines,
                AbsDisplayStart = absDisplayStart,
                ScrollOffset = req.ScrollOffset,
                CursorRow = cursorRow,
                CursorCol = cursorCol,
                CursorStyle = cursorStyle,
                Theme = theme,
                DirtySpans = dirtySpans,
                RowsData = rowsData,
                Images = images,
                SelectionRows = selectionRows,
                SearchHighlights = searchHighlights
            };
        }

        private RenderThemeSnapshot CreateRenderThemeSnapshot_NoLock()
        {
            return new RenderThemeSnapshot
            {
                Foreground = Theme.Foreground,
                Background = Theme.Background,
                CursorColor = Theme.CursorColor,
                AnsiPalette = new[]
                {
                    Theme.Black,
                    Theme.Red,
                    Theme.Green,
                    Theme.Yellow,
                    Theme.Blue,
                    Theme.Magenta,
                    Theme.Cyan,
                    Theme.White,
                    Theme.BrightBlack,
                    Theme.BrightRed,
                    Theme.BrightGreen,
                    Theme.BrightYellow,
                    Theme.BrightBlue,
                    Theme.BrightMagenta,
                    Theme.BrightCyan,
                    Theme.BrightWhite
                }
            };
        }

        private void EnsureSnapshotStateCapacity_NoLock(int viewportRows)
        {
            if (_lastSnapshotAbsRows.Length >= viewportRows)
            {
                return;
            }

            int newLen = Math.Max(viewportRows, Math.Max(64, _lastSnapshotAbsRows.Length == 0 ? 64 : _lastSnapshotAbsRows.Length * 2));
            Array.Resize(ref _lastSnapshotAbsRows, newLen);
            Array.Resize(ref _lastSnapshotRowIds, newLen);
            Array.Resize(ref _lastSnapshotRowRevisions, newLen);
        }

        private static SelectionRowSnapshot[] BuildSelectionRows(SelectionState? selection, int absDisplayStart, int viewportRows, int viewportCols)
        {
            if (selection == null || !selection.IsActive || viewportRows <= 0 || viewportCols <= 0)
            {
                return Array.Empty<SelectionRowSnapshot>();
            }

            var list = new List<SelectionRowSnapshot>();
            int absEnd = absDisplayStart + viewportRows;
            for (int absRow = absDisplayStart; absRow < absEnd; absRow++)
            {
                var (isSelected, colStart, colEnd) = selection.GetSelectionRangeForRow(absRow, viewportCols);
                if (!isSelected)
                {
                    continue;
                }

                int clampedStart = Math.Clamp(colStart, 0, viewportCols - 1);
                int clampedEnd = Math.Clamp(colEnd, 0, viewportCols - 1);
                if (clampedEnd < clampedStart)
                {
                    continue;
                }

                list.Add(new SelectionRowSnapshot
                {
                    AbsRow = absRow,
                    ColStart = clampedStart,
                    ColEnd = clampedEnd
                });
            }

            return list.Count > 0 ? list.ToArray() : Array.Empty<SelectionRowSnapshot>();
        }

        private static SearchHighlightSnapshot[] BuildSearchHighlights(IReadOnlyList<SearchMatch>? matches, int activeSearchIndex, int absDisplayStart, int viewportRows)
        {
            if (matches == null || matches.Count == 0 || viewportRows <= 0)
            {
                return Array.Empty<SearchHighlightSnapshot>();
            }

            int absEnd = absDisplayStart + viewportRows;
            var list = new List<SearchHighlightSnapshot>();
            for (int i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                if (match.AbsRow < absDisplayStart || match.AbsRow >= absEnd)
                {
                    continue;
                }

                list.Add(new SearchHighlightSnapshot
                {
                    AbsRow = match.AbsRow,
                    StartCol = match.StartCol,
                    EndCol = match.EndCol,
                    IsActive = i == activeSearchIndex
                });
            }

            return list.Count > 0 ? list.ToArray() : Array.Empty<SearchHighlightSnapshot>();
        }
    }
}

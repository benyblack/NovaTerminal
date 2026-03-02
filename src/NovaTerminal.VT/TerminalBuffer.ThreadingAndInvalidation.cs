using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace NovaTerminal.Core
{
    public partial class TerminalBuffer
    {
        private int _batchWriteDepth;
        private bool _batchInvalidatePending;
        private long _cursorSuppressedUntilUtcTicks;
        private static readonly TimeSpan _maxSyncDuration = TimeSpan.FromMilliseconds(200);
        private int[] _lastSnapshotAbsRows = Array.Empty<int>();
        private long[] _lastSnapshotRowIds = Array.Empty<long>();
        private uint[] _lastSnapshotRowRevisions = Array.Empty<uint>();
        private RenderCellSnapshot[][] _lastSnapshotRowCells = Array.Empty<RenderCellSnapshot[]>();
        private long[] _cachedRenderRowIds = Array.Empty<long>();
        private uint[] _cachedRenderRowRevisions = Array.Empty<uint>();
        private int[] _cachedRenderRowCols = Array.Empty<int>();
        private RenderCellSnapshot[][] _cachedRenderRowCells = Array.Empty<RenderCellSnapshot[]>();
        private int _lastSnapshotCols = -1;
        private bool _hasSnapshotState;

        public bool IsSynchronizedOutput => _isSynchronizedOutput;
        public long CursorSuppressedUntilUtcTicks => _cursorSuppressedUntilUtcTicks;

        internal void ExtendCursorSuppression_NoLock(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
            {
                return;
            }

            long suppressUntilTicks = DateTime.UtcNow.Add(duration).Ticks;
            if (suppressUntilTicks > _cursorSuppressedUntilUtcTicks)
            {
                _cursorSuppressedUntilUtcTicks = suppressUntilTicks;
            }
        }

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

                var dirtyList = new List<DirtySpan>(Math.Max(8, viewportRows * 2));

                for (int r = 0; r < viewportRows; r++)
                {
                    int absRow = absDisplayStart + r;
                    var row = GetRowAbsolute(absRow);
                    RenderRowSnapshot rowSnapshot;
                    long rowId = 0;
                    uint rowRevision = 0;

                    if (row != null)
                    {
                        rowSnapshot = new RenderRowSnapshot
                        {
                            AbsRow = absRow,
                            Revision = row.Revision,
                            Cols = viewportCols,
                            Cells = GetOrBuildCachedRenderRowCells_NoLock(r, row, viewportCols),
                            RowId = row.Id
                        };
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
                        _cachedRenderRowIds[r] = 0;
                        _cachedRenderRowRevisions[r] = 0;
                        _cachedRenderRowCols[r] = 0;
                        _cachedRenderRowCells[r] = Array.Empty<RenderCellSnapshot>();
                    }

                    rowsData[r] = rowSnapshot;

                    bool fullRowDirty = !_hasSnapshotState ||
                                        _lastSnapshotCols != viewportCols ||
                                        _lastSnapshotAbsRows[r] != absRow ||
                                        _lastSnapshotRowIds[r] != rowId;

                    if (rowSnapshot.Cols > 0 && viewportCols > 0)
                    {
                        if (fullRowDirty)
                        {
                            dirtyList.Add(new DirtySpan
                            {
                                Row = r,
                                ColStart = 0,
                                ColEnd = viewportCols
                            });
                        }
                        else if (_lastSnapshotRowRevisions[r] != rowRevision)
                        {
                            AppendChangedSpansForRow_NoLock(r, rowSnapshot.Cells, viewportCols, dirtyList);
                            if (dirtyList.Count == 0 || dirtyList[^1].Row != r)
                            {
                                // Revision changed but cell diff produced no spans. Be conservative.
                                dirtyList.Add(new DirtySpan
                                {
                                    Row = r,
                                    ColStart = 0,
                                    ColEnd = viewportCols
                                });
                            }
                        }
                    }

                    _lastSnapshotAbsRows[r] = absRow;
                    _lastSnapshotRowIds[r] = rowId;
                    _lastSnapshotRowRevisions[r] = rowRevision;
                    _lastSnapshotRowCells[r] = rowSnapshot.Cells;
                }

                _lastSnapshotCols = viewportCols;
                _hasSnapshotState = true;
                dirtySpans = NormalizeDirtySpans(dirtyList, viewportRows, viewportCols);

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
            Array.Resize(ref _lastSnapshotRowCells, newLen);
            Array.Resize(ref _cachedRenderRowIds, newLen);
            Array.Resize(ref _cachedRenderRowRevisions, newLen);
            Array.Resize(ref _cachedRenderRowCols, newLen);
            Array.Resize(ref _cachedRenderRowCells, newLen);
        }

        private RenderCellSnapshot[] GetOrBuildCachedRenderRowCells_NoLock(int rowIndex, TerminalRow row, int viewportCols)
        {
            if (viewportCols <= 0)
            {
                return Array.Empty<RenderCellSnapshot>();
            }

            RenderCellSnapshot[]? cachedCells = _cachedRenderRowCells[rowIndex];
            bool canReuse =
                cachedCells != null &&
                cachedCells.Length == viewportCols &&
                _cachedRenderRowIds[rowIndex] == row.Id &&
                _cachedRenderRowRevisions[rowIndex] == row.Revision &&
                _cachedRenderRowCols[rowIndex] == viewportCols;

            if (canReuse)
            {
                return cachedCells!;
            }

            var rebuiltCells = new RenderCellSnapshot[viewportCols];
            PopulateRenderCellsFromRow_NoLock(row, viewportCols, rebuiltCells);

            _cachedRenderRowIds[rowIndex] = row.Id;
            _cachedRenderRowRevisions[rowIndex] = row.Revision;
            _cachedRenderRowCols[rowIndex] = viewportCols;
            _cachedRenderRowCells[rowIndex] = rebuiltCells;
            return rebuiltCells;
        }

        private void AppendChangedSpansForRow_NoLock(int rowIndex, RenderCellSnapshot[] currentCells, int viewportCols, List<DirtySpan> dirtySpans)
        {
            int cols = Math.Max(0, Math.Min(viewportCols, currentCells.Length));
            if (cols == 0)
            {
                return;
            }

            RenderCellSnapshot[] previousCells = _lastSnapshotRowCells[rowIndex] ?? Array.Empty<RenderCellSnapshot>();
            int previousCols = previousCells.Length;

            int spanStart = -1;
            for (int c = 0; c < cols; c++)
            {
                bool isChanged = c >= previousCols || !RenderCellEquals(previousCells[c], currentCells[c]);
                if (isChanged)
                {
                    if (spanStart < 0)
                    {
                        spanStart = c;
                    }
                }
                else if (spanStart >= 0)
                {
                    dirtySpans.Add(new DirtySpan
                    {
                        Row = rowIndex,
                        ColStart = spanStart,
                        ColEnd = c
                    });
                    spanStart = -1;
                }
            }

            if (spanStart >= 0)
            {
                dirtySpans.Add(new DirtySpan
                {
                    Row = rowIndex,
                    ColStart = spanStart,
                    ColEnd = cols
                });
            }
        }

        private static bool RenderCellEquals(in RenderCellSnapshot a, in RenderCellSnapshot b)
        {
            return a.Character == b.Character &&
                   a.Text == b.Text &&
                   a.Foreground.Equals(b.Foreground) &&
                   a.Background.Equals(b.Background) &&
                   a.IsInverse == b.IsInverse &&
                   a.IsBold == b.IsBold &&
                   a.IsDefaultForeground == b.IsDefaultForeground &&
                   a.IsDefaultBackground == b.IsDefaultBackground &&
                   a.IsWide == b.IsWide &&
                   a.IsWideContinuation == b.IsWideContinuation &&
                   a.IsHidden == b.IsHidden &&
                   a.IsFaint == b.IsFaint &&
                   a.IsItalic == b.IsItalic &&
                   a.IsUnderline == b.IsUnderline &&
                   a.IsBlink == b.IsBlink &&
                   a.IsStrikethrough == b.IsStrikethrough &&
                   a.FgIndex == b.FgIndex &&
                   a.BgIndex == b.BgIndex;
        }

        private static DirtySpan[] NormalizeDirtySpans(List<DirtySpan> spans, int viewportRows, int viewportCols)
        {
            if (spans.Count == 0 || viewportRows <= 0 || viewportCols <= 0)
            {
                return Array.Empty<DirtySpan>();
            }

            spans.Sort(static (a, b) =>
            {
                int rowCmp = a.Row.CompareTo(b.Row);
                if (rowCmp != 0) return rowCmp;

                int startCmp = a.ColStart.CompareTo(b.ColStart);
                if (startCmp != 0) return startCmp;

                return a.ColEnd.CompareTo(b.ColEnd);
            });

            var normalized = new List<DirtySpan>(spans.Count);
            for (int i = 0; i < spans.Count; i++)
            {
                var span = spans[i];
                int row = Math.Clamp(span.Row, 0, viewportRows - 1);
                int colStart = Math.Clamp(span.ColStart, 0, viewportCols);
                int colEnd = Math.Clamp(span.ColEnd, 0, viewportCols);
                if (colEnd <= colStart)
                {
                    continue;
                }

                if (normalized.Count == 0)
                {
                    normalized.Add(new DirtySpan
                    {
                        Row = row,
                        ColStart = colStart,
                        ColEnd = colEnd
                    });
                    continue;
                }

                int lastIdx = normalized.Count - 1;
                DirtySpan last = normalized[lastIdx];
                if (last.Row == row && last.ColEnd >= colStart)
                {
                    normalized[lastIdx] = new DirtySpan
                    {
                        Row = last.Row,
                        ColStart = last.ColStart,
                        ColEnd = Math.Max(last.ColEnd, colEnd)
                    };
                }
                else
                {
                    normalized.Add(new DirtySpan
                    {
                        Row = row,
                        ColStart = colStart,
                        ColEnd = colEnd
                    });
                }
            }

            return normalized.Count > 0 ? normalized.ToArray() : Array.Empty<DirtySpan>();
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

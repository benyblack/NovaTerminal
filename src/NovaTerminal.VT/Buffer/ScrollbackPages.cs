using System;
using System.Collections.Generic;

namespace NovaTerminal.Core.Storage
{
    /// <summary>
    /// Metrics snapshot for <see cref="ScrollbackPages"/>.
    /// </summary>
    public readonly struct ScrollbackMetrics
    {
        /// <summary>Number of rows currently retained in the scrollback.</summary>
        public int RowCount { get; init; }

        /// <summary>Number of page objects currently in use (rented from the pool).</summary>
        public int ActivePages { get; init; }

        /// <summary>Number of pages waiting in the pool, ready for reuse.</summary>
        public int PooledPages { get; init; }

        /// <summary>Total bytes consumed by live cell data.</summary>
        public long BytesUsed { get; init; }

        /// <summary>Configured byte budget (maximum allowed).</summary>
        public long MaxBytes { get; init; }
    }

    /// <summary>
    /// Page-based scrollback store that replaces the per-row <see cref="CircularBuffer{TerminalRow}"/>.
    ///
    /// Rows are packed into <see cref="TerminalPage"/> slabs (default 64 rows each), so adding
    /// 10,000 scrollback lines allocates ~157 page objects instead of 10,000 <c>TerminalRow</c>
    /// objects. Each page's backing <c>TerminalCell[]</c> is rented from
    /// <see cref="System.Buffers.ArrayPool{T}.Shared"/> for further allocation reuse.
    ///
    /// Memory is bounded by <see cref="MaxScrollbackBytes"/>. When the budget is exceeded the
    /// oldest pages are evicted and returned to the <see cref="TerminalPagePool"/>.
    /// </summary>
    public sealed class ScrollbackPages
    {
        // ── Configuration ────────────────────────────────────────────────────────
        /// <summary>Maximum bytes of cell data to retain. Default: 128 MB.</summary>
        public long MaxScrollbackBytes { get; set; }

        // ── Internals ────────────────────────────────────────────────────────────
        private readonly int _cols;
        private readonly int _rowsPerPage;
        private readonly TerminalPagePool _pool;

        /// <summary>
        /// Ordered list of pages, oldest first.
        /// <see cref="LinkedList{T}"/> gives O(1) front eviction.
        /// </summary>
        private readonly LinkedList<TerminalPage> _pages = new();

        private long _currentBytes;

        // Absolute row counters (never reset on eviction so indexing stays correct).
        private long _totalRowsAppended;
        private long _totalRowsEvicted;

        // ── Properties ───────────────────────────────────────────────────────────

        /// <summary>Number of rows currently accessible (after eviction).</summary>
        public int Count => (int)Math.Min(_totalRowsAppended - _totalRowsEvicted, int.MaxValue);

        /// <summary>Total rows that have ever been appended to this scrollback.</summary>
        public long TotalRowsAppended => _totalRowsAppended;

        /// <summary>Total rows that have been evicted from this scrollback due to budget limits.</summary>
        public long TotalRowsEvicted => _totalRowsEvicted;

        /// <summary>Approximate byte consumption of retained cell data.</summary>
        public long CurrentBytes => _currentBytes;

        // ── Construction ─────────────────────────────────────────────────────────

        /// <param name="cols">Number of columns per row (must match the buffer).</param>
        /// <param name="pool">Shared pool from which pages are rented and returned.</param>
        /// <param name="maxScrollbackBytes">Byte budget; defaults to 128 MB.</param>
        /// <param name="rowsPerPage">Rows per page slab; defaults to 64.</param>
        public ScrollbackPages(
            int cols,
            TerminalPagePool pool,
            long maxScrollbackBytes = 128L * 1024 * 1024,
            int rowsPerPage = TerminalPageConstants.DefaultRowsPerPage)
        {
            if (cols <= 0) throw new ArgumentOutOfRangeException(nameof(cols));
            _cols = cols;
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
            _rowsPerPage = rowsPerPage;
            MaxScrollbackBytes = maxScrollbackBytes;
        }

        // ── Public API ───────────────────────────────────────────────────────────

        /// <summary>
        /// Appends a row to the scrollback. If the current page is full, a new page
        /// is rented from the pool. Budget eviction is applied afterwards.
        /// </summary>
        /// <param name="row">Read-only span of exactly <see cref="_cols"/> cells.</param>
        /// <param name="isWrapped">Whether this row is wrapped (flows into the next).</param>
        public void AppendRow(ReadOnlySpan<TerminalCell> row, bool isWrapped = false)
        {
            if (row.Length != _cols)
                throw new ArgumentException($"Row length {row.Length} must equal Cols {_cols}.", nameof(row));

            // Ensure there is a page with available space.
            if (_pages.Last == null || _pages.Last.Value.IsFull)
            {
                var page = _pool.Rent(_cols, _rowsPerPage);
                _pages.AddLast(page);
                _currentBytes += page.ByteSize;
            }

            var currentPage = _pages.Last!.Value;
            int rowIndex = currentPage.UsedRows;
            row.CopyTo(currentPage.GetRowSpan(rowIndex));
            currentPage.SetRowWrapped(rowIndex, isWrapped);
            currentPage.UsedRows++;
            _totalRowsAppended++;

            TryEvictUntilWithinBudget();
        }

        /// <summary>
        /// Retrieves a read-only span of cells for the given logical row index
        /// (0 = oldest retained row, Count-1 = newest row).
        /// </summary>
        public bool IsRowWrapped(int logicalIndex)
        {
            if ((uint)logicalIndex >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(logicalIndex));

            long absRow = _totalRowsEvicted + logicalIndex;
            long pageStartAbs = _totalRowsEvicted;

            foreach (var page in _pages)
            {
                long pageEndAbs = pageStartAbs + page.UsedRows;
                if (absRow < pageEndAbs)
                {
                    int rowInPage = (int)(absRow - pageStartAbs);
                    return page.IsRowWrapped(rowInPage);
                }
                pageStartAbs = pageEndAbs;
            }

            return false;
        }

        /// <summary>
        /// Retrieves a read-only span of cells for the given logical row index
        /// (0 = oldest retained row, Count-1 = newest row).
        /// </summary>
        public ReadOnlySpan<TerminalCell> GetRow(int logicalIndex)
        {
            if ((uint)logicalIndex >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(logicalIndex));

            long absRow = _totalRowsEvicted + logicalIndex;
            long pageStartAbs = _totalRowsEvicted;

            foreach (var page in _pages)
            {
                long pageEndAbs = pageStartAbs + page.UsedRows;
                if (absRow < pageEndAbs)
                {
                    int rowInPage = (int)(absRow - pageStartAbs);
                    return page.GetRowSpanReadOnly(rowInPage);
                }
                pageStartAbs = pageEndAbs;
            }

            throw new InvalidOperationException($"Row {logicalIndex} not found in pages (absRow={absRow}).");
        }

        /// <summary>
        /// Copies the cells of a logical row into <paramref name="destination"/>.
        /// </summary>
        public void CopyRowTo(int logicalIndex, Span<TerminalCell> destination)
        {
            GetRow(logicalIndex).CopyTo(destination);
        }

        /// <summary>
        /// Attempts to remove the newest row from the scrollback (the reverse of AppendRow).
        /// Used during vertical resize (height grow) to pull history back into the viewport.
        /// </summary>
        public bool TryPopLastRow(Span<TerminalCell> destination)
        {
            if (_pages.Last == null || _pages.Last.Value.UsedRows == 0)
                return false;

            var page = _pages.Last.Value;
            page.GetRowSpanReadOnly(page.UsedRows - 1).CopyTo(destination);
            
            page.UsedRows--;
            _totalRowsAppended--;

            if (page.UsedRows == 0)
            {
                _pages.RemoveLast();
                _currentBytes -= page.ByteSize;
                _pool.Return(page);
            }

            return true;
        }

        /// <summary>
        /// Evicts oldest pages until total bytes are within <see cref="MaxScrollbackBytes"/>.
        /// Returned pages go back to the pool.
        /// </summary>
        public void TryEvictUntilWithinBudget()
        {
            while (_currentBytes > MaxScrollbackBytes && _pages.Count > 0)
            {
                var oldest = _pages.First!.Value;
                _pages.RemoveFirst();
                _totalRowsEvicted += oldest.UsedRows;
                _currentBytes -= oldest.ByteSize;
                _pool.Return(oldest);
            }
        }

        /// <summary>
        /// Removes all scrollback data and returns all pages to the pool.
        /// </summary>
        public void Clear()
        {
            foreach (var page in _pages)
                _pool.Return(page);

            _pages.Clear();
            _currentBytes = 0;
            _totalRowsAppended = 0;
            _totalRowsEvicted = 0;
        }

        /// <summary>
        /// Returns a point-in-time snapshot of memory metrics for diagnostics.
        /// </summary>
        public ScrollbackMetrics GetMetrics() => new ScrollbackMetrics
        {
            RowCount = Count,
            ActivePages = _pool.ActivePages,
            PooledPages = _pool.PooledPages,
            BytesUsed = _currentBytes,
            MaxBytes = MaxScrollbackBytes,
        };
    }
}

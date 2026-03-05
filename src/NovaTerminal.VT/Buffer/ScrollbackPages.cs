using System;
using System.Collections.Generic;

namespace NovaTerminal.Core.Buffer
{
    /// <summary>
    /// Page-based scrollback store that replaces the per-row <see cref="CircularBuffer{TerminalRow}"/>.
    ///
    /// Rows are packed into <see cref="TerminalPage"/> slabs (default 64 rows each), so adding
    /// 10,000 scrollback lines allocates ~157 page objects instead of 10,000 <c>TerminalRow</c>
    /// objects, reducing GC overhead dramatically.
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
        /// We use a <see cref="LinkedList{T}"/> so that eviction at the front is O(1).
        /// </summary>
        private readonly LinkedList<TerminalPage> _pages = new();

        private long _currentBytes;

        // Logical row count (the number of appended rows still retained after eviction).
        private long _totalRowsAppended;   // ever appended
        private long _totalRowsEvicted;    // already evicted from the front

        /// <summary>
        /// Number of rows currently accessible in the scrollback (after eviction).
        /// </summary>
        public int Count => (int)Math.Min(_totalRowsAppended - _totalRowsEvicted, int.MaxValue);

        /// <summary>
        /// Approximate byte consumption of retained cell data.
        /// </summary>
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
        public void AppendRow(ReadOnlySpan<TerminalCell> row)
        {
            if (row.Length != _cols)
                throw new ArgumentException($"Row length {row.Length} must equal Cols {_cols}.", nameof(row));

            // Ensure there is a page with available space.
            if (_pages.Last == null || _pages.Last.Value.IsFull)
            {
                var page = _pool.Rent(_rowsPerPage, _cols);
                _pages.AddLast(page);
                _currentBytes += page.ByteSize;
            }

            var currentPage = _pages.Last!.Value;
            row.CopyTo(currentPage.GetRowSpan(currentPage.UsedRows));
            currentPage.UsedRows++;
            _totalRowsAppended++;

            TryEvictUntilWithinBudget();
        }

        /// <summary>
        /// Retrieves a read-only span of cells for the given logical row index
        /// (0 = oldest retained row, Count-1 = newest row).
        /// </summary>
        public ReadOnlySpan<TerminalCell> GetRow(int logicalIndex)
        {
            if ((uint)logicalIndex >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(logicalIndex));

            // The absolute row number (ignoring evictions).
            long absRow = _totalRowsEvicted + logicalIndex;

            // Walk pages from the oldest to find which page owns this absolute row.
            long pageStartAbs = _totalRowsEvicted - GetEvictedRowsInCurrentFront();
            foreach (var page in _pages)
            {
                // Compute how many rows this page starts at (absolute).
                // We track this via walking: pageStartAbs updated each iteration.
                long pageEndAbs = pageStartAbs + page.UsedRows;
                if (absRow < pageEndAbs)
                {
                    int rowInPage = (int)(absRow - pageStartAbs);
                    return page.GetRowSpanReadOnly(rowInPage);
                }
                pageStartAbs = pageEndAbs;
            }

            // Should not happen if Count is correct.
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
        /// Evicts oldest pages until total bytes are within budget.
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

        // ── Private helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// When pages are evicted, _totalRowsEvicted tracks absolute rows gone.
        /// However, the first live page may not start exactly at _totalRowsEvicted
        /// if some partial-page tracking is needed.  In this design we evict whole
        /// pages, so the first page always starts at the current _totalRowsEvicted.
        /// This helper therefore returns 0.
        /// </summary>
        private static long GetEvictedRowsInCurrentFront() => 0;
    }
}

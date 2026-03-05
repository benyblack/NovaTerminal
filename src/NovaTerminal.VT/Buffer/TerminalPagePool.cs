using System;
using System.Collections.Concurrent;
using System.Threading;

namespace NovaTerminal.Core.Buffer
{
    /// <summary>
    /// Thread-safe pool of <see cref="TerminalPage"/> objects.
    ///
    /// Pages rented from this pool have their backing <see cref="TerminalPage.Cells"/>
    /// array sourced from <see cref="System.Buffers.ArrayPool{T}.Shared"/>, so
    /// returning a page also returns the cell array to the shared pool.
    ///
    /// Usage:
    /// <code>
    ///   var page = pool.Rent(cols);
    ///   // … use page …
    ///   pool.Return(page);
    /// </code>
    /// </summary>
    public sealed class TerminalPagePool
    {
        // ── State ────────────────────────────────────────────────────────────────
        private readonly ConcurrentBag<TerminalPage> _bag = new();
        private int _activePages;   // pages currently rented out
        private int _pooledPages;   // pages sitting in the bag

        // ── Configuration ────────────────────────────────────────────────────────

        /// <summary>Maximum number of pages to retain in the pool (caps idle memory).</summary>
        public int MaxPooledPages { get; set; } = 32;

        // ── Metrics ──────────────────────────────────────────────────────────────

        /// <summary>Number of pages currently rented out (in active use).</summary>
        public int ActivePages => _activePages;

        /// <summary>Number of pages sitting in the pool, ready to be rented.</summary>
        public int PooledPages => _pooledPages;

        // ── Preheat ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Pre-allocates <paramref name="count"/> pages of the given shape and places
        /// them in the pool so the first few scrollback additions don't trigger
        /// cold allocations.
        ///
        /// Called once per terminal instance at construction time
        /// (default: <see cref="TerminalPageConstants.PreheatPagesPerInstance"/> = 4 pages).
        /// </summary>
        public void Preheat(int count, int cols,
            int rowsPerPage = TerminalPageConstants.DefaultRowsPerPage)
        {
            for (int i = 0; i < count; i++)
            {
                if (_pooledPages >= MaxPooledPages) break;
                var page = new TerminalPage(rowsPerPage, cols);
                _bag.Add(page);
                Interlocked.Increment(ref _pooledPages);
            }
        }

        // ── Rent / Return ────────────────────────────────────────────────────────

        /// <summary>
        /// Rents a page from the pool (or allocates a new one if none matches).
        /// The returned page is reset to default cells with <c>UsedRows = 0</c>.
        /// </summary>
        /// <param name="cols">Number of columns the page must have.</param>
        /// <param name="rowsPerPage">Rows per page; defaults to <see cref="TerminalPageConstants.DefaultRowsPerPage"/>.</param>
        public TerminalPage Rent(int cols,
            int rowsPerPage = TerminalPageConstants.DefaultRowsPerPage)
        {
            while (_bag.TryTake(out var page))
            {
                Interlocked.Decrement(ref _pooledPages);

                if (page.RowsInPage == rowsPerPage && page.Cols == cols)
                {
                    page.Reset();
                    Interlocked.Increment(ref _activePages);
                    return page;
                }

                // Shape mismatch — return the mismatched page's array to ArrayPool
                // and discard the wrapper. This is rare (only after a terminal resize).
                page.ReturnToPool();
            }

            var newPage = new TerminalPage(rowsPerPage, cols);
            Interlocked.Increment(ref _activePages);
            return newPage;
        }

        /// <summary>
        /// Returns a page to the pool. If the pool is full the page's cell array is
        /// returned to <see cref="System.Buffers.ArrayPool{T}.Shared"/> immediately.
        /// </summary>
        public void Return(TerminalPage? page)
        {
            if (page == null) return;

            Interlocked.Decrement(ref _activePages);

            if (_pooledPages < MaxPooledPages)
            {
                page.Reset();
                _bag.Add(page);
                Interlocked.Increment(ref _pooledPages);
            }
            else
            {
                // Pool is full — release the backing array straight to ArrayPool.
                page.ReturnToPool();
            }
        }

        // ── Teardown ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Removes all pages from the pool and returns their backing arrays to
        /// <see cref="System.Buffers.ArrayPool{T}.Shared"/>.
        /// </summary>
        public void Clear()
        {
            while (_bag.TryTake(out var page))
            {
                Interlocked.Decrement(ref _pooledPages);
                page.ReturnToPool();
            }
        }
    }
}

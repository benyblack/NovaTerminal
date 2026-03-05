using System;
using System.Collections.Concurrent;

namespace NovaTerminal.Core.Buffer
{
    /// <summary>
    /// Thread-safe pool of <see cref="TerminalPage"/> objects.
    ///
    /// Renting and returning pages avoids repeated large-array allocations for
    /// the scrollback buffer. Each pool slot holds a page of a specific
    /// (rowsPerPage × cols) shape; differently-shaped pages are not pooled.
    ///
    /// This class is intentionally simple. A more sophisticated eviction strategy
    /// (Step 3) can be layered on top without changing the public API.
    /// </summary>
    public sealed class TerminalPagePool
    {
        private readonly ConcurrentBag<TerminalPage> _bag = new();

        /// <summary>Maximum number of pages to keep in the pool (to cap idle memory).</summary>
        public int MaxPooledPages { get; set; } = 32;

        /// <summary>
        /// Rents a page from the pool, or allocates a new one if none is available
        /// or if the pooled page has a different shape.
        /// The returned page has its cells reset to <see cref="TerminalCell.Default"/>.
        /// </summary>
        public TerminalPage Rent(int rowsPerPage, int cols)
        {
            while (_bag.TryTake(out var page))
            {
                if (page.RowsInPage == rowsPerPage && page.Cols == cols)
                {
                    page.Reset();
                    return page;
                }
                // Wrong shape — discard (let GC collect it). This is rare.
            }

            return new TerminalPage(rowsPerPage, cols);
        }

        /// <summary>
        /// Returns a page to the pool. The page is reset before being stored.
        /// If the pool is already full, the page is discarded.
        /// </summary>
        public void Return(TerminalPage page)
        {
            if (page == null) return;
            if (_bag.Count >= MaxPooledPages) return; // Don't over-hold memory

            page.Reset();
            _bag.Add(page);
        }

        /// <summary>
        /// Removes all pages from the pool, releasing their backing arrays to the GC.
        /// </summary>
        public void Clear()
        {
            while (_bag.TryTake(out _)) { }
        }
    }
}

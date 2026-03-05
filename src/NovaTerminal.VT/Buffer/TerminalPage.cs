using System;

namespace NovaTerminal.Core.Buffer
{
    /// <summary>
    /// A contiguous slab of TerminalCell values for multiple scrollback rows.
    /// Eliminates per-row heap objects by packing many rows into one flat array.
    /// </summary>
    public sealed class TerminalPage
    {
        /// <summary>Number of rows this page can hold.</summary>
        public readonly int RowsInPage;

        /// <summary>Number of columns per row.</summary>
        public readonly int Cols;

        /// <summary>
        /// Flat cell storage: row r, column c is at index (r * Cols + c).
        /// Length = RowsInPage * Cols.
        /// </summary>
        public readonly TerminalCell[] Cells;

        /// <summary>How many rows in this page contain actual data (0 … RowsInPage).</summary>
        public int UsedRows;

        public TerminalPage(int rowsInPage, int cols)
        {
            if (rowsInPage <= 0) throw new ArgumentOutOfRangeException(nameof(rowsInPage));
            if (cols <= 0) throw new ArgumentOutOfRangeException(nameof(cols));

            RowsInPage = rowsInPage;
            Cols = cols;
            Cells = new TerminalCell[rowsInPage * cols];
            UsedRows = 0;

            // Pre-fill with default cells so stale data is never visible.
            var def = TerminalCell.Default;
            var span = Cells.AsSpan();
            for (int i = 0; i < span.Length; i++)
                span[i] = def;
        }

        /// <summary>Returns true when no more rows can be appended to this page.</summary>
        public bool IsFull => UsedRows >= RowsInPage;

        /// <summary>
        /// Byte size of the cell data, used for budget tracking.
        /// Does not count object/array overheads (negligible once paged).
        /// </summary>
        public int ByteSize => Cells.Length * TerminalPageConstants.CellBytes;

        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a span over the cells of the given row within this page.
        /// </summary>
        /// <param name="rowIndex">Row index within the page (0-based, must be &lt; UsedRows).</param>
        public Span<TerminalCell> GetRowSpan(int rowIndex)
        {
            if ((uint)rowIndex >= (uint)RowsInPage)
                throw new ArgumentOutOfRangeException(nameof(rowIndex));
            return Cells.AsSpan(rowIndex * Cols, Cols);
        }

        /// <summary>
        /// Returns a read-only span over the cells of the given row within this page.
        /// </summary>
        public ReadOnlySpan<TerminalCell> GetRowSpanReadOnly(int rowIndex)
        {
            if ((uint)rowIndex >= (uint)RowsInPage)
                throw new ArgumentOutOfRangeException(nameof(rowIndex));
            return Cells.AsSpan(rowIndex * Cols, Cols);
        }

        /// <summary>
        /// Resets a row to all-default cells and clears the UsedRows counter
        /// back to <paramref name="rowIndex"/> so the page can be reused from that point.
        /// Typically used by the pool when reclaiming a page.
        /// </summary>
        public void ClearRow(int rowIndex)
        {
            if ((uint)rowIndex >= (uint)RowsInPage)
                throw new ArgumentOutOfRangeException(nameof(rowIndex));

            var def = TerminalCell.Default;
            var span = Cells.AsSpan(rowIndex * Cols, Cols);
            for (int i = 0; i < span.Length; i++)
                span[i] = def;
        }

        /// <summary>
        /// Resets all rows to default cells and sets UsedRows to 0.
        /// Called by the page pool before returning the page for reuse.
        /// </summary>
        public void Reset()
        {
            UsedRows = 0;
            var def = TerminalCell.Default;
            var span = Cells.AsSpan();
            for (int i = 0; i < span.Length; i++)
                span[i] = def;
        }
    }

    internal static class TerminalPageConstants
    {
        /// <summary>Measured size of TerminalCell in bytes (char + ushort + uint + uint = 12 B).</summary>
        public const int CellBytes = 12;

        /// <summary>Number of rows stored per page. 64 rows balances allocation granularity vs. overhead.</summary>
        public const int DefaultRowsPerPage = 64;
    }
}

using System;
using System.Collections.Generic;
using System.Text;

namespace NovaTerminal.Core
{
    /// <summary>
    /// Manages text selection state in the terminal.
    /// </summary>
    public class SelectionState
    {
        public bool IsActive { get; set; }
        public (int Row, int Col) Start { get; set; }
        public (int Row, int Col) End { get; set; }

        public SelectionState()
        {
            Clear();
        }

        public void Clear()
        {
            IsActive = false;
            Start = (0, 0);
            End = (0, 0);
        }

        /// <summary>
        /// Normalizes the selection so Start is always before End.
        /// </summary>
        private (int StartRow, int StartCol, int EndRow, int EndCol) Normalize()
        {
            var (startRow, startCol) = Start;
            var (endRow, endCol) = End;

            if (startRow > endRow || (startRow == endRow && startCol > endCol))
            {
                // Swap start and end
                return (endRow, endCol, startRow, startCol);
            }

            return (startRow, startCol, endRow, endCol);
        }

        /// <summary>
        /// Gets the ranges of selected cells for rendering.
        /// Returns tuples of (Row, ColStart, ColEnd) for each row that has selection.
        /// </summary>
        public IEnumerable<(int Row, int ColStart, int ColEnd)> GetSelectedRanges(int maxCols)
        {
            if (!IsActive) yield break;

            var (startRow, startCol, endRow, endCol) = Normalize();

            if (startRow == endRow)
            {
                // Selection on single row
                yield return (startRow, startCol, endCol);
            }
            else
            {
                // Multi-row selection
                // First row: from startCol to end of line
                yield return (startRow, startCol, maxCols - 1);

                // Middle rows: entire lines
                for (int row = startRow + 1; row < endRow; row++)
                {
                    yield return (row, 0, maxCols - 1);
                }

                // Last row: from start of line to endCol
                yield return (endRow, 0, endCol);
            }
        }

        /// <summary>
        /// Extracts the selected text from the buffer.
        /// </summary>
        public string GetSelectedText(TerminalBuffer buffer)
        {
            if (!IsActive) return string.Empty;

            var (startRow, startCol, endRow, endCol) = Normalize();
            var sb = new StringBuilder();

            for (int row = startRow; row <= endRow; row++)
            {
                int colStart = (row == startRow) ? startCol : 0;
                int colEnd = (row == endRow) ? endCol : buffer.Cols - 1;

                // Extract text from this row
                for (int col = colStart; col <= colEnd; col++)
                {
                    var cell = buffer.GetCellAbsolute(col, row);
                    if (cell.IsWideContinuation) continue;
                    sb.Append(cell.Text ?? cell.Character.ToString());
                }

                // Add line break if not the last row
                if (row < endRow)
                {
                    sb.AppendLine();
                }
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Checks if a specific cell is selected.
        /// </summary>
        public bool IsCellSelected(int row, int col)
        {
            if (!IsActive) return false;

            var (startRow, startCol, endRow, endCol) = Normalize();

            if (row < startRow || row > endRow) return false;

            if (row == startRow && row == endRow)
            {
                return col >= startCol && col <= endCol;
            }
            else if (row == startRow)
            {
                return col >= startCol;
            }
            else if (row == endRow)
            {
                return col <= endCol;
            }
            else
            {
                return true; // Middle rows are fully selected
            }
        }

        public (bool IsSelected, int StartCol, int EndCol) GetSelectionRangeForRow(int row, int maxCols)
        {
            if (!IsActive) return (false, 0, 0);

            var (startRow, startCol, endRow, endCol) = Normalize();

            if (row < startRow || row > endRow) return (false, 0, 0);

            int s = 0;
            int e = maxCols - 1;

            if (row == startRow) s = startCol;
            if (row == endRow) e = endCol;

            return (true, s, e);
        }
    }
}

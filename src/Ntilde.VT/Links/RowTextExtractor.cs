using System.Collections.Generic;
using System.Text;

namespace NovaTerminal.VT.Links
{
    /// <summary>
    /// Turns an absolute buffer row into its display text plus a map from each text-character
    /// index to the originating terminal column. Wide-cell continuations are skipped; extended
    /// grapheme text contributes multiple characters that all map to its starting column.
    /// </summary>
    public static class RowTextExtractor
    {
        public static (string Text, int[] CharToCol) Extract(TerminalBuffer buffer, int absRow)
        {
            if (buffer == null) return (string.Empty, System.Array.Empty<int>());

            var sb = new StringBuilder();
            var map = new List<int>();

            buffer.Lock.EnterReadLock();
            try
            {
                if (absRow < 0 || absRow >= buffer.TotalLines) return (string.Empty, System.Array.Empty<int>());

                int cols = buffer.Cols;
                for (int col = 0; col < cols; col++)
                {
                    var cell = buffer.GetCellAbsolute(col, absRow);
                    if (cell.IsWideContinuation) continue;

                    string g = buffer.GetGraphemeAbsolute(col, absRow);
                    if (string.IsNullOrEmpty(g)) g = " ";
                    foreach (char ch in g)
                    {
                        sb.Append(ch);
                        map.Add(col);
                    }
                }
            }
            finally { buffer.Lock.ExitReadLock(); }

            return (sb.ToString(), map.ToArray());
        }

        /// <summary>Maps a char-range LinkSpan to an inclusive [StartCol, EndCol] column range.</summary>
        public static (int StartCol, int EndCol) SpanToColumns(LinkSpan span, int[] charToCol)
        {
            int startCol = charToCol[span.StartChar];
            int endCol = charToCol[span.EndChar - 1];
            return (startCol, endCol);
        }
    }
}

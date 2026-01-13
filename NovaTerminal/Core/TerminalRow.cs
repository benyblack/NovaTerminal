using System.Collections.Generic;

namespace NovaTerminal.Core
{
    public class TerminalRow
    {
        public TerminalCell[] Cells;
        // If true, this line ends because it wrapped automatically.
        // If false, it ends because of an explicit newline (or end of buffer).
        public bool IsWrapped { get; set; } = false;

        public TerminalRow(int cols)
        {
            Cells = new TerminalCell[cols];
            for (int i = 0; i < cols; i++) Cells[i] = TerminalCell.Default;
        }
    }
}

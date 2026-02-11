using System.Collections.Generic;

namespace NovaTerminal.Core
{
    public class TerminalRow
    {
        public TerminalCell[] Cells;
        // If true, this line ends because it wrapped automatically.
        // If false, it ends because of an explicit newline (or end of buffer).
        public bool IsWrapped { get; set; } = false;
        public uint Revision { get; set; } = 0;
        public void TouchRevision() => Revision++;

        public TerminalRow(int cols)
        {
            Cells = new TerminalCell[cols];
            for (int i = 0; i < cols; i++) Cells[i] = TerminalCell.Default;
        }

        public TerminalRow(int cols, Avalonia.Media.Color fg, Avalonia.Media.Color bg)
        {
            Cells = new TerminalCell[cols];
            for (int i = 0; i < cols; i++)
            {
                // Initialize as Default so they update when theme changes
                Cells[i] = new TerminalCell(' ', fg, bg, false, false, true, true);
            }
        }
    }
}

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

        // M2.2: Side-table for extended graphemes (strings)
        private Dictionary<int, string>? _extendedText;
        private Dictionary<int, string>? _hyperlinks;

        public string? GetExtendedText(int col)
        {
            if (_extendedText == null) return null;
            return _extendedText.TryGetValue(col, out var text) ? text : null;
        }

        public void SetExtendedText(int col, string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                _extendedText?.Remove(col);
                if (_extendedText?.Count == 0) _extendedText = null;
                return;
            }
            _extendedText ??= new Dictionary<int, string>();
            _extendedText[col] = text;
        }

        public void ClearExtendedText()
        {
            _extendedText = null;
        }

        public string? GetHyperlink(int col)
        {
            if (_hyperlinks == null) return null;
            return _hyperlinks.TryGetValue(col, out var link) ? link : null;
        }

        public void SetHyperlink(int col, string? link)
        {
            if (string.IsNullOrWhiteSpace(link))
            {
                _hyperlinks?.Remove(col);
                if (_hyperlinks?.Count == 0) _hyperlinks = null;
                return;
            }

            _hyperlinks ??= new Dictionary<int, string>();
            _hyperlinks[col] = link;
        }

        public void ClearHyperlinks()
        {
            _hyperlinks = null;
        }

        public TerminalRow(int cols)
        {
            Cells = new TerminalCell[cols];
            for (int i = 0; i < cols; i++) Cells[i] = TerminalCell.Default;
        }

        public TerminalRow(int cols, TermColor fg, TermColor bg)
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

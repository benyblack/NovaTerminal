using System.Collections.Generic;

namespace NovaTerminal.Core
{
    public class TerminalRow
    {
        private static long _nextId = 0;
        public readonly long Id;

        public TerminalCell[] Cells;
        // If true, this line ends because it wrapped automatically.
        // If false, it ends because of an explicit newline (or end of buffer).
        public bool IsWrapped { get; set; } = false;
        public uint Revision { get; set; } = 0;
        public void TouchRevision() => Revision++;

        // M2.2: Side-table for extended graphemes (strings)
        private Storage.SmallMap<string>? _extendedText;
        private Storage.SmallMap<string>? _hyperlinks;

        public string? GetExtendedText(int col)
        {
            if (_extendedText == null) return null;
            return _extendedText.TryGet(col, out var text) ? text : null;
        }

        public void SetExtendedText(int col, string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                _extendedText?.Remove(col);
                if (_extendedText?.Count == 0) _extendedText = null;
                return;
            }
            _extendedText ??= new Storage.SmallMap<string>();
            _extendedText.Set(col, text);
        }

        public void ClearExtendedText()
        {
            _extendedText = null;
        }

        public string? GetHyperlink(int col)
        {
            if (_hyperlinks == null) return null;
            return _hyperlinks.TryGet(col, out var link) ? link : null;
        }

        public void SetHyperlink(int col, string? link)
        {
            if (string.IsNullOrWhiteSpace(link))
            {
                _hyperlinks?.Remove(col);
                if (_hyperlinks?.Count == 0) _hyperlinks = null;
                return;
            }

            _hyperlinks ??= new Storage.SmallMap<string>();
            _hyperlinks.Set(col, link);
        }

        public void ClearHyperlinks()
        {
            _hyperlinks = null;
        }

        /// <summary>
        /// Returns the raw SmallMap backing extended text (grapheme clusters) for this row.
        /// This is intended for preservation into paged scrollback — do not cache or mutate.
        /// </summary>
        public Storage.SmallMap<string>? GetExtendedTextMap() => _extendedText;

        /// <summary>
        /// Returns the raw SmallMap backing hyperlinks for this row.
        /// This is intended for preservation into paged scrollback — do not cache or mutate.
        /// </summary>
        public Storage.SmallMap<string>? GetHyperlinkMap() => _hyperlinks;

        public TerminalRow(int cols)
        {
            Id = System.Threading.Interlocked.Increment(ref _nextId);
            Cells = new TerminalCell[cols];
            for (int i = 0; i < cols; i++) Cells[i] = TerminalCell.Default;
        }

        public TerminalRow(int cols, TermColor fg, TermColor bg)
        {
            Id = System.Threading.Interlocked.Increment(ref _nextId);
            Cells = new TerminalCell[cols];
            for (int i = 0; i < cols; i++)
            {
                // Initialize as Default so they update when theme changes
                Cells[i] = new TerminalCell(' ', fg, bg, false, false, true, true);
            }
        }
    }
}

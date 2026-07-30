using System.Collections.Generic;

namespace NovaTerminal.VT
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

        /// <summary>
        /// True when this row carries any side-table entry. Lets callers on the write hot path
        /// (insert mode runs per printable character) skip metadata maintenance entirely for
        /// the overwhelmingly common plain-ASCII, no-hyperlink row.
        /// </summary>
        public bool HasRowMetadata => _extendedText != null || _hyperlinks != null;

        /// <summary>
        /// Moves side-table entries to follow a horizontal cell shift, so extended graphemes and
        /// hyperlinks stay attached to the cells they describe.
        /// </summary>
        /// <param name="startCol">First column affected — the cursor column for ICH/DCH.</param>
        /// <param name="delta">
        /// Positive to shift right (ICH / insert mode), negative to shift left (DCH).
        /// </param>
        /// <param name="cols">Row width; entries pushed past it are dropped.</param>
        /// <remarks>
        /// Without this, <c>CSI @</c> / <c>CSI P</c> moved <see cref="Cells"/> but left the maps
        /// keyed to pre-shift columns: cells kept <c>HasExtendedText</c> while their strings —
        /// and their hyperlinks — pointed at the wrong columns (issue #164, item 1).
        /// </remarks>
        public void ShiftRowMetadata(int startCol, int delta, int cols)
        {
            if (delta == 0) return;
            _extendedText = ShiftMap(_extendedText, startCol, delta, cols);
            _hyperlinks = ShiftMap(_hyperlinks, startCol, delta, cols);
        }

        private static Storage.SmallMap<string>? ShiftMap(
            Storage.SmallMap<string>? map, int startCol, int delta, int cols)
        {
            if (map is null || map.Count == 0) return null;

            // Snapshot before rebuilding: shifting in place would revisit entries that had already
            // been relocated (and, for a right shift, overwrite ones not yet visited).
            int n = map.Count;
            var keys = new int[n];
            var values = new string[n];
            int i = 0;
            map.ForEach((col, value) =>
            {
                keys[i] = col;
                values[i] = value;
                i++;
            });

            Storage.SmallMap<string>? shifted = null;
            for (int e = 0; e < n; e++)
            {
                int col = keys[e];
                int dest = col;
                if (col >= startCol)
                {
                    dest = col + delta;

                    // Dropped: pushed off the end of the row (ICH), or consumed by the deletion
                    // itself (DCH moves the deleted columns to below startCol). Columns left of
                    // startCol are untouched and must not be range-checked against startCol.
                    if (dest < startCol || dest >= cols) continue;
                }

                shifted ??= new Storage.SmallMap<string>();
                shifted.Set(dest, values[e]);
            }

            return shifted;
        }

        /// <summary>
        /// Copies <paramref name="source"/>'s side-table entries for columns below
        /// <paramref name="columnLimit"/> onto this row, leaving higher columns untouched.
        /// </summary>
        /// <remarks>
        /// Only valid where columns map one-to-one — the resize paths that copy cells straight
        /// across rather than reflowing them (the alt screen, and detached screen buffers). Reflow
        /// re-columns its content and has to map each cell individually instead.
        ///
        /// Without this, those paths rebuilt rows from <see cref="Cells"/> alone, so a resize while
        /// an alt-screen application was up dropped every extended grapheme and OSC 8 link on
        /// screen — the same loss as reflow's, on the no-reflow path (#164).
        /// </remarks>
        public void CopyRowMetadataFrom(TerminalRow source, int columnLimit)
        {
            if (!source.HasRowMetadata || columnLimit <= 0) return;

            source._extendedText?.ForEach((col, text) =>
            {
                if (col < columnLimit) SetExtendedText(col, text);
            });
            source._hyperlinks?.ForEach((col, link) =>
            {
                if (col < columnLimit) SetHyperlink(col, link);
            });
        }

        /// <summary>
        /// Installs side tables wholesale. Counterpart of the Get*Map accessors:
        /// used when a row is restored from paged scrollback (height grow) so
        /// extended graphemes and hyperlinks survive the round trip. The row
        /// takes ownership of the maps.
        /// </summary>
        public void RestoreSideTables(Storage.SmallMap<string>? extendedText, Storage.SmallMap<string>? hyperlinks)
        {
            _extendedText = extendedText is { Count: > 0 } ? extendedText : null;
            _hyperlinks = hyperlinks is { Count: > 0 } ? hyperlinks : null;
        }

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

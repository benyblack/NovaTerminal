using System;

namespace NovaTerminal.Shell
{
    /// <summary>
    /// Tracks the vertical tab sidebar's one-line output preview for a single tab, picking the
    /// bottom-most row whose content has meaningfully changed since the last update rather than
    /// simply the bottom-most non-empty row.
    ///
    /// TUI-style agent CLIs (Claude Code and similar) pin a static status/input bar to the very
    /// bottom of the screen — a prompt box, a spinner, a token counter — that is almost always
    /// the last non-empty row but never carries new information: it just sits there or ticks a
    /// counter. Picking "the bottom-most non-empty row" for the preview therefore locks onto that
    /// chrome forever and the sidebar preview never changes. Instead, this tracker scans upward
    /// from the bottom for the first row that both has content and looks like new output (rather
    /// than the same chrome with a spinner glyph or a token count updated), and falls back to
    /// keeping whatever the previous preview was when nothing on screen qualifies — so a quiet
    /// screen (nothing changed at all) doesn't blank out a perfectly good preview.
    /// </summary>
    internal sealed class TabPreviewTracker
    {
        /// <summary>
        /// Fraction of character positions (over the longer of the two strings) that must differ
        /// for a row to count as "meaningfully changed" rather than "the same chrome with a minor
        /// tick" (spinner glyph, token counter, elapsed timer, etc).
        /// </summary>
        internal const double MeaningfulChangeRatio = 0.4;

        private string[] _previousRows = Array.Empty<string>();
        private string _preview = string.Empty;

        /// <summary>
        /// Feeds the tab's current visible viewport rows (top-to-bottom, each already
        /// <c>TrimEnd</c>'ed) into the tracker and returns the updated preview text.
        /// </summary>
        public string Update(string[] currentRows)
        {
            if (_previousRows.Length == 0)
            {
                // Cold start: no history to diff against, so keep today's behavior — the
                // bottom-most non-empty row.
                _preview = FindBottomMostNonEmpty(currentRows);
            }
            else
            {
                for (int i = currentRows.Length - 1; i >= 0; i--)
                {
                    string current = currentRows[i];
                    if (string.IsNullOrEmpty(current)) continue;

                    string? previous = i < _previousRows.Length ? _previousRows[i] : null;
                    if (IsMeaningfulChange(previous, current))
                    {
                        _preview = current;
                        break;
                    }
                }
                // If no row qualified, `_preview` is left untouched (sticky).
            }

            _previousRows = currentRows;
            return _preview;
        }

        private static string FindBottomMostNonEmpty(string[] rows)
        {
            for (int i = rows.Length - 1; i >= 0; i--)
            {
                if (!string.IsNullOrEmpty(rows[i])) return rows[i];
            }

            return string.Empty;
        }

        /// <summary>
        /// True when <paramref name="current"/> differs enough from <paramref name="previous"/>
        /// to count as new content rather than the same static row with a minor tick (spinner
        /// glyph, token counter, elapsed timer). A null/empty previous value always counts as
        /// changed (there's nothing to compare against — e.g. a row index that didn't exist
        /// before). Otherwise this compares position-by-position over the longer of the two
        /// strings (positions past the end of the shorter one count as differing) and requires
        /// at least <see cref="MeaningfulChangeRatio"/> of positions to differ.
        /// </summary>
        public static bool IsMeaningfulChange(string? previous, string current)
        {
            if (string.IsNullOrEmpty(previous)) return true;

            int maxLen = Math.Max(previous.Length, current.Length);
            if (maxLen == 0) return false;

            int diffCount = 0;
            for (int i = 0; i < maxLen; i++)
            {
                char a = i < previous.Length ? previous[i] : '\0';
                char b = i < current.Length ? current[i] : '\0';
                if (a != b) diffCount++;
            }

            double ratio = (double)diffCount / maxLen;
            return ratio >= MeaningfulChangeRatio;
        }
    }
}

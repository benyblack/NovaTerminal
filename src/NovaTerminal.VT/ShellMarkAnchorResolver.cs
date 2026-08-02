namespace NovaTerminal.VT
{
    /// <summary>
    /// Resolves an <c>OSC 133;B</c> mark to the row it currently occupies <i>on screen</i>: the
    /// zero-based visual row inside the rendered viewport, or nothing when the marked line is not
    /// in the viewport.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Command Assist V2 anchors its bubble/popup to the prompt row. With a
    /// mark that row is a fact rather than the cursor-position guess the geometric heuristic makes,
    /// but the mark records a <i>buffer</i> position and the overlay needs a <i>viewport</i> one.
    /// The conversion depends on the scroll offset, so it has to be re-derived on every placement
    /// pass; scrolling the marked prompt off screen must yield "no anchor", not a row outside the
    /// pane.
    /// </para>
    /// <para>
    /// <b>Placement.</b> Next to <see cref="GridQueryReader"/> and for the same reason: this is
    /// buffer arithmetic over VT types, and the layering tests forbid
    /// <c>NovaTerminal.CommandAssist</c> from referencing <c>NovaTerminal.VT</c>. The App layer
    /// converts at the boundary.
    /// </para>
    /// <para>
    /// <b>Validity rules are shared with <see cref="GridQueryReader"/></b> — generation epoch first
    /// (a stale <c>AbsoluteRow</c> resolves to a plausible but wrong row after a scrollback reset),
    /// then alt screen, then eviction. It never throws and never guesses: every ambiguity is
    /// <c>false</c>.
    /// </para>
    /// </remarks>
    public static class ShellMarkAnchorResolver
    {
        /// <summary>
        /// Converts <paramref name="mark"/> to a viewport row.
        /// </summary>
        /// <param name="buffer">The buffer the mark was taken against.</param>
        /// <param name="mark">The newest <c>OSC 133;B</c> mark.</param>
        /// <param name="scrollOffset">
        /// Rows the viewport is scrolled back by; 0 means pinned to the live edge. This is the same
        /// value <see cref="TerminalBuffer.GetVisualCursorRow"/> takes. Must be in
        /// <c>[0, Scrollback.Count]</c>: anything outside that names a viewport that cannot be
        /// reached, and is refused rather than extrapolated.
        /// </param>
        /// <param name="visibleRows">
        /// How many rows the renderer is actually drawing. Deliberately the renderer's count rather
        /// than <c>buffer.Rows</c>: during a resize the two disagree for a frame, and "is the mark
        /// on screen" is a question about what is on screen.
        /// </param>
        /// <param name="visualRow">The zero-based row inside the viewport, when resolvable.</param>
        /// <returns><c>false</c> when the mark is stale or its line is outside the viewport.</returns>
        public static bool TryResolveVisualRow(
            TerminalBuffer buffer,
            ShellIntegrationMark mark,
            int scrollOffset,
            int visibleRows,
            out int visualRow)
        {
            visualRow = -1;
            if (buffer is null || visibleRows <= 0 || scrollOffset < 0)
            {
                return false;
            }

            // Non-recursive lock, same discipline as GridQueryReader: only acquire if this thread
            // is not already inside one, because re-entering TerminalBuffer.Lock throws.
            bool lockTaken = false;
            if (!buffer.Lock.IsReadLockHeld &&
                !buffer.Lock.IsWriteLockHeld &&
                !buffer.Lock.IsUpgradeableReadLockHeld)
            {
                buffer.Lock.EnterReadLock();
                lockTaken = true;
            }

            try
            {
                return TryResolveVisualRowLocked(buffer, mark, scrollOffset, visibleRows, out visualRow);
            }
            finally
            {
                if (lockTaken)
                {
                    buffer.Lock.ExitReadLock();
                }
            }
        }

        private static bool TryResolveVisualRowLocked(
            TerminalBuffer buffer,
            ShellIntegrationMark mark,
            int scrollOffset,
            int visibleRows,
            out int visualRow)
        {
            visualRow = -1;

            // Coordinate-space epoch first: after a scrollback reset (CSI 3J, RIS, clear-buffer,
            // reflow) a stale AbsoluteRow resolves to a perfectly plausible row, so no arithmetic
            // check downstream can catch it.
            if (mark.Generation != buffer.Scrollback.Generation)
            {
                return false;
            }

            // The alt screen has no scrollback and no shared row numbering, and Command Assist is
            // hidden there anyway.
            if (mark.IsAltScreen || buffer.IsAltScreenActive)
            {
                return false;
            }

            long derivedRow = mark.AbsoluteRow - buffer.Scrollback.TotalRowsEvicted;
            if (derivedRow < 0)
            {
                return false; // the marked line aged out of history
            }

            if (derivedRow >= buffer.InternalTotalLines)
            {
                return false; // past the end of the buffer: not a row that exists
            }

            // An offset larger than the history that exists names a viewport nobody can be
            // scrolled to. Left unchecked it drives viewportTop negative, which shifts every
            // derivedRow *down* by the overshoot and hands back a plausible-looking row for a
            // mark that is nowhere near it - the one failure mode this type is supposed to make
            // impossible. Every ambiguity is false, and an impossible offset is one.
            if (scrollOffset > buffer.Scrollback.Count)
            {
                return false;
            }

            // Viewport top in the buffer's current addressing space, mirroring
            // TerminalBuffer.GetVisualCursorRow: the live edge starts at Scrollback.Count, and
            // scrolling back moves the top up by scrollOffset rows.
            long viewportTop = buffer.Scrollback.Count - (long)scrollOffset;
            long row = derivedRow - viewportTop;
            if (row < 0 || row >= visibleRows)
            {
                return false; // scrolled out of the viewport
            }

            visualRow = (int)row;
            return true;
        }
    }
}

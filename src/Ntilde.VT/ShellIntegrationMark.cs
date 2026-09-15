namespace NovaTerminal.VT
{
    /// <summary>
    /// Where an OSC 133 shell-integration mark landed in the buffer, captured at parse time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Emitted for <c>OSC 133;B</c> (prompt end / start of the user's input), which is the
    /// anchor a grid reader needs in order to extract the live command line from the buffer
    /// instead of mirroring keystrokes.
    /// </para>
    /// <para>
    /// Two row coordinates are carried on purpose:
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="Row"/> is the row index in the buffer's <i>current</i> addressing space
    /// (scrollback row count + cursor row on the main screen; the cursor row itself on the
    /// alt screen). It is what <c>TerminalBuffer.GetRow</c> / <c>GetCell</c> take, but it
    /// shifts down by one every time a row is evicted from scrollback, so it is only valid
    /// for immediate use.
    /// </description></item>
    /// <item><description>
    /// <see cref="AbsoluteRow"/> is <c>Scrollback.TotalRowsEvicted + Row</c>: a
    /// monotonically increasing, eviction-stable identity for the same physical line.
    /// A later consumer re-derives the current row as
    /// <c>AbsoluteRow - Scrollback.TotalRowsEvicted</c>. This is the same identity the
    /// render path already uses for row caching (see
    /// <c>TerminalBuffer.ThreadingAndInvalidation</c>).
    /// </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Staleness contract.</b> Two different things can invalidate
    /// <see cref="AbsoluteRow"/>, and only one of them is detectable from the row number:
    /// <list type="bullet">
    /// <item><description>
    /// <i>Eviction</i> — the marked line aged out of history. <c>TotalRowsEvicted</c> only
    /// grows, so <c>AbsoluteRow - TotalRowsEvicted</c> goes negative and the mark is
    /// self-evidently dead.
    /// </description></item>
    /// <item><description>
    /// <i>Coordinate-space reset</i> — <c>ScrollbackPages.Clear()</c> resets BOTH counters to
    /// zero. It is reached from CSI 3J ("clear scrollback", what <c>clear(1)</c> sends with
    /// the <c>E3</c> terminfo capability), RIS, the user's clear-buffer action, and reflow.
    /// After it, a pre-reset <see cref="AbsoluteRow"/> resolves to a large <i>positive</i>
    /// row that simply belongs to different content — there is nothing wrong-looking about
    /// it. That case is why <see cref="Generation"/> exists: compare it against
    /// <c>Scrollback.Generation</c> and discard the mark when they differ. A negative row is
    /// a sufficient staleness test only <i>within</i> one generation.
    /// </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// A reflowing resize both re-wraps logical lines and rebuilds the scrollback store, so
    /// it bumps <see cref="Generation"/> too and pre-resize ids are correctly rejected. In
    /// practice this is benign, because every shell re-prints its prompt after a resize and
    /// the prompt itself carries the B mark, so a fresh mark arrives with fresh coordinates.
    /// </para>
    /// <para>
    /// <see cref="IsAltScreen"/> marks are captured against the alt screen, which has no
    /// scrollback: their <see cref="AbsoluteRow"/> shares no numbering with main-screen
    /// marks and must not be compared across the two.
    /// </para>
    /// </remarks>
    /// <param name="Row">Row index in the buffer's current addressing space.</param>
    /// <param name="Column">Cursor column at the moment the mark was parsed.</param>
    /// <param name="AbsoluteRow">Eviction-stable row identity for <paramref name="Row"/>.</param>
    /// <param name="IsAltScreen">True when the mark was captured while the alt screen was active.</param>
    /// <param name="Generation">
    /// <c>Scrollback.Generation</c> at capture time — the epoch <paramref name="AbsoluteRow"/>
    /// is expressed in. A mark whose generation no longer matches the buffer's is invalid.
    /// </param>
    public readonly record struct ShellIntegrationMark(
        int Row,
        int Column,
        long AbsoluteRow,
        bool IsAltScreen,
        long Generation);
}

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
    /// <c>AbsoluteRow - Scrollback.TotalRowsEvicted</c>; a negative result means the marked
    /// line has aged out of history. This is the same identity the render path already uses
    /// for row caching (see <c>TerminalBuffer.ThreadingAndInvalidation</c>).
    /// </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Neither coordinate survives a reflowing resize: reflow rebuilds the scrollback store
    /// and re-wraps logical lines, so ids issued before a resize must be discarded. In
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
    public readonly record struct ShellIntegrationMark(
        int Row,
        int Column,
        long AbsoluteRow,
        bool IsAltScreen);
}

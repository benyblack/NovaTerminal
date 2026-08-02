namespace NovaTerminal.CommandAssist.ShellIntegration.Contracts;

/// <summary>
/// Buffer position of an OSC 133 shell-integration mark, in terms this assembly can hold
/// without referencing the VT core.
/// </summary>
/// <remarks>
/// <para>
/// This mirrors <c>NovaTerminal.VT.ShellIntegrationMark</c>; the App layer converts at the
/// boundary, the same way <c>AssistPoint</c>/<c>AssistRect</c> keep Avalonia geometry out of
/// this assembly.
/// </para>
/// <para>
/// <see cref="Row"/> is only valid until the next scrollback eviction; <see cref="AbsoluteRow"/>
/// is the eviction-stable identity (<c>AbsoluteRow - totalRowsEvicted</c> re-derives the current
/// row, and a negative result means the marked line has scrolled out of history).
/// </para>
/// <para>
/// A negative row is <i>not</i> the only staleness case. Clearing the scrollback (CSI 3J — what
/// <c>clear(1)</c> sends with the <c>E3</c> capability — plus RIS, the user's clear-buffer
/// action, and reflow) resets the buffer's row counters to zero, after which a pre-reset
/// <see cref="AbsoluteRow"/> resolves to a perfectly plausible but wrong row.
/// <see cref="Generation"/> is the epoch that makes this detectable: the mark is only usable
/// while it still equals the buffer's current scrollback generation. Every shell re-emits its
/// prompt — and therefore its B mark — after a resize or a clear, so a fresh mark follows.
/// </para>
/// </remarks>
/// <param name="Row">Row index in the buffer's current addressing space at mark time.</param>
/// <param name="Column">Cursor column at mark time; the first cell of the user's input.</param>
/// <param name="AbsoluteRow">Eviction-stable row identity for <paramref name="Row"/>.</param>
/// <param name="IsAltScreen">True when the mark was captured on the alt screen.</param>
/// <param name="Generation">
/// Scrollback coordinate-space epoch at mark time; a mark whose generation differs from the
/// buffer's current one must be discarded rather than resolved.
/// </param>
public readonly record struct ShellMarkPosition(
    int Row,
    int Column,
    long AbsoluteRow,
    bool IsAltScreen,
    long Generation);

namespace NovaTerminal.VT
{
    /// <summary>
    /// The live command line as read out of the terminal grid by
    /// <see cref="GridQueryReader.TryReadCommandLine"/>: the cells between an
    /// <c>OSC 133;B</c> mark and the cursor, in reading order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is grid truth, not a keystroke mirror. It stays correct across history recall,
    /// <c>Ctrl+U</c>, tab completion, bracketed paste and any other edit the shell's line
    /// editor performs without telling the terminal what it did — the V1 shadow buffer could
    /// not, which is why it desynced.
    /// </para>
    /// <para>
    /// <b>Only valid between <c>OSC 133;B</c> and the following <c>OSC 133;C</c>.</b> The reader
    /// cannot tell "the user is still typing" from "the command already ran and this is its
    /// output": both look like cells between the mark and the cursor. Gating on the command
    /// lifecycle is the caller's job.
    /// </para>
    /// </remarks>
    /// <param name="Text">
    /// The extracted text. Soft-wrapped (auto-wrapped) rows are joined with no separator, so a
    /// logical line that spans several physical rows comes back as one line. A hard line break
    /// inside the span — shell continuation input — becomes a single <c>'\n'</c>; see
    /// <paramref name="IsMultiline"/>.
    /// </param>
    /// <param name="CursorOffset">
    /// The cursor's index within <paramref name="Text"/>. Always in <c>[0, Text.Length]</c>: the
    /// cursor is frequently mid-line (arrow keys, <c>Ctrl+A</c>), so this is not simply the
    /// length. When the cursor sits on the trailing cell of a double-width character the offset
    /// lands after that character.
    /// </param>
    /// <param name="IsMultiline">
    /// True when <paramref name="Text"/> contains a hard line break — a shell continuation
    /// entry, or a bracketed paste of several lines. <paramref name="Text"/> is then
    /// <i>raw</i>: it still contains whatever the shell painted as a
    /// continuation prompt (<c>PS2</c>, <c>PROMPT2</c>, <c>&gt;&nbsp;</c>), because nothing marks
    /// those cells as prompt rather than input. Consumers must treat multiline text as opaque —
    /// fine for history and display, never a typed prefix to complete against.
    /// </param>
    /// <param name="RightPromptTrimmed">
    /// True when trailing cells were excluded as a right-aligned prompt (zsh <c>RPROMPT</c>,
    /// fish <c>fish_right_prompt</c>, starship's right prompt). Diagnostic only; the exclusion
    /// rule is documented on <see cref="GridQueryReader"/>.
    /// </param>
    /// <param name="StartRow">
    /// First row of the span in the buffer's current addressing space (scrollback rows +
    /// viewport row). Shifts under scrollback eviction like any other current-space row index.
    /// </param>
    /// <param name="EndRow">Last row of the span, same addressing space as <paramref name="StartRow"/>.</param>
    public readonly record struct GridCommandLine(
        string Text,
        int CursorOffset,
        bool IsMultiline,
        bool RightPromptTrimmed,
        int StartRow,
        int EndRow);
}

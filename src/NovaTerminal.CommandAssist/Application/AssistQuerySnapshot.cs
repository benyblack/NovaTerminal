namespace NovaTerminal.CommandAssist.Application;

/// <summary>
/// What the shell's line editor currently holds, as read out of the terminal grid: the V2 query.
/// </summary>
/// <remarks>
/// <para>
/// This is Command Assist's own shape for <c>NovaTerminal.VT.GridCommandLine</c>. The two records
/// carry the same facts, but this assembly may not reference VT (the layering tests forbid it), so
/// the App maps one to the other at its boundary and the grid crosses into Command Assist as plain
/// data. The fields are deliberately the ones a consumer has to branch on, not the ones the reader
/// found convenient: the span's row numbers stay behind in VT because nothing here has a use for
/// them.
/// </para>
/// <para>
/// A snapshot only exists while the shell is between <c>OSC 133;B</c> and the <c>OSC 133;C</c> that
/// closes the line editor. Outside that window - and in a session with no shell integration at all -
/// there is no snapshot, and the honest query is "unknown", not "empty". Callers get
/// <see langword="null"/> and are expected to drop prefix-dependent behavior rather than guess.
/// </para>
/// </remarks>
/// <param name="Text">
/// The command line exactly as it is painted, with hard line breaks as <c>'\n'</c>. Never null.
/// </param>
/// <param name="CursorOffset">
/// Where the cursor sits within <paramref name="Text"/>; always a valid index into it. Routinely
/// less than the length, because arrow keys are normal.
/// </param>
/// <param name="IsMultiline">
/// The entry spans a hard line break, so <paramref name="Text"/> contains whatever the shell painted
/// as a continuation prompt (<c>PS2</c>, <c>PROMPT2</c>, fish's <c>&gt; </c>). Nothing marks those
/// cells as prompt rather than input, so the text is usable for history and display but is not a
/// typed prefix.
/// </param>
/// <param name="RightPromptTrimmed">
/// A right-aligned prompt (zsh <c>RPROMPT</c>, fish <c>fish_right_prompt</c>, starship's right
/// prompt) was recognised on the final row and excluded. The reader's trim is deliberately
/// conservative, but "conservative" is not "certain": the tail of the line is the one part of it
/// that may not be what the user typed.
/// </param>
public readonly record struct AssistQuerySnapshot(
    string Text,
    int CursorOffset,
    bool IsMultiline,
    bool RightPromptTrimmed)
{
    /// <summary>
    /// Whether <see cref="Text"/> may be treated as a prefix the user typed and left the cursor at
    /// the end of - the assumption every suffix-append insertion rests on.
    /// </summary>
    /// <remarks>
    /// Three ways it fails, and all three are ordinary rather than exotic:
    /// <list type="bullet">
    /// <item>the cursor is not at the end, so appending would land text in the middle of the line;</item>
    /// <item>the entry is multiline, so the text contains continuation-prompt cells that were never
    /// typed;</item>
    /// <item>a right prompt was trimmed, so the tail of the line is the reader's judgement rather
    /// than an observation.</item>
    /// </list>
    /// Ranking and help still run on an untrustworthy snapshot - a bad ranking shows the wrong rows,
    /// which the user ignores. A bad insertion edits the user's command line.
    /// </remarks>
    public bool IsUsableAsTypedPrefix =>
        !IsMultiline && !RightPromptTrimmed && CursorOffset == Text.Length;

    /// <summary>
    /// <see cref="Text"/> up to the cursor: what the user has actually put on the line to the left of
    /// where they are typing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The PR #293 review's first blocker, and why the reader is not the thing that was
    /// wrong.</strong> <see cref="Text"/> is the whole painted line, and PSReadLine's inline prediction
    /// is painted on that line - real cells, to the right of the cursor, in a dim colour the grid does
    /// not record as "not input". So with predictions on (<c>PredictionSource
    /// HistoryAndPlugin</c>, <c>PredictionViewStyle InlineView</c>, which is the pwsh 7.2+ default for
    /// many users) typing <c>ec</c> produced <c>Text = "echo some long thing from history"</c> with
    /// <c>CursorOffset = 2</c>. Every consumer that ranked or tokenized <see cref="Text"/> was
    /// therefore working from the shell's guess rather than from the user's two characters: the bubble
    /// ranked on the prediction, and the two-character floor measured the prediction's length, so it
    /// let a one-character line through as well.
    /// </para>
    /// <para>
    /// The reader is right to return the whole line - it reports what is painted, and it cannot tell a
    /// prediction from text the user typed and then arrowed left through. This is the projection every
    /// consumer of the query as a <em>prefix</em> wants, and the cursor is the only signal that
    /// distinguishes the two regions.
    /// </para>
    /// <para>
    /// Not a substitute for <see cref="IsUsableAsTypedPrefix"/>. Insertion still refuses when the
    /// cursor is mid-line, because "the text left of the cursor" says nothing about what appending
    /// would do to the text right of it - and a prediction and a mid-line cursor are indistinguishable
    /// on the grid, so refusing both is the only safe reading. Ranking and the Help token are the
    /// consumers this is for: they want the best available prefix and cannot damage anything by being
    /// wrong.
    /// </para>
    /// <para>
    /// Clamped rather than trusting the invariant. <see cref="CursorOffset"/> is documented as always a
    /// valid index, and a defensive clamp here costs two comparisons and removes a whole class of
    /// crash from a reader change.
    /// </para>
    /// </remarks>
    public string TextBeforeCursor =>
        CursorOffset <= 0 ? string.Empty :
        CursorOffset >= Text.Length ? Text :
        Text[..CursorOffset];
}

using System;
using System.Diagnostics;

namespace NovaTerminal.CommandAssist.Application;

/// <summary>
/// Works out what the terminal must be sent to turn what is already on the command line into the
/// selected suggestion - or refuses, when the line is not one it can prove the shape of.
/// </summary>
/// <remarks>
/// <para>
/// Public for the same reason as <see cref="CommandAssistKeyRouter"/>: the App's
/// <c>TerminalPane</c> calls it when accepting a suggestion, and it is a pure static function over
/// values, so exposing it lets this assembly avoid granting the App <c>InternalsVisibleTo</c>.
/// </para>
/// <para>
/// <strong>Additive everywhere except explicit history search.</strong> The rule for most of this
/// feature's life was "send only the characters the suggestion adds" - Command Assist never deletes
/// what the user typed, never moves the cursor, never rewrites the line - and that is still the rule
/// for the passive typing bubble, for an explicit Suggest session, and for the direct insert chord.
/// <see cref="CommandAssistInsertionStyle.ReplaceTypedPrefix"/> carves out one surface:
/// <c>Ctrl+R</c> history search, where the characters on the line are a filter over a list rather
/// than the start of a command, and accepting a row means running <em>that row</em>. Everywhere else
/// the style is <see cref="CommandAssistInsertionStyle.Append"/> and the arithmetic is bit-identical
/// to what it always was.
/// </para>
/// <para>
/// <strong>Why the carve-out was necessary rather than nice.</strong> <c>JsonlHistoryStore</c>
/// filters by <em>subsequence</em>, so a non-prefix row is the ordinary case in a filtered history
/// list, not an edge case: type <c>git</c>, select <c>echo git-alpha</c>, press <c>Enter</c>, and the
/// additive rule refuses - the key falls through to the shell, and the shell runs <c>git</c>. The
/// user asked for one command and got a different one. Additive's promise ("we never damage the
/// line") is worth a refusal when the alternative is a wrong edit; it is not worth a refusal when the
/// alternative is the user's own explicit selection.
/// </para>
/// <para>
/// <strong>Refusal is still a feature, and the refusals that remain are the ones that protect the
/// line.</strong> Replace relaxes exactly one condition - the row no longer has to start with the
/// query. Everything else is untouched, because every other refusal is about whether the planner can
/// <em>see</em> the line rather than about whether the row extends it:
/// </para>
/// <list type="bullet">
/// <item>no grid truth at all (a markless session, a closed lifecycle gate) - nothing to count;</item>
/// <item>a multiline entry - the text contains continuation-prompt cells the user never typed, so
/// any count taken from it is wrong;</item>
/// <item>a cursor that is neither at the end nor in front of a proven ghost - backspaces delete
/// <em>leftwards</em>, so a replace here would erase the head of the line, leave the tail, and insert
/// the command in front of the survivor;</item>
/// <item>the line already being the selected command - erasing N characters to retype the identical
/// N is pure flicker, and it is precisely the window in which "the deletes land but the insert does
/// not" costs the user their line for no gain.</item>
/// </list>
/// <para>
/// <strong>Replace is not undoable by Command Assist.</strong> There is no inverse operation here and
/// no snapshot kept: once the deletes are on the wire the typed characters are gone as far as this
/// feature is concerned. What the user has is their shell's own undo - <c>Ctrl+_</c> in readline,
/// <c>Ctrl+Z</c> in PSReadLine - and nothing in this code path should be documented or described as
/// though the edit were reversible from our side.
/// </para>
/// <para>
/// <strong>Known limitation, accepted: vi command mode.</strong> With <c>set -o vi</c> (readline/zle)
/// or <c>Set-PSReadLineOption -EditMode Vi</c>, <c>DEL</c> in command mode may move the cursor left
/// rather than delete a character, so a replace would leave the typed characters in place and insert
/// the command after them. This is not detectable from the grid - the mode is invisible to us - and
/// additive insertion is already meaningless in vi command mode (the "typed prefix" is not where the
/// next character would land), so it is recorded here as an accepted risk rather than treated as a
/// blocker.
/// </para>
/// </remarks>
public static class CommandAssistInsertionPlanner
{
    /// <summary>
    /// Computes the deletes-then-text the terminal must be sent so that the command line becomes
    /// <paramref name="selectedCommand"/>, or returns <see langword="false"/> when that cannot be
    /// done safely.
    /// </summary>
    /// <param name="query">
    /// The live command line, or <see langword="null"/> when the session is markless or the shell is
    /// not in its line editor. <see langword="null"/> always refuses, for both styles. Without grid
    /// truth there is no way to know what is already on the line: appending a whole command to an
    /// unknown prefix produces <c>git sgit status</c>, and replacing needs a <em>count</em>, which is
    /// strictly more information than appending needed. Degraded mode does not offer either.
    /// </param>
    /// <param name="selectedCommand">The suggestion the user accepted.</param>
    /// <param name="style">
    /// Whether this accept extends the line or replaces the typed query. Chosen by the caller from
    /// session state - see <c>CommandAssistController.AcceptReplacesTypedQuery</c>.
    /// </param>
    /// <param name="plan">The deletes and text to send; <c>default</c> on refusal.</param>
    /// <remarks>
    /// <para>
    /// <strong>The counting unit, which is the subtle part.</strong>
    /// <see cref="CommandAssistInsertionPlan.BackspaceCount"/> is
    /// <see cref="AssistQuerySnapshot.TypedPrefix"/><c>.Length</c>: UTF-16 code units, taken from the
    /// typed prefix and never from <see cref="AssistQuerySnapshot.Text"/>. <c>Text</c> includes the
    /// shell's inline prediction - typically ten to forty characters the user never typed - so
    /// counting from it would erase most of a line the user had barely started.
    /// </para>
    /// <para>
    /// Code units rather than graphemes, and this is the thing a later reader will want to "fix".
    /// <c>GridQueryReader</c> appends one <em>grapheme</em> per non-wide-continuation cell, so
    /// <c>Text.Length</c> is a code-unit count with no grapheme or cell count carried across the
    /// seam. On the other side, readline, zle and fish each delete one codepoint per backward-delete
    /// and PSReadLine deletes one .NET <c>char</c> (modern versions coalescing surrogate pairs) - all
    /// of which are <em>at most</em> the number of UTF-16 code units. So the count does not have to be
    /// exact: under replace we always delete to the start of the input buffer, backward-delete at
    /// position 0 is a no-op in all four editors, and an overshoot is therefore absorbed by the
    /// start-of-line floor at the cost of a bell. What we need is an upper bound, and code units are
    /// one. (Measured rather than assumed: <c>PtyBackspaceAtLineStartTests</c> types three characters
    /// through the real PTY, waits for the grid to show them, sends <em>five</em> <c>DEL</c> bytes, and
    /// requires the grid to come back to exactly the prompt it started from - so it establishes that
    /// <c>0x7f</c> deletes one character per byte <em>and</em> that the two surplus deletes are
    /// absorbed, and it cannot pass on a session that silently died.)
    /// </para>
    /// <para>
    /// Grapheme counting would be actively wrong, because it errs the other way: <c>e</c> +
    /// <c>U+0301</c> is one grapheme but two backward-deletes in readline, so a grapheme count
    /// <em>under</em>-counts combining sequences and leaves debris on the line - which the inserted
    /// command is then appended to, producing exactly the corruption this whole file exists to
    /// prevent. Undershoot is unrecoverable; overshoot is a bell.
    /// <c>CommandAssistInsertionPlannerTests</c> pins three cases (a combining sequence, two CJK
    /// characters, one non-BMP emoji) so a refactor towards graphemes has to argue with a test rather
    /// than with a comment.
    /// </para>
    /// <para>
    /// The empty-line case used to be an early return of its own ("an empty line is a fact, send the
    /// whole command"). It is gone, because the shared arithmetic already produces exactly that
    /// answer: with a zero-length typed prefix, append sends <c>selectedCommand[0..]</c> with no
    /// deletes and replace sends <c>selectedCommand</c> with zero deletes. Numerically identical, and
    /// a second branch would only be somewhere for a stray backspace to appear.
    /// </para>
    /// <para>
    /// <strong>Rejected alternatives, so they are not re-proposed.</strong> (1) Prepending the
    /// <c>\x7f</c> bytes into the returned string: makes the count untestable, and defeats the pane's
    /// <c>IsNullOrEmpty</c> guard because a string of pure deletes reads as non-empty. (2) A separate
    /// <c>TryCreateReplacement</c>: duplicates ninety percent of the refusal discipline above, which
    /// is the part that must not drift between the two styles. (3) A pane-side erase step before the
    /// accept: reintroduces the accept-before-plan ordering bug PR #294 fixed, where a refusal after
    /// the accept tore the surface down and sent nothing.
    /// </para>
    /// <para>
    /// <strong>(4) Refusing a replace on a soft-wrapped query. Tried, measured, reverted.</strong> The
    /// motivation is real: <c>GridQueryReader.SoftWrappedRowEnd</c> documents a case where a typed
    /// space in the last column before a wide character is dropped, which under-counts by one -
    /// harmless to an append (an under-count could only ever produce a refusal) and corrupting to a
    /// replace (it leaves a character behind for the inserted command to be appended to). The available
    /// signal is not good enough, though. Carrying <c>GridCommandLine.EndRow &gt; StartRow</c> across
    /// the App boundary and refusing on it makes <em>every PSReadLine-rendered prompt</em> refuse,
    /// empty ones included: PSReadLine erases to the right edge and one cell past it, which is a real
    /// autowrap, so the span runs onto a blank continuation row and the bit gets set on a single-row
    /// command line. Measured - <c>PaneAssistInsertionTests.OnAPromptPsReadLineHasRendered_*</c> and
    /// <c>OnAnEmptyPromptPsReadLineHasRendered_EnterSendsTheWholeCommand</c> all fail. That is the same
    /// shape of bug as the <c>RightPromptTrimmed</c> refusal PR #301 removed: a guard that fires on
    /// every prompt a common shell paints, so the feature is simply inert.
    /// </para>
    /// <para>
    /// <strong>What was rejected is the broad signal, not the idea.</strong> The hazard is real and
    /// still open: a query long enough to wrap, whose last column before the seam holds a typed space
    /// followed by a wide character, reads back one character short, and a replace then leaves the
    /// leftmost character on the line - <c>gecho git-alpha</c>. The signal a fix wants is narrower than
    /// the one tried here: a bool raised only when <c>GridQueryReader.SoftWrappedRowEnd</c> actually
    /// returned <c>cols - 1</c> for a row at or before the <em>cursor</em> row. That fires on the
    /// defect and on nothing else, where <c>EndRow &gt; StartRow</c> fires on every PSReadLine repaint.
    /// It needs a reader change, so it is a follow-up rather than part of this change; until then the
    /// hazard is accepted and stated rather than papered over with a guard that disables the feature.
    /// </para>
    /// </remarks>
    public static bool TryCreatePlan(
        AssistQuerySnapshot? query,
        string? selectedCommand,
        CommandAssistInsertionStyle style,
        out CommandAssistInsertionPlan plan)
    {
        plan = default;

        if (string.IsNullOrEmpty(selectedCommand))
        {
            return false;
        }

        // A row carrying a line break would be *submitted* rather than inserted - SendInput writes
        // raw bytes with no bracketed-paste wrapping on this path - and under replace it would be
        // submitted against a line we have just erased. Cheap, and it fails closed for both styles
        // rather than leaving one of them holding a sharper edge than the other.
        if (selectedCommand.IndexOf('\n') >= 0 || selectedCommand.IndexOf('\r') >= 0)
        {
            return false;
        }

        if (query is not AssistQuerySnapshot line)
        {
            return false;
        }

        // The two ways a snapshot fails to be a typed prefix - cursor mid-line, multiline entry - are
        // spelled out on AssistQuerySnapshot.IsUsableAsTypedPrefix. Each breaks both styles, in its
        // own way: the cursor decides where sent text lands *and* which characters the deletes eat,
        // and a continuation prompt is text in the snapshot the user never typed, so no count taken
        // from it is right.
        if (!line.IsUsableAsTypedPrefix)
        {
            return false;
        }

        // TypedPrefix, not Text: on a line whose tail the reader classified as an inline prediction the
        // two differ, and measuring against Text would compute the delta against the shell's guess -
        // and, under replace, would count the shell's guess into the number of characters to erase.
        // The ghost is display-only; the typed characters are the line.
        string typed = line.TypedPrefix;
        bool replaces = style == CommandAssistInsertionStyle.ReplaceTypedPrefix;

        // The one condition replace relaxes. Append must refuse a row that does not extend the line,
        // because the only edit it is allowed to make is at the end; replace is not making that edit.
        if (!replaces && !selectedCommand.StartsWith(typed, StringComparison.Ordinal))
        {
            return false;
        }

        // Both styles refuse when the line already *is* the command. For append that is the old
        // "same length as a proven prefix, so the suffix is empty" test, restated as what it always
        // meant. For replace it is a real decision rather than a fallout: erasing N characters to
        // retype the identical N is flicker with a failure mode attached, and refusing keeps the
        // invariant that a successful plan is never a no-op.
        if (string.Equals(selectedCommand, typed, StringComparison.Ordinal))
        {
            return false;
        }

        // One arithmetic for both styles. Uniform in the replace case *including* when the row does
        // happen to start with the query: no "optimise back to append when it is a prefix" branch.
        // Three reasons. One behaviour per surface, so the user does not have to know which. The
        // bytes on the wire must not vary with which row happens to be highlighted. And the
        // optimisation would make the delete path dead code on the *common* Ctrl+R accept - type
        // `git`, take `git status` - leaving it live only on the rarer rows; a path skipped in the
        // common case is a path that rots.
        int backspaceCount = replaces ? typed.Length : 0;
        string textToSend = replaces ? selectedCommand : selectedCommand[typed.Length..];

        plan = new CommandAssistInsertionPlan(backspaceCount, textToSend);
        return true;
    }

    /// <summary>
    /// The additive-only entry point: computes the suffix to append, or returns
    /// <see langword="false"/> if the line cannot be extended into
    /// <paramref name="selectedCommand"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A public, test-only shim.</strong> There are no production callers left: the pane calls
    /// <see cref="TryCreatePlan"/> directly, because it has to choose a style. This is kept anyway, and
    /// kept public, because it is the pin - a signature that <em>cannot</em> express an erase, which
    /// every additive test written before the replace style still goes through unchanged. That is what
    /// makes "additive is untouched" a fact the suite checks rather than a claim a reviewer has to
    /// verify by reading the diff. Deleting it under the dead-code rule would delete the evidence, not
    /// the dead code.
    /// </para>
    /// </remarks>
    /// <param name="query">See <see cref="TryCreatePlan"/>.</param>
    /// <param name="selectedCommand">The suggestion the user accepted.</param>
    /// <param name="textToSend">The suffix to send; <see langword="null"/> on refusal.</param>
    public static bool TryCreateInsertion(
        AssistQuerySnapshot? query,
        string? selectedCommand,
        out string? textToSend)
    {
        if (!TryCreatePlan(query, selectedCommand, CommandAssistInsertionStyle.Append, out CommandAssistInsertionPlan plan))
        {
            textToSend = null;
            return false;
        }

        Debug.Assert(
            plan.BackspaceCount == 0,
            "An Append plan must never erase; the whole point of this overload is that it cannot.");

        textToSend = plan.TextToSend;
        return true;
    }
}

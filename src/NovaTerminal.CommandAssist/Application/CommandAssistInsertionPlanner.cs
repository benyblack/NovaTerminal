namespace NovaTerminal.CommandAssist.Application;

/// <summary>
/// Works out the delta the terminal must be sent to turn what is already on the command line into
/// the selected suggestion - or refuses, when the line is not one a suffix can safely be appended
/// to.
/// </summary>
/// <remarks>
/// <para>
/// Public for the same reason as <see cref="CommandAssistKeyRouter"/>: the App's
/// <c>TerminalPane</c> calls it when accepting a suggestion, and it is a pure static function over
/// values, so exposing it lets this assembly avoid granting the App <c>InternalsVisibleTo</c>.
/// </para>
/// <para>
/// <strong>Insertion stays additive (Phase 1c).</strong> The rule has always been "send only the
/// characters the suggestion adds" - Command Assist never deletes what the user typed, never moves
/// the cursor, never rewrites the line. What changed is what the rule is computed against: V1 used
/// the keystroke mirror, so every edit it could not see produced a delta against a line that did not
/// exist. Now the input is an <see cref="AssistQuerySnapshot"/> read out of the grid, and the cases
/// the mirror got wrong are the cases this refuses.
/// </para>
/// <para>
/// <strong>Refusal is a feature.</strong> A refused insertion costs the user one keystroke of
/// convenience. A wrong one edits their command line - the thing they are about to run. Every
/// condition below resolves that asymmetry the same way.
/// </para>
/// </remarks>
public static class CommandAssistInsertionPlanner
{
    /// <summary>
    /// Computes the text to send so that the command line becomes <paramref name="selectedCommand"/>,
    /// or returns <see langword="false"/> if that cannot be done by appending.
    /// </summary>
    /// <param name="query">
    /// The live command line, or <see langword="null"/> when the session is markless or the shell is
    /// not in its line editor. <see langword="null"/> always refuses: without grid truth there is no
    /// way to know what is already on the line, and appending a whole command to an unknown prefix
    /// produces <c>git sgit status</c>. Insertion is a prefix-dependent feature, and degraded mode
    /// does not offer those.
    /// </param>
    /// <param name="selectedCommand">The suggestion the user accepted.</param>
    /// <param name="textToSend">The suffix to send; <see langword="null"/> on refusal.</param>
    public static bool TryCreateInsertion(
        AssistQuerySnapshot? query,
        string? selectedCommand,
        out string? textToSend)
    {
        textToSend = null;

        if (string.IsNullOrEmpty(selectedCommand))
        {
            return false;
        }

        if (query is not AssistQuerySnapshot line)
        {
            return false;
        }

        // The three ways a snapshot fails to be a typed prefix - cursor mid-line, multiline entry,
        // trimmed right prompt - are spelled out on AssistQuerySnapshot.IsUsableAsTypedPrefix. Each
        // one breaks the append in its own way: the cursor decides where sent text lands, a
        // continuation prompt is text in the snapshot the user never typed, and a trimmed right
        // prompt means the tail of the line is the reader's inference rather than an observation.
        if (!line.IsUsableAsTypedPrefix)
        {
            return false;
        }

        // TypedPrefix, not Text: on a line whose tail the reader classified as an inline prediction the
        // two differ, and measuring the delta against Text would compute it against the shell's guess -
        // sending nothing when the prediction happens to match the suggestion, and refusing whenever it
        // does not. The ghost is display-only; the typed characters are the line.
        string text = line.TypedPrefix;
        if (text.Length == 0)
        {
            // An empty line is a fact here, not an absence of one: the grid was read and the line
            // is empty. Sending the whole command is exactly right.
            textToSend = selectedCommand;
            return true;
        }

        if (!selectedCommand.StartsWith(text, System.StringComparison.Ordinal))
        {
            return false;
        }

        if (selectedCommand.Length == text.Length)
        {
            return false;
        }

        textToSend = selectedCommand[text.Length..];
        return true;
    }
}

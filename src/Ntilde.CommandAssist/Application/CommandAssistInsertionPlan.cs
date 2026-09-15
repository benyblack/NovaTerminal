namespace NovaTerminal.CommandAssist.Application;

/// <summary>
/// How an accepted row is turned into bytes: extend what the user typed, or throw it away and
/// send the row whole.
/// </summary>
/// <remarks>
/// <para>
/// The style is a property of the <em>surface the accept came from</em>, not of the row and not
/// of the line. <see cref="Append"/> is the rule everywhere Command Assist offers a completion -
/// the passive typing bubble, the explicit Suggest session, the direct insert chord - and it is
/// the rule that lets the feature never destroy anything the user typed.
/// <see cref="ReplaceTypedPrefix"/> exists for exactly one surface: explicit history search, where
/// the typed characters are a <em>filter over the list</em> rather than the start of the command,
/// and every reverse-search and fuzzy finder in existence resolves the accept the same way.
/// </para>
/// <para>
/// Deciding it at the call site rather than inside the planner is deliberate. The planner is a pure
/// function over a snapshot and a row; "which surface is the user in" is session state it has no
/// access to and should not grow a dependency on. See
/// <c>CommandAssistController.AcceptReplacesTypedQuery</c> for the one place that answers it.
/// </para>
/// </remarks>
public enum CommandAssistInsertionStyle
{
    /// <summary>
    /// Send only the characters the row adds to what is already on the line, and refuse whenever
    /// the row is not an extension of it. Command Assist deletes nothing.
    /// </summary>
    Append,

    /// <summary>
    /// Erase the typed prefix and send the row whole. Accepting any row means running that row -
    /// fzf semantics - so a row that shares no prefix with the query is accepted rather than
    /// refused.
    /// </summary>
    ReplaceTypedPrefix
}

/// <summary>
/// What the terminal must be sent to turn the current command line into the accepted row: some
/// number of backward deletes, then some text.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Invariant: a plan returned with <see langword="true"/> is never <c>(0, "")</c>.</strong>
/// A successful plan always changes the line. The planner enforces it by refusing the two shapes
/// that could produce a no-op - an empty row and a line that already equals the row - and the pane
/// re-checks it before sending, because "we planned successfully and sent nothing" is
/// indistinguishable to the user from the feature being broken, and that indistinguishability is
/// what PR #294 was about.
/// </para>
/// <para>
/// <strong><see cref="BackspaceCount"/> is a count of UTF-16 code units, not of graphemes and not
/// of grid cells.</strong> The full argument lives on
/// <see cref="CommandAssistInsertionPlanner.TryCreatePlan"/>, because that is where the count is
/// produced and where someone will be tempted to "fix" it.
/// </para>
/// <para>
/// The two halves are kept apart rather than pre-concatenated into one string so the count stays
/// assertable. A planner that returned <c>"\x7f\x7f\x7fecho hi"</c> would make every test about the
/// count a test about string prefixes, and would defeat the pane's own emptiness guard - the
/// rejected alternative recorded on the planner.
/// </para>
/// </remarks>
/// <param name="BackspaceCount">
/// How many <c>DEL</c> (<c>0x7f</c>) bytes to send before <paramref name="TextToSend"/>. Zero for
/// every <see cref="CommandAssistInsertionStyle.Append"/> plan, and zero for a replace on a line the
/// grid reported as empty.
/// </param>
/// <param name="TextToSend">
/// The characters to send after the deletes. Never <see langword="null"/> on a successful plan.
/// </param>
public readonly record struct CommandAssistInsertionPlan(int BackspaceCount, string TextToSend);

namespace NovaTerminal.CommandAssist.Domain;

/// <summary>
/// The full-help command a probe found for a token, ready to be offered as an insertable row.
/// </summary>
/// <param name="Command">
/// What to put on the command line - <c>Get-Help ssh</c>, <c>man tar</c>, <c>kubectl --help</c>.
/// </param>
/// <param name="Description">Why this row is here, in one line, for the row's detail text.</param>
public readonly record struct CommandHelpProbeResult(string Command, string Description);

/// <summary>
/// Asks the host whether full help for a command plausibly exists on this machine, so the Help
/// surface can offer an "open full help" row (V2 Phase 4b, Phase 4 task 3, source (b)).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is a seam and not a class in this assembly.</strong> Answering the question
/// means looking at <c>PATH</c>, at the filesystem, and at which shell the pane is running - all of
/// which are host facts. The assist assembly's contract is that it computes on what it is told, and
/// it is the same reason <c>AssistQuerySnapshot</c> arrives through a delegate rather than being
/// read from the terminal buffer here. The App supplies <c>LocalCommandHelpProbe</c>; a test
/// supplies a fake and gets a deterministic answer with no machine in the loop.
/// </para>
/// <para>
/// <strong>Existence, never execution.</strong> An implementation may look for an executable on
/// <c>PATH</c> or a man page on <c>MANPATH</c>. It must not spawn <c>man</c>, <c>--help</c> or
/// anything else: the answer is wanted while the user is looking at a popup, a spawned process on
/// a slow filesystem or an unreachable network drive would block that, and "would this work?" does
/// not require running it. Implementations are expected to cache per token.
/// </para>
/// <para>
/// <strong>Synchronous on purpose.</strong> The one caller (<see cref="CommandKnowledgeService"/>'s
/// recipe path) is already inside an async method on a worker; an existence check that is allowed to
/// take long enough to need its own <c>Task</c> is an implementation that has broken the rule above.
/// </para>
/// </remarks>
public interface ICommandHelpProbe
{
    /// <summary>
    /// Returns how to open full help for <paramref name="commandToken"/> under
    /// <paramref name="shellKind"/>, or <see langword="null"/> when nothing plausible was found.
    /// </summary>
    /// <param name="commandToken">
    /// The command as typed. May be a two-token form (<c>git rebase</c>); an implementation that
    /// only understands executables should probe the first token and describe the whole.
    /// </param>
    /// <param name="shellKind">
    /// <c>pwsh</c>, <c>bash</c>, <c>zsh</c>, <c>fish</c>, <c>cmd</c> or <see langword="null"/> when
    /// the session's shell is unknown.
    /// </param>
    CommandHelpProbeResult? Probe(string commandToken, string? shellKind);
}

using System;

namespace NovaTerminal.CommandAssist.Providers;

/// <summary>
/// What an <see cref="IAssistContentProvider"/> is able to answer. A provider advertises the union
/// of what it can do; a request asks for exactly one.
/// </summary>
/// <remarks>
/// <para>
/// The four members are the design doc's (Pillar 6) and are deliberately kept even though only two
/// have providers today. They are not aspirational padding: each names a distinct <em>question</em>,
/// and the registry's "is anything configured for this" answer is per-question. Inventing them later
/// would mean renumbering a flags enum that had already been persisted in settings.
/// </para>
/// <list type="bullet">
/// <item><see cref="Explain"/> - "what does this do?" over a selection or a command line, answered as
/// prose. No provider claims it today: the Explain entry point routes to the Help path, which is an
/// <see cref="EnrichDocs"/> question, and a local catalogue cannot explain an arbitrary pipeline.</item>
/// <item><see cref="SuggestFix"/> - "this exited non-zero, what now?". Served by the local error
/// heuristics.</item>
/// <item><see cref="NlToCommand"/> - "turn this English sentence into a command". Has no provider and
/// <strong>no user-reachable entry point</strong>; see <see cref="AssistEmptyStates"/>.</item>
/// <item><see cref="EnrichDocs"/> - "what is this command and how is it used?". Served by the bundled
/// tldr-derived catalogue plus the local help probe.</item>
/// </list>
/// </remarks>
[Flags]
public enum AssistCapabilities
{
    /// <summary>Answers nothing. The identity for a composed capability set.</summary>
    None = 0,

    /// <summary>Prose explanation of a command line or a selection.</summary>
    Explain = 1 << 0,

    /// <summary>A candidate fix for a command that failed.</summary>
    SuggestFix = 1 << 1,

    /// <summary>A command derived from a natural-language description.</summary>
    NlToCommand = 1 << 2,

    /// <summary>Documentation and example invocations for a command.</summary>
    EnrichDocs = 1 << 3
}

using System.Collections.Generic;

namespace NovaTerminal.CommandAssist.Models;

/// <param name="RecognizerId">
/// Which entry of <c>CommandErrorRecognizers.All</c> produced this, or <see langword="null"/> when it
/// came from the service's uninformed-guess fallback instead of from the table.
/// <para>
/// <strong>Two consumers, both added in the UX-polish round.</strong> The noise floor in
/// <c>CommandAssistModeRouter.ShouldSurfacePassiveFix</c> needs to tell "a recogniser read the output
/// and understood it" from "nothing matched, so here is a name-similarity guess" - a distinction
/// confidence alone cannot express, because the no-output fallback is priced at 0.7 and would clear
/// any threshold low enough to admit a real explanation. And the typo suppression needs to know
/// specifically that the failure was <c>command-not-found</c>, which is the one classification that
/// says the history entry is a misspelling rather than a command.
/// </para>
/// <para>
/// Stamped centrally in <c>HeuristicErrorInsightService</c> rather than by each recogniser, so a new
/// table entry cannot forget to set it.
/// </para>
/// </param>
/// <param name="UnresolvedCommandToken">
/// The name the shell could not resolve, but <em>only</em> when that name is the command line's own
/// first token. <see langword="null"/> otherwise, including for every recogniser that is not
/// command-not-found.
/// <para>
/// <strong>Dogfood round 4, item 4a.</strong> It exists to carry one fact to
/// <c>IHistoryStore.TryMarkInvalidCommandsByFirstTokenAsync</c>, which flags every older history entry
/// beginning with the same word. The "only when it is the first token" restriction is the whole safety
/// argument: <c>npm run build</c> can raise a command-not-found for a token that is not <c>npm</c>, and
/// propagating <em>that</em> by first token would suppress every <c>npm</c> line the user has ever run.
/// The recogniser already computes the distinction (it needs it to decide whether the fix rewrites the
/// command or replaces a word inside it); this publishes it rather than recomputing it downstream from
/// less information.
/// </para>
/// </param>
public sealed record CommandFixSuggestion(
    string Title,
    string SuggestedCommand,
    string? Description,
    double Confidence,
    IReadOnlyList<string>? Badges = null,
    string? RecognizerId = null,
    string? UnresolvedCommandToken = null)
{
    /// <summary>
    /// Whether a recogniser in the table stood behind this, as opposed to the fallback guess.
    /// </summary>
    public bool IsRecognized => !string.IsNullOrEmpty(RecognizerId);

    /// <summary>The recogniser id for an unresolved program name; see <see cref="RecognizerId"/>.</summary>
    public const string CommandNotFoundRecognizerId = "command-not-found";

    /// <summary>Whether this fix says the shell could not resolve the command's name.</summary>
    public bool IsCommandNotFound =>
        string.Equals(RecognizerId, CommandNotFoundRecognizerId, System.StringComparison.Ordinal);
}

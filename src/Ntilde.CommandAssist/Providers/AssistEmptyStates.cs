namespace NovaTerminal.CommandAssist.Providers;

/// <summary>
/// The strings a helper surface shows when there is nothing to show, split by <em>why</em>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>"We looked and found nothing" and "nobody is configured to look" are different
/// sentences.</strong> The first is a fact about the user's command; the second is a fact about the
/// app's configuration, and telling a user "No local help found." when the reason is that no help
/// provider exists sends them looking for a better command instead of at Settings. The registry can
/// tell the two apart (<see cref="AssistContentProviderRegistry.HasProviderFor"/>), so the surface
/// does too.
/// </para>
/// <para>
/// <strong>Honesty about reach.</strong> Every capability the shipped UI can ask for -
/// <see cref="AssistCapabilities.EnrichDocs"/> from Help, <see cref="AssistCapabilities.SuggestFix"/>
/// from a failed command - has a local provider registered at the App composition root, so in the
/// shipped app the "not configured" strings are unreachable: a user cannot turn a local provider off,
/// and there is nothing else to configure. They are reachable, and tested, for a controller composed
/// without those providers (a host embedding the assist assembly, the MCP surface, a test).
/// </para>
/// <para>
/// <strong><see cref="AssistCapabilities.NlToCommand"/> has no entry point and none was
/// invented.</strong> The design doc's "AI assist not configured" empty state belongs to a Mode E /
/// Ask-AI affordance that V2 does not ship; adding a button that can only ever say "not configured"
/// would be shipping a dead end and calling it a feature. The string exists so that the milestone
/// which adds the entry point has an answer ready instead of a blank popup, and it is exercised by
/// unit test rather than by UI.
/// </para>
/// </remarks>
public static class AssistEmptyStates
{
    /// <summary>Help was asked and the catalogue and probe had nothing. The pre-Phase-5 string.</summary>
    public const string NoLocalHelp = "No local help found.";

    /// <summary>A command failed and no recogniser matched it. The pre-Phase-5 string.</summary>
    public const string NoLocalFix = "No likely local fix found.";

    /// <summary>
    /// History search was filtered down to nothing by the command line the user is typing.
    /// </summary>
    /// <remarks>
    /// The one entry here that belongs to the ranking pass rather than to a content provider, and it
    /// only became reachable when typing started filtering the <c>Ctrl+R</c> list instead of closing
    /// it. Without it, narrowing past the last match leaves an open popup containing nothing, which
    /// reads as the surface having broken rather than as the search having no answer. It is kept
    /// beside the others because it answers the same question they do - what does a surface say when
    /// it has nothing to show - and splitting one string out would cost a second home for it.
    /// </remarks>
    public const string NoHistoryMatch = "No matching commands in history.";

    /// <summary>Nothing is registered to answer documentation questions.</summary>
    public const string NoHelpProvider = "No help provider is configured.";

    /// <summary>Nothing is registered to answer "why did this fail".</summary>
    public const string NoFixProvider = "No fix provider is configured.";

    /// <summary>
    /// Nothing is registered for a capability only an AI provider could serve. The design doc's
    /// string; see the type remarks for why nothing in the shipped UI can reach it yet.
    /// </summary>
    public const string AiNotConfigured = "AI assist is not configured.";

    /// <summary>
    /// The honest sentence for "<paramref name="capability"/> has no enabled provider".
    /// </summary>
    public static string ForMissingProvider(AssistCapabilities capability) => capability switch
    {
        AssistCapabilities.EnrichDocs => NoHelpProvider,
        AssistCapabilities.SuggestFix => NoFixProvider,
        _ => AiNotConfigured
    };
}

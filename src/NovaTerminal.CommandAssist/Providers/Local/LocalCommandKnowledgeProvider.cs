using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Providers.Local;

/// <summary>
/// Serves <see cref="AssistCapabilities.EnrichDocs"/> from the bundled command-knowledge catalogue -
/// the 585-command tldr-derived asset V2 Phase 4b shipped, plus the local help probe.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two interfaces behind one provider, because Help asks one question.</strong>
/// <see cref="CommandKnowledgeService"/> implements both <see cref="ICommandDocsProvider"/> and
/// <see cref="IRecipeProvider"/> over one parsed catalogue, and the Help path has always asked both
/// in the same breath. Registering them as two providers would have doubled the request count and
/// invited a future composition where the doc rows and the example rows come from different
/// catalogues - which is not a thing anyone wants and not a thing the popup could label.
/// </para>
/// <para>
/// <strong>Attribution is read after the awaits, and that ordering is load-bearing.</strong> The
/// catalogue's licence line is a field inside the asset, so it exists only once the asset has been
/// parsed - and the two calls below are what parse it. Reading it before them yields null and the
/// CC BY-SA credit silently disappears from the popup footer. Asked through an optional interface
/// (<see cref="ICommandKnowledgeAttributionSource"/>) so that a source with nothing to credit simply
/// does not implement it.
/// </para>
/// <para>
/// Either interface may be absent - a host that wants docs without examples, or a test. Absent both
/// is a caller error: register no provider instead, which is what makes the empty state honest.
/// </para>
/// </remarks>
public sealed class LocalCommandKnowledgeProvider : IAssistContentProvider
{
    /// <summary>The persisted provider id. Part of the settings contract; see <see cref="AssistProviderPolicy"/>.</summary>
    public const string ProviderId = "local.command-knowledge";

    private readonly ICommandDocsProvider? _docsProvider;
    private readonly IRecipeProvider? _recipeProvider;

    public LocalCommandKnowledgeProvider(
        ICommandDocsProvider? docsProvider,
        IRecipeProvider? recipeProvider)
    {
        if (docsProvider == null && recipeProvider == null)
        {
            throw new ArgumentNullException(
                nameof(docsProvider),
                "A knowledge provider with neither a docs source nor a recipe source can only ever " +
                "return nothing, which the empty state should say instead of pretending to look.");
        }

        _docsProvider = docsProvider;
        _recipeProvider = recipeProvider;
    }

    /// <inheritdoc/>
    public string Id => ProviderId;

    /// <inheritdoc/>
    public string DisplayName => "Bundled command knowledge";

    /// <inheritdoc/>
    public AssistCapabilities Capabilities => AssistCapabilities.EnrichDocs;

    /// <inheritdoc/>
    /// <remarks>
    /// Answers from an embedded asset and from existence checks against <c>PATH</c>/<c>MANPATH</c>.
    /// It never spawns a process and never opens a socket, so there is nothing to opt into.
    /// </remarks>
    public bool RequiresExplicitOptIn => false;

    /// <inheritdoc/>
    public async Task<AssistContentResult> QueryAsync(
        AssistContentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if ((request.Capability & AssistCapabilities.EnrichDocs) == AssistCapabilities.None)
        {
            return AssistContentResult.Empty(ProviderId, request.Capability);
        }

        var query = new CommandHelpQuery(
            RawInput: request.CommandText.Value,
            CommandToken: request.CommandToken?.Value,
            ShellKind: request.ShellKind,
            WorkingDirectory: request.WorkingDirectory?.Value,
            SelectedText: request.SelectedText?.Value,
            SessionId: request.SessionId);

        IReadOnlyList<CommandHelpItem> docs = _docsProvider == null
            ? Array.Empty<CommandHelpItem>()
            : await _docsProvider.GetHelpAsync(query, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<CommandHelpItem> recipes = _recipeProvider == null
            ? Array.Empty<CommandHelpItem>()
            : await _recipeProvider.GetRecipesAsync(query, cancellationToken).ConfigureAwait(false);

        // After the awaits. See the type remarks.
        string? attribution = (_docsProvider as ICommandKnowledgeAttributionSource)?.Attribution
                              ?? (_recipeProvider as ICommandKnowledgeAttributionSource)?.Attribution;

        return new AssistContentResult(
            ProviderId,
            AssistCapabilities.EnrichDocs,
            docs: docs,
            recipes: recipes,
            attribution: attribution);
    }
}

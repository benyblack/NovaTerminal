using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Providers.Local;

/// <summary>
/// Serves <see cref="AssistCapabilities.SuggestFix"/> from <see cref="IErrorInsightService"/> - the
/// fifteen local failure recognisers V2 Phase 4a shipped.
/// </summary>
/// <remarks>
/// <para>
/// <strong>An adapter, not a rewrite.</strong> <see cref="HeuristicErrorInsightService"/> and its
/// recogniser table were built in Phase 4a against <see cref="IErrorInsightService"/> and are covered
/// by a large sample-driven test suite. Re-basing them on <see cref="IAssistContentProvider"/>
/// directly would have churned tested code to no end: the seam's job is to make the orchestrator's
/// path uniform, and thirty lines of translation here achieve that without touching a recogniser. The
/// service interface stays public and stays the thing a fix heuristic implements.
/// </para>
/// <para>
/// The translation reconstructs a <see cref="CommandFailureContext"/> out of the request - which is
/// worth reading as the seam demonstrating its own guarantee. The context the recognisers get is
/// built from redacted text only; there is no path back to the raw grid from here, and if a future
/// recogniser needs something the request does not carry, adding it to the request is the visible,
/// reviewable change that has to happen first.
/// </para>
/// </remarks>
public sealed class LocalErrorInsightProvider : IAssistContentProvider
{
    /// <summary>The persisted provider id. Part of the settings contract; see <see cref="AssistProviderPolicy"/>.</summary>
    public const string ProviderId = "local.error-heuristics";

    private readonly IErrorInsightService _errorInsightService;

    public LocalErrorInsightProvider(IErrorInsightService errorInsightService)
    {
        ArgumentNullException.ThrowIfNull(errorInsightService);
        _errorInsightService = errorInsightService;
    }

    /// <inheritdoc/>
    public string Id => ProviderId;

    /// <inheritdoc/>
    public string DisplayName => "Local error heuristics";

    /// <inheritdoc/>
    public AssistCapabilities Capabilities => AssistCapabilities.SuggestFix;

    /// <inheritdoc/>
    /// <remarks>Runs entirely in-process over an embedded pattern table. Nothing to opt into.</remarks>
    public bool RequiresExplicitOptIn => false;

    /// <inheritdoc/>
    public async Task<AssistContentResult> QueryAsync(
        AssistContentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if ((request.Capability & AssistCapabilities.SuggestFix) == AssistCapabilities.None)
        {
            return AssistContentResult.Empty(ProviderId, request.Capability);
        }

        var context = new CommandFailureContext(
            CommandText: request.CommandText.Value,
            ExitCode: request.ExitCode,
            ShellKind: request.ShellKind,
            WorkingDirectory: request.WorkingDirectory?.Value,
            OutputTail: request.OutputTail?.Value,
            IsRemote: request.IsRemote,
            SelectedText: request.SelectedText?.Value);

        IReadOnlyList<CommandFixSuggestion> fixes =
            await _errorInsightService.AnalyzeAsync(context, cancellationToken).ConfigureAwait(false);

        return new AssistContentResult(ProviderId, AssistCapabilities.SuggestFix, fixes: fixes);
    }
}

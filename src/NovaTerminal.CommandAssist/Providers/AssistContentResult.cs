using System;
using System.Collections.Generic;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Providers;

/// <summary>
/// One provider's answer to one <see cref="AssistContentRequest"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three lists rather than one, because the surface renders them differently.</strong>
/// <see cref="Docs"/> become <c>Doc</c> rows (a summary of what a command is),
/// <see cref="Recipes"/> become <c>Recipe</c> rows (insertable example invocations) and
/// <see cref="Fixes"/> become <c>Fix</c> rows carrying a confidence that decides whether the popup
/// opens at all. Flattening them into one list would have made the seam lossy against the surface
/// that already existed, which is the opposite of "zero behavior change".
/// </para>
/// <para>
/// A provider fills only the lists its capability covers and leaves the rest empty. The registry does
/// not police that: a provider returning a doc row for a <see cref="AssistCapabilities.SuggestFix"/>
/// request is answering a question it was not asked, which is a bug in the provider, but it is not a
/// safety problem and silently dropping content is a worse failure to debug.
/// </para>
/// </remarks>
public sealed record AssistContentResult
{
    /// <param name="providerId">The answering provider's <see cref="IAssistContentProvider.Id"/>.</param>
    /// <param name="capability">The capability this result answers.</param>
    /// <param name="fixes">Candidate fixes, for <see cref="AssistCapabilities.SuggestFix"/>.</param>
    /// <param name="docs">Documentation rows, for <see cref="AssistCapabilities.EnrichDocs"/>.</param>
    /// <param name="recipes">Example invocations, for <see cref="AssistCapabilities.EnrichDocs"/>.</param>
    /// <param name="attribution">
    /// A licence or credit line that must be displayed wherever this result's content is. Rendered in
    /// the Help popup footer under exactly the rows it covers.
    /// </param>
    public AssistContentResult(
        string providerId,
        AssistCapabilities capability,
        IReadOnlyList<CommandFixSuggestion>? fixes = null,
        IReadOnlyList<CommandHelpItem>? docs = null,
        IReadOnlyList<CommandHelpItem>? recipes = null,
        string? attribution = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        ProviderId = providerId;
        Capability = capability;
        Fixes = fixes ?? Array.Empty<CommandFixSuggestion>();
        Docs = docs ?? Array.Empty<CommandHelpItem>();
        Recipes = recipes ?? Array.Empty<CommandHelpItem>();
        Attribution = attribution;
    }

    /// <summary>The answering provider's id.</summary>
    public string ProviderId { get; }

    /// <summary>The capability answered.</summary>
    public AssistCapabilities Capability { get; }

    /// <summary>Candidate fixes for a failed command. Never <see langword="null"/>.</summary>
    public IReadOnlyList<CommandFixSuggestion> Fixes { get; }

    /// <summary>Documentation rows. Never <see langword="null"/>.</summary>
    public IReadOnlyList<CommandHelpItem> Docs { get; }

    /// <summary>Example-invocation rows. Never <see langword="null"/>.</summary>
    public IReadOnlyList<CommandHelpItem> Recipes { get; }

    /// <summary>A credit line to render under this result's rows, or <see langword="null"/>.</summary>
    public string? Attribution { get; }

    /// <summary>Whether the provider produced no rows of any kind.</summary>
    public bool IsEmpty => Fixes.Count == 0 && Docs.Count == 0 && Recipes.Count == 0;

    /// <summary>A no-content answer. What a provider returns when it was asked the wrong question.</summary>
    public static AssistContentResult Empty(string providerId, AssistCapabilities capability)
        => new(providerId, capability);
}

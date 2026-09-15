using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.CommandAssist.Providers;

/// <summary>
/// A source of assist content. The one interface the orchestrator asks for Help rows and Fix rows,
/// implemented today by two thin adapters over the local heuristics and intended tomorrow by an AI
/// provider that has not been designed yet.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the whole of V2's AI work, and shipping only the seam is the point.</strong> The
/// V1 assist grew an AI-shaped hole that nothing filled and several things assumed; V2 instead makes
/// the local heuristics travel the exact path a remote provider would, so the path is exercised on
/// every Help and every Fix from the day it lands rather than on the day a provider first ships. What
/// is deliberately absent: any network call, any API client, any credential handling, any model
/// selection. Those are a separate milestone with their own design.
/// </para>
/// <para>
/// <strong>Implementations must treat <see cref="AssistContentRequest"/> as the complete input.</strong>
/// A provider that reads the filesystem, the environment or the terminal grid for extra context has
/// stepped around the redaction guarantee: the request is the audited surface, and anything a
/// provider adds to it was never filtered. The two local providers honour this - the command
/// catalogue is an embedded asset and the help probe checks <c>PATH</c> for existence only.
/// </para>
/// </remarks>
public interface IAssistContentProvider
{
    /// <summary>
    /// A stable, machine-readable identifier (<c>local.command-knowledge</c>). This is what a settings
    /// opt-in names, so it is part of the persisted contract and may not change casually.
    /// </summary>
    string Id { get; }

    /// <summary>A human-readable name, for a future Settings list.</summary>
    string DisplayName { get; }

    /// <summary>The union of questions this provider can answer.</summary>
    AssistCapabilities Capabilities { get; }

    /// <summary>
    /// Whether the user must name this provider in settings before it will be queried at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is where "providers are opt-in per capability" actually lives.</strong> The design
    /// doc phrases the opt-in as a settings surface; expressing it as an obligation on the provider is
    /// stronger, because a provider that leaves the machine declares that fact in its own type and the
    /// registry refuses to query it until the policy names it. A settings key alone would have been a
    /// gate that a provider registered on the wrong code path could simply walk around.
    /// </para>
    /// <para>
    /// <see langword="false"/> for anything that answers from bundled assets and local state - those
    /// are the feature, not an add-on, and a toggle whose only effect is to break Help is the phantom
    /// flag V2 Phase 3b deleted. <see langword="true"/> for anything that sends the request off this
    /// machine.
    /// </para>
    /// </remarks>
    bool RequiresExplicitOptIn { get; }

    /// <summary>
    /// Answers <paramref name="request"/>, or returns an empty result when it has nothing to say.
    /// </summary>
    /// <remarks>
    /// Returning <see cref="AssistContentResult.Empty"/> is the normal "no answer" path; throwing is
    /// for genuine faults, and <see cref="AssistContentProviderRegistry"/> contains a throw to the one
    /// provider rather than letting it take the surface down.
    /// </remarks>
    Task<AssistContentResult> QueryAsync(AssistContentRequest request, CancellationToken cancellationToken = default);
}

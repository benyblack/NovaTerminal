using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.CommandAssist.Providers;

/// <summary>
/// The ordered set of content providers, and the one thing the controller queries. Composite plus
/// registry: it decides who may answer a request and merges nothing - it returns each answer as its
/// own result, in provider order.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Sequential, in registration order.</strong> Not because concurrency is hard here, but
/// because order is content policy: the bundled catalogue answers before anything else would, so a
/// user with a future AI provider configured still sees the offline rows first and sees them at
/// local latency. Parallelism would trade that for a wall-clock saving the local providers do not
/// need.
/// </para>
/// <para>
/// <strong>A throwing provider costs its own rows and nothing else.</strong> Today both providers are
/// in-process and a throw means a bug; the reason to contain it now is that the shape of a future
/// provider is a timeout, a 500 and a rate limit, and the surface that must not die when those happen
/// is the Help popup showing perfectly good local content. Cancellation is not swallowed: a cancelled
/// token means the caller stopped caring, and that has to reach the caller.
/// </para>
/// </remarks>
public sealed class AssistContentProviderRegistry
{
    private readonly IAssistContentProvider[] _providers;
    private readonly AssistProviderPolicy _policy;

    /// <param name="providers">Providers in query order. Null entries are ignored.</param>
    /// <param name="policy">
    /// Which opt-in providers are enabled. Defaults to <see cref="AssistProviderPolicy.LocalOnly"/>.
    /// </param>
    public AssistContentProviderRegistry(
        IEnumerable<IAssistContentProvider>? providers = null,
        AssistProviderPolicy? policy = null)
    {
        var ordered = new List<IAssistContentProvider>();
        if (providers != null)
        {
            foreach (IAssistContentProvider provider in providers)
            {
                if (provider != null)
                {
                    ordered.Add(provider);
                }
            }
        }

        _providers = ordered.ToArray();
        _policy = policy ?? AssistProviderPolicy.LocalOnly;
    }

    /// <summary>Every registered provider, enabled or not. Diagnostics and a future Settings list.</summary>
    public IReadOnlyList<IAssistContentProvider> Providers => _providers;

    /// <summary>
    /// Whether any enabled provider can answer <paramref name="capability"/>.
    /// </summary>
    /// <remarks>
    /// This is the question an empty state asks. "We looked and found nothing" and "nobody can answer
    /// this" are different sentences to a user, and telling them apart is the whole reason this is a
    /// registry rather than a list. See <see cref="AssistEmptyStates"/>.
    /// </remarks>
    public bool HasProviderFor(AssistCapabilities capability)
    {
        foreach (IAssistContentProvider provider in _providers)
        {
            if (_policy.IsEnabled(provider, capability))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Asks every enabled provider for <paramref name="request"/>'s capability, in order, and returns
    /// their results. An empty list means nothing is configured to answer.
    /// </summary>
    public async Task<IReadOnlyList<AssistContentResult>> QueryAsync(
        AssistContentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var results = new List<AssistContentResult>();

        foreach (IAssistContentProvider provider in _providers)
        {
            if (!_policy.IsEnabled(provider, request.Capability))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            AssistContentResult result;
            try
            {
                result = await provider.QueryAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // See the type remarks: one provider's failure is not the surface's failure.
                continue;
            }

            if (result != null && !result.IsEmpty)
            {
                results.Add(result);
            }
        }

        return results;
    }
}

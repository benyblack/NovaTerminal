using System;
using System.Collections.Generic;
using System.Linq;

namespace NovaTerminal.CommandAssist.Providers;

/// <summary>
/// Which opt-in providers the user has enabled, per capability. The registry consults it before
/// querying anything.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The reserved settings shape, and why it is not in <c>TerminalSettings</c> yet.</strong>
/// This type <em>is</em> the config shape the AI milestone will deserialize into: a provider-id
/// allow-list per capability. What V2 Phase 5 deliberately does not add is the
/// <c>settings.json</c> key and the Settings UI row, because with only local providers shipped every
/// value of that key would be the empty object, and a persisted setting that cannot change any
/// observable behavior is exactly the phantom flag Phase 3b deleted
/// (<c>CommandAssistAutoHideInAltScreen</c>). The shape is fixed and documented here and in
/// <c>docs/command-assist/CommandAssist.md</c> so the milestone that adds a provider adds the key in
/// the same change that makes it mean something. Unknown keys are ignored on load, so introducing it
/// later is not a breaking change.
/// </para>
/// <para>
/// The intended JSON, for the record:
/// <code>
/// "commandAssistProviders": {
///   "suggestFix":  ["acme.cloud-fixes"],
///   "enrichDocs":  [],
///   "explain":     ["acme.cloud-explain"],
///   "nlToCommand": ["acme.cloud-nl2cmd"]
/// }
/// </code>
/// An absent capability, and an absent key altogether, both mean "no opt-in providers enabled" -
/// which is the safe default and today's shipped behavior.
/// </para>
/// <para>
/// <strong>Local providers are not listable.</strong> A provider with
/// <see cref="IAssistContentProvider.RequiresExplicitOptIn"/> <see langword="false"/> is always
/// enabled and this type has no way to switch it off. That is the honest surface: "turn off the
/// bundled command catalogue" is not a privacy control, it is a way to break Help, and the two
/// controls that genuinely matter for local content already exist (the master flag and the history
/// flag).
/// </para>
/// </remarks>
public sealed class AssistProviderPolicy
{
    private readonly Dictionary<AssistCapabilities, HashSet<string>> _enabledIds = new();

    /// <summary>
    /// Builds a policy from a per-capability allow-list of provider ids. A null or absent entry means
    /// no opt-in provider is enabled for that capability.
    /// </summary>
    public AssistProviderPolicy(
        IReadOnlyDictionary<AssistCapabilities, IReadOnlyList<string>>? enabledProviderIdsByCapability)
    {
        if (enabledProviderIdsByCapability == null)
        {
            return;
        }

        foreach (KeyValuePair<AssistCapabilities, IReadOnlyList<string>> pair in enabledProviderIdsByCapability)
        {
            if (pair.Value == null)
            {
                continue;
            }

            _enabledIds[pair.Key] = new HashSet<string>(
                pair.Value.Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The shipped policy: local providers on, no opt-in provider enabled anywhere. Immutable and
    /// shared - it holds no per-session state.
    /// </summary>
    public static AssistProviderPolicy LocalOnly { get; } = new(null);

    /// <summary>
    /// Whether <paramref name="provider"/> may be queried for <paramref name="capability"/>.
    /// </summary>
    public bool IsEnabled(IAssistContentProvider provider, AssistCapabilities capability)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if ((provider.Capabilities & capability) == AssistCapabilities.None)
        {
            return false;
        }

        if (!provider.RequiresExplicitOptIn)
        {
            return true;
        }

        return _enabledIds.TryGetValue(capability, out HashSet<string>? ids) && ids.Contains(provider.Id);
    }
}

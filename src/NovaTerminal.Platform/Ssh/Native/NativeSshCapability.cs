using NovaTerminal.Platform.Ssh.Models;

namespace NovaTerminal.Platform.Ssh.Native;

/// <summary>
/// Why the native SSH backend cannot serve a profile, if it cannot.
/// </summary>
public enum NativeSshUnsupportedReason
{
    None = 0,

    /// <summary>
    /// The profile carries a remote (server-side listener) forward. The native backend has no
    /// <c>tcpip-forward</c> support, so there is nothing to degrade to.
    /// </summary>
    RemotePortForward = 1,

    /// <summary>
    /// The profile chains more than one jump hop. The native backend nests exactly one
    /// direct-tcpip hop today.
    /// </summary>
    MultipleJumpHops = 2
}

/// <summary>
/// The verdict for one profile: supported, or unsupported with a user-facing explanation that
/// names the fix.
/// </summary>
public readonly record struct NativeSshCapabilityResult(NativeSshUnsupportedReason Reason, string Explanation)
{
    public bool IsSupported => Reason == NativeSshUnsupportedReason.None;

    public static NativeSshCapabilityResult Supported { get; } =
        new(NativeSshUnsupportedReason.None, string.Empty);

    public static NativeSshCapabilityResult Unsupported(NativeSshUnsupportedReason reason, string explanation) =>
        new(reason, explanation);
}

/// <summary>
/// Single source of truth for "can the native SSH backend serve this profile?".
///
/// The answer used to be spelled out in three places that could drift apart — the session
/// constructor, the jump-host plan, and the port-forward session — each throwing its own wording,
/// and none of them reachable before a connect was already underway. Concentrating it here lets the
/// profile editor refuse to save a native profile that could never connect, and lets a caller ask
/// the question without constructing a session.
///
/// Deliberately NOT a fallback mechanism. A profile explicitly set to <see cref="SshBackendKind.Native"/>
/// still fails loudly when native cannot serve it (see
/// <c>docs/plans/2026-03-31-native-ssh-rollout-controls-design.md</c>); this type only makes the
/// refusal early and uniform. Routing a profile that has expressed no preference is a separate
/// question — see the note in <c>docs/SSH_ROADMAP.md</c>.
/// </summary>
public static class NativeSshCapability
{
    public static NativeSshCapabilityResult Evaluate(SshProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return Evaluate(profile.Forwards, profile.JumpHops);
    }

    /// <summary>
    /// Overload for callers holding an in-progress edit rather than a saved profile (the connection
    /// editor's observable collections), so validating a draft does not require minting a profile.
    /// </summary>
    public static NativeSshCapabilityResult Evaluate(
        IReadOnlyCollection<PortForward>? forwards,
        IReadOnlyCollection<SshJumpHop>? jumpHops)
    {
        // Jump hops first: a profile with both problems is more likely to be reshaped around the
        // hop chain than around the forward, so lead with the structural one.
        if (jumpHops is { Count: > 1 })
        {
            return NativeSshCapabilityResult.Unsupported(
                NativeSshUnsupportedReason.MultipleJumpHops,
                "Multiple jump hops are not supported by the native SSH backend yet. "
                + "Use a single jump hop, or switch this profile to the OpenSSH backend.");
        }

        if (forwards != null && forwards.Any(forward => forward.Kind is not PortForwardKind.Local and not PortForwardKind.Dynamic))
        {
            return NativeSshCapabilityResult.Unsupported(
                NativeSshUnsupportedReason.RemotePortForward,
                "The native SSH backend supports local and dynamic port forwards only. "
                + "Remove the remote forward, or switch this profile to the OpenSSH backend.");
        }

        return NativeSshCapabilityResult.Supported;
    }
}

using NovaTerminal.Platform.Ssh.Models;

namespace NovaTerminal.Platform.Ssh.Native;

/// <summary>
/// Why the native SSH backend cannot serve a profile, if it cannot. See
/// <see cref="NativeSshCapability"/>.
/// </summary>
public enum NativeSshUnsupportedReason
{
    None = 0,

    /// <summary>
    /// A remote forward with source port 0 asks the server to allocate the listen port
    /// (OpenSSH's <c>-R 0:...</c>). The native backend matches incoming connections back to
    /// their rule by the port the rule requested, so a server-picked port has no rule to land
    /// on yet. OpenSSH serves this shape; native refuses it by name until it can.
    /// </summary>
    RemoteForwardWithServerAllocatedPort = 1
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
        // Almost every profile shape is supported: jump chains of any length (one nested
        // direct-tcpip hop per entry, as OpenSSH treats -J), and local, dynamic, and remote
        // forwards alike. The one refusal left below reaches the profile editor, the factory,
        // and the session at once — which is the whole reason this type exists.
        _ = jumpHops;

        if (forwards != null)
        {
            foreach (PortForward forward in forwards)
            {
                if (forward.Kind == PortForwardKind.Remote && forward.SourcePort == 0)
                {
                    return NativeSshCapabilityResult.Unsupported(
                        NativeSshUnsupportedReason.RemoteForwardWithServerAllocatedPort,
                        "The native SSH backend cannot serve a remote forward with source port 0 (a server-allocated listen port). Give the forward an explicit port, or switch this profile's backend to OpenSSH.");
                }
            }
        }

        return NativeSshCapabilityResult.Supported;
    }
}

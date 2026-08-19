using NovaTerminal.Platform.Ssh.Models;

namespace NovaTerminal.Platform.Ssh.Native;

/// <summary>
/// Why the native SSH backend cannot serve a profile, if it cannot. Currently only
/// <see cref="None"/>: remote forwards were the last unsupported shape, and they gained native
/// <c>tcpip-forward</c> support. The enum stays because the gate stays — see
/// <see cref="NativeSshCapability"/>.
/// </summary>
public enum NativeSshUnsupportedReason
{
    None = 0
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
        // Every profile shape is supported today: jump chains of any length (one nested
        // direct-tcpip hop per entry, as OpenSSH treats -J), and local, dynamic, and remote
        // forwards alike. The parameters and the gate's call sites stay wired even so — the next
        // shape the backend cannot serve gets its refusal here, reaching the profile editor,
        // the factory, and the session in one change, which is the whole reason this type exists.
        _ = forwards;
        _ = jumpHops;

        return NativeSshCapabilityResult.Supported;
    }
}

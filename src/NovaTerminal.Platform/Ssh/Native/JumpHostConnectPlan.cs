using NovaTerminal.Platform.Ssh.Models;

namespace NovaTerminal.Platform.Ssh.Native;

public sealed class JumpHostConnectPlan
{
    private JumpHostConnectPlan()
    {
    }

    public required string TargetHost { get; init; }
    public required string TargetUser { get; init; }
    public int TargetPort { get; init; } = 22;
    public SshJumpHop? JumpHost { get; init; }
    public bool HasJumpHost => JumpHost != null;

    public static JumpHostConnectPlan Create(SshProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // Defence in depth: NativeSshSession already refused this shape via NativeSshCapability, but
        // the plan is constructible on its own. Share the wording so the two cannot drift.
        NativeSshCapabilityResult capability = NativeSshCapability.Evaluate(profile);
        if (capability.Reason == NativeSshUnsupportedReason.MultipleJumpHops)
        {
            throw new NotSupportedException(capability.Explanation);
        }

        return new JumpHostConnectPlan
        {
            TargetHost = profile.Host,
            TargetUser = profile.User,
            TargetPort = profile.Port > 0 ? profile.Port : 22,
            JumpHost = profile.JumpHops.Count == 0
                ? null
                : new SshJumpHop
                {
                    Host = profile.JumpHops[0].Host,
                    User = profile.JumpHops[0].User,
                    Port = profile.JumpHops[0].Port > 0 ? profile.JumpHops[0].Port : 22
                }
        };
    }
}

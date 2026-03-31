using NovaTerminal.Core.Ssh.Models;

namespace NovaTerminal.Core.Ssh.Native;

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

        if (profile.JumpHops.Count > 1)
        {
            throw new NotSupportedException("Multiple jump hops are not supported by the native SSH backend yet.");
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

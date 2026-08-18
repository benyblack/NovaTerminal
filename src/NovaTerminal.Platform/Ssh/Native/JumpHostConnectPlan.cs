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

    /// <summary>
    /// The chain in connect order, client → target; empty means a direct connection. Each hop is
    /// a full SSH session nested over a direct-tcpip channel of the hop before it, so order is
    /// load-bearing: the first entry is the only one reached over raw TCP.
    /// </summary>
    public IReadOnlyList<SshJumpHop> JumpHops { get; init; } = Array.Empty<SshJumpHop>();

    public bool HasJumpHops => JumpHops.Count > 0;

    public static JumpHostConnectPlan Create(SshProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new JumpHostConnectPlan
        {
            TargetHost = profile.Host,
            TargetUser = profile.User,
            TargetPort = profile.Port > 0 ? profile.Port : 22,
            JumpHops = profile.JumpHops
                .Select(hop => new SshJumpHop
                {
                    Host = hop.Host,
                    User = hop.User,
                    Port = hop.Port > 0 ? hop.Port : 22
                })
                .ToArray()
        };
    }
}

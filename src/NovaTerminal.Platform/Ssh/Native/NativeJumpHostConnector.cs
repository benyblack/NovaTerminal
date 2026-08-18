using NovaTerminal.Platform.Ssh.Models;

namespace NovaTerminal.Platform.Ssh.Native;

public sealed class NativeJumpHostConnector
{
    public NativeSshConnectionOptions CreateConnectionOptions(SshProfile profile, int cols, int rows)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return CreateConnectionOptions(JumpHostConnectPlan.Create(profile), profile, cols, rows);
    }

    public NativeSshConnectionOptions CreateConnectionOptions(JumpHostConnectPlan plan, SshProfile profile, int cols, int rows)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(profile);

        return new NativeSshConnectionOptions
        {
            Host = plan.TargetHost,
            User = plan.TargetUser,
            Port = plan.TargetPort,
            Cols = cols,
            Rows = rows,
            KeepAliveIntervalSeconds = profile.ServerAliveIntervalSeconds > 0
                ? profile.ServerAliveIntervalSeconds
                : 30,
            KeepAliveCountMax = profile.ServerAliveCountMax > 0
                ? profile.ServerAliveCountMax
                : 3,
            IdentityFilePath = string.IsNullOrWhiteSpace(profile.IdentityFilePath)
                ? null
                : profile.IdentityFilePath,
            JumpHops = plan.JumpHops
                .Select(hop => new SshJumpHop
                {
                    Host = hop.Host,
                    // A hop without its own user authenticates as the target user — the same
                    // default OpenSSH applies to a bare `-J host`, and the same one the native
                    // layer applies when the field crosses the FFI as null.
                    User = string.IsNullOrWhiteSpace(hop.User) ? plan.TargetUser : hop.User,
                    Port = hop.Port > 0 ? hop.Port : 22
                })
                .ToArray()
        };
    }

    public string DescribePath(JumpHostConnectPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.JumpHops.Count switch
        {
            0 => "direct",
            1 => "jump-host",
            _ => $"jump-chain:{plan.JumpHops.Count}"
        };
    }
}

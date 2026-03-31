using NovaTerminal.Core.Ssh.Models;

namespace NovaTerminal.Core.Ssh.Native;

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
            IdentityFilePath = string.IsNullOrWhiteSpace(profile.IdentityFilePath)
                ? null
                : profile.IdentityFilePath,
            JumpHost = plan.JumpHost == null
                ? null
                : new SshJumpHop
                {
                    Host = plan.JumpHost.Host,
                    User = string.IsNullOrWhiteSpace(plan.JumpHost.User) ? plan.TargetUser : plan.JumpHost.User,
                    Port = plan.JumpHost.Port > 0 ? plan.JumpHost.Port : 22
                }
        };
    }

    public string DescribePath(JumpHostConnectPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.HasJumpHost ? "jump-host" : "direct";
    }
}

using NovaTerminal.Core.Ssh.Models;

namespace NovaTerminal.Core.Ssh.Native;

public sealed class NativeSshConnectionOptions
{
    public required string Host { get; init; }
    public required string User { get; init; }
    public int Port { get; init; } = 22;
    public int Cols { get; init; } = 120;
    public int Rows { get; init; } = 30;
    public string Term { get; init; } = "xterm-256color";
    public string? IdentityFilePath { get; init; }
    public SshJumpHop? JumpHost { get; init; }

    public static NativeSshConnectionOptions FromProfile(SshProfile profile, int cols, int rows)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new NativeSshConnectionOptions
        {
            Host = profile.Host,
            User = profile.User,
            Port = profile.Port,
            Cols = cols,
            Rows = rows,
            IdentityFilePath = string.IsNullOrWhiteSpace(profile.IdentityFilePath)
                ? null
                : profile.IdentityFilePath
        };
    }
}

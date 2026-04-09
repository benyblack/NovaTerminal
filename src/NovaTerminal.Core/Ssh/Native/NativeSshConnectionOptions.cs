using NovaTerminal.Core.Ssh.Models;

namespace NovaTerminal.Core.Ssh.Native;

public sealed class NativeSshConnectionOptions
{
    private const int DefaultKeepAliveIntervalSeconds = 30;
    private const int DefaultKeepAliveCountMax = 3;

    public required string Host { get; init; }
    public required string User { get; init; }
    public int Port { get; init; } = 22;
    public int Cols { get; init; } = 120;
    public int Rows { get; init; } = 30;
    public string Term { get; init; } = "xterm-256color";
    public string? IdentityFilePath { get; init; }
    public SshJumpHop? JumpHost { get; init; }
    public int KeepAliveIntervalSeconds { get; init; } = DefaultKeepAliveIntervalSeconds;
    public int KeepAliveCountMax { get; init; } = DefaultKeepAliveCountMax;

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
            KeepAliveIntervalSeconds = profile.ServerAliveIntervalSeconds > 0
                ? profile.ServerAliveIntervalSeconds
                : DefaultKeepAliveIntervalSeconds,
            KeepAliveCountMax = profile.ServerAliveCountMax > 0
                ? profile.ServerAliveCountMax
                : DefaultKeepAliveCountMax,
            IdentityFilePath = string.IsNullOrWhiteSpace(profile.IdentityFilePath)
                ? null
                : profile.IdentityFilePath
        };
    }
}

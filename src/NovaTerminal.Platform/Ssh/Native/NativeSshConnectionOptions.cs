using NovaTerminal.Platform.Ssh.Models;

namespace NovaTerminal.Platform.Ssh.Native;

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
    public string? Password { get; init; }
    public string? IdentityFilePath { get; init; }

    /// <summary>
    /// Try public-key auth with every identity the user's SSH agent holds — after a configured
    /// identity file (which keeps first position so an over-full agent cannot exhaust the
    /// server's auth tries before it), before anything that prompts. Discovered the way OpenSSH
    /// discovers it (SSH_AUTH_SOCK on Unix; the OpenSSH service pipe, then Pageant, on Windows).
    /// No agent, an empty agent, and refused keys all fall through. Applies to jump hops too.
    /// </summary>
    public bool UseAgent { get; init; }
    public string? KnownHostsFilePath { get; init; }

    /// <summary>The jump chain in connect order, client → target; empty means direct.</summary>
    public IReadOnlyList<SshJumpHop> JumpHops { get; init; } = Array.Empty<SshJumpHop>();
    public int KeepAliveIntervalSeconds { get; init; } = DefaultKeepAliveIntervalSeconds;
    public int KeepAliveCountMax { get; init; } = DefaultKeepAliveCountMax;
    public RemoteShellKind RemoteShellKind { get; init; } = RemoteShellKind.Auto;
    public string? ShellDetectionCommand { get; init; }
    public string? BashCwdBootstrap { get; init; }
    public string? ZshCwdBootstrap { get; init; }
    public string? FishCwdBootstrap { get; init; }

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
            IdentityFilePath = ResolveIdentityFilePath(profile),
            UseAgent = ResolveUseAgent(profile),
            RemoteShellKind = profile.RemoteShellKind
        };
    }

    /// <summary>
    /// Agent identities are offered unless the profile explicitly chose an identity file —
    /// the contract OpenSSH's IdentitiesOnly expresses. Default mode gets both: the configured
    /// file first (it predates agent support and must stay reachable), then the agent.
    /// </summary>
    public static bool ResolveUseAgent(SshProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.AuthMode != SshAuthMode.IdentityFile;
    }

    /// <summary>
    /// The identity file the native backend should offer, or null. A profile explicitly in
    /// Agent mode ignores any leftover path (the user chose the agent); Default and
    /// IdentityFile modes use the path when one is set.
    /// </summary>
    public static string? ResolveIdentityFilePath(SshProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.AuthMode == SshAuthMode.Agent || string.IsNullOrWhiteSpace(profile.IdentityFilePath))
        {
            return null;
        }

        return profile.IdentityFilePath;
    }
}

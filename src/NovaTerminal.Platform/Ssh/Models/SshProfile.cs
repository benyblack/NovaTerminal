using System.Collections.Generic;

namespace NovaTerminal.Platform.Ssh.Models;

public enum SshAuthMode
{
    Default = 0,
    Agent = 1,
    IdentityFile = 2
}

public sealed class SshProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public SshBackendKind BackendKind { get; set; } = SshBackendKind.OpenSsh;
    public string Name { get; set; } = "New SSH Profile";
    public string GroupPath { get; set; } = "General";
    public string Notes { get; set; } = string.Empty;
    public string AccentColor { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public string Host { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public SshAuthMode AuthMode { get; set; } = SshAuthMode.Default;
    public string IdentityFilePath { get; set; } = string.Empty;
    public bool RememberPasswordInVault { get; set; }
    public List<SshJumpHop> JumpHops { get; set; } = new();
    public List<PortForward> Forwards { get; set; } = new();
    public SshMuxOptions MuxOptions { get; set; } = new();
    public int ServerAliveIntervalSeconds { get; set; } = 30;
    public int ServerAliveCountMax { get; set; } = 3;
    public string ExtraSshArgs { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public RemoteShellKind RemoteShellKind { get; set; } = RemoteShellKind.Auto;

    // A3 (agent host act surface): when true, AI agents granted "Agent access
    // (act)" may type into and spawn sessions for this remote profile. Default
    // false — acting on a remote reaches another machine with the user's
    // credentials, so it is opt-in per profile on top of the global act toggle.
    // New field; absent in older stores deserializes to false (no migration).
    public bool AllowAgentAccess { get; set; }
}

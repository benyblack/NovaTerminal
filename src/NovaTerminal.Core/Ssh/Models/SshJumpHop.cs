namespace NovaTerminal.Core.Ssh.Models;

public sealed class SshJumpHop
{
    public string Host { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
}

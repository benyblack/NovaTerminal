namespace NovaTerminal.Core.Ssh.Models;

public sealed class SshJumpHop
{
    public string Host { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public int Port { get; set; } = 22;

    public override string ToString()
    {
        string userPrefix = string.IsNullOrWhiteSpace(User) ? string.Empty : $"{User.Trim()}@";
        int port = Port > 0 ? Port : 22;
        return port == 22 ? $"{userPrefix}{Host}" : $"{userPrefix}{Host}:{port}";
    }
}

namespace NovaTerminal.Core.Ssh.Native;

public sealed class KnownHostEntry
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Algorithm { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public DateTime TrustedAtUtc { get; set; } = DateTime.UtcNow;
}

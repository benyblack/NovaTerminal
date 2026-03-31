namespace NovaTerminal.ViewModels.Ssh;

public sealed class HostKeyPromptViewModel
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public string Algorithm { get; init; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public bool IsChangedHostKey { get; init; }

    public string Title => IsChangedHostKey ? "Changed Host Key" : "Unknown Host Key";
    public string Message => IsChangedHostKey
        ? "The server host key changed. Only continue if you trust this change."
        : "The server host key is not trusted yet. Continue only if you trust this host.";
}

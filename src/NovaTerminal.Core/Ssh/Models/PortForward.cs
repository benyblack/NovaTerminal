namespace NovaTerminal.Core.Ssh.Models;

public enum PortForwardKind
{
    Local = 0,
    Remote = 1,
    Dynamic = 2
}

public sealed class PortForward
{
    public PortForwardKind Kind { get; set; } = PortForwardKind.Local;
    public string BindAddress { get; set; } = string.Empty;
    public int SourcePort { get; set; }
    public string DestinationHost { get; set; } = string.Empty;
    public int DestinationPort { get; set; }

    public override string ToString()
    {
        string bind = string.IsNullOrWhiteSpace(BindAddress)
            ? SourcePort.ToString()
            : $"{BindAddress}:{SourcePort}";

        return Kind switch
        {
            PortForwardKind.Local => $"Local {bind} -> {DestinationHost}:{DestinationPort}",
            PortForwardKind.Remote => $"Remote {bind} -> {DestinationHost}:{DestinationPort}",
            PortForwardKind.Dynamic => $"Dynamic {bind}",
            _ => bind
        };
    }
}

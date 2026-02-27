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
}

namespace NovaTerminal.Core.Ssh.Native;

public sealed class NativePortForwardOpenOptions
{
    public required string HostToConnect { get; init; }
    public int PortToConnect { get; init; }
    public string OriginatorAddress { get; init; } = "127.0.0.1";
    public int OriginatorPort { get; init; }
}

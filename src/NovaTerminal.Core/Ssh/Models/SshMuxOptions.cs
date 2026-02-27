namespace NovaTerminal.Core.Ssh.Models;

public sealed class SshMuxOptions
{
    public bool Enabled { get; set; }
    public bool ControlMasterAuto { get; set; } = true;
    public string ControlPath { get; set; } = string.Empty;
    public int ControlPersistSeconds { get; set; }
}

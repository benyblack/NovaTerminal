namespace NovaTerminal.Platform.Ssh.Interactions;

public enum SshInteractionKind
{
    UnknownHostKey = 0,
    ChangedHostKey = 1,
    Password = 2,
    Passphrase = 3,
    KeyboardInteractive = 4
}

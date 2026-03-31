namespace NovaTerminal.Core.Ssh.Native;

[Flags]
public enum NativeSshEventFlags
{
    None = 0,
    Json = 1,
    Binary = 2
}

public enum NativeSshEventKind
{
    None = 0,
    Connected = 1,
    Data = 2,
    HostKeyPrompt = 3,
    PasswordPrompt = 4,
    PassphrasePrompt = 5,
    KeyboardInteractivePrompt = 6,
    ExitStatus = 7,
    Error = 8,
    Closed = 9
}

public enum NativeSshResponseKind
{
    HostKeyDecision = 1,
    Password = 2,
    Passphrase = 3,
    KeyboardInteractive = 4
}

public sealed class NativeSshEvent
{
    public NativeSshEvent(
        NativeSshEventKind kind,
        byte[] payload,
        int statusCode = 0,
        NativeSshEventFlags flags = NativeSshEventFlags.None)
    {
        Kind = kind;
        Payload = payload ?? Array.Empty<byte>();
        StatusCode = statusCode;
        Flags = flags;
    }

    public NativeSshEventKind Kind { get; }
    public byte[] Payload { get; }
    public int StatusCode { get; }
    public NativeSshEventFlags Flags { get; }

    public static NativeSshEvent Data(byte[] payload) =>
        new(NativeSshEventKind.Data, payload, flags: NativeSshEventFlags.Binary);

    public static NativeSshEvent ExitStatus(int statusCode, byte[]? payload = null) =>
        new(NativeSshEventKind.ExitStatus, payload ?? Array.Empty<byte>(), statusCode, NativeSshEventFlags.Json);

    public static NativeSshEvent Closed(byte[]? payload = null) =>
        new(NativeSshEventKind.Closed, payload ?? Array.Empty<byte>(), flags: NativeSshEventFlags.Json);
}

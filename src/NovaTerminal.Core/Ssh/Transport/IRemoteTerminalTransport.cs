namespace NovaTerminal.Core.Ssh.Transport;

public interface IRemoteTerminalTransport : IDisposable
{
    event Action<byte[]>? OnOutputReceived;
    event Action<int>? OnExit;

    void SendInput(byte[] data);
    void Resize(int cols, int rows);
    void Start();
}

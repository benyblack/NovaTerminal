namespace NovaTerminal.Core.Ssh.Native;

public interface INativeSshInterop
{
    IntPtr Connect(NativeSshConnectionOptions options);

    // Blocking FFI/network call. App/UI-facing services must offload this work before awaiting it.
    IReadOnlyList<NativeRemotePathEntry> ListRemoteDirectory(
        NativeSshConnectionOptions connectionOptions,
        string remotePath,
        CancellationToken cancellationToken);
    void RunSftpTransfer(
        NativeSshConnectionOptions connectionOptions,
        NativeSftpTransferOptions transferOptions,
        Action<NativeSftpTransferProgress>? progress,
        CancellationToken cancellationToken);
    NativeSshEvent? PollEvent(IntPtr sessionHandle);
    void Write(IntPtr sessionHandle, ReadOnlySpan<byte> data);
    void Resize(IntPtr sessionHandle, int cols, int rows);
    int OpenDirectTcpIp(IntPtr sessionHandle, NativePortForwardOpenOptions options);
    void WriteChannel(IntPtr sessionHandle, int channelId, ReadOnlySpan<byte> data);
    void SendChannelEof(IntPtr sessionHandle, int channelId);
    void CloseChannel(IntPtr sessionHandle, int channelId);
    void SubmitResponse(IntPtr sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data);
    void Close(IntPtr sessionHandle);
}

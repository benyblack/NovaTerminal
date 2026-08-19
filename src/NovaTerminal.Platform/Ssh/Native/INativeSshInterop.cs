namespace NovaTerminal.Platform.Ssh.Native;

public interface INativeSshInterop
{
    NovaSshSafeHandle Connect(NativeSshConnectionOptions options);

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
    NativeSshEvent? PollEvent(NovaSshSafeHandle sessionHandle);
    void Write(NovaSshSafeHandle sessionHandle, ReadOnlySpan<byte> data);
    void Resize(NovaSshSafeHandle sessionHandle, int cols, int rows);
    int OpenDirectTcpIp(NovaSshSafeHandle sessionHandle, NativePortForwardOpenOptions options);
    void WriteChannel(NovaSshSafeHandle sessionHandle, int channelId, ReadOnlySpan<byte> data);

    /// <summary>
    /// Queues forward-channel data toward the remote, reporting whether it was accepted.
    /// <see langword="false"/> means the channel already has more queued than its budget allows: not
    /// an error, and nothing was consumed. The caller should retry, which is what stops it reading its
    /// local socket and lets TCP flow control throttle the local peer — the alternative to either
    /// growing the queue without limit or closing a channel for being merely slow.
    /// </summary>
    /// <remarks>
    /// Default implementation delegates to <see cref="WriteChannel"/> and reports acceptance, so an
    /// interop that has no queue of its own (test doubles, in-memory fakes) needs no changes. Only the
    /// real FFI can answer this meaningfully, and only it overrides.
    /// </remarks>
    bool TryWriteChannel(NovaSshSafeHandle sessionHandle, int channelId, ReadOnlySpan<byte> data)
    {
        WriteChannel(sessionHandle, channelId, data);
        return true;
    }
    /// <summary>
    /// Asks the server to open a remote-forward listener (a tcpip-forward global request) and
    /// returns the port it bound. Connections arriving on that listener surface as
    /// <see cref="NativeSshEventKind.ForwardChannelIncoming"/> events. Blocking FFI/network call —
    /// it waits for the server's verdict — so callers offload it, never hold a UI thread on it.
    /// </summary>
    /// <remarks>
    /// Default implementation throws: a test double that never expects remote forwards should fail
    /// a test that reaches for one, and the double that does expect them overrides.
    /// </remarks>
    int RequestRemoteForward(NovaSshSafeHandle sessionHandle, string bindAddress, int port) =>
        throw new NotSupportedException(
            $"{GetType().Name} does not implement remote forwards.");

    void SendChannelEof(NovaSshSafeHandle sessionHandle, int channelId);
    void CloseChannel(NovaSshSafeHandle sessionHandle, int channelId);
    void SubmitResponse(NovaSshSafeHandle sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data);
    void Close(NovaSshSafeHandle sessionHandle);
}

namespace NovaTerminal.Core.Ssh.Native;

public interface INativeSshInterop
{
    IntPtr Connect(NativeSshConnectionOptions options);
    NativeSshEvent? PollEvent(IntPtr sessionHandle);
    void Write(IntPtr sessionHandle, ReadOnlySpan<byte> data);
    void Resize(IntPtr sessionHandle, int cols, int rows);
    void SubmitResponse(IntPtr sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data);
    void Close(IntPtr sessionHandle);
}

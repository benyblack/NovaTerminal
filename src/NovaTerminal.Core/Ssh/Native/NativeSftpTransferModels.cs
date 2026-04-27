using System.Runtime.InteropServices;

namespace NovaTerminal.Core.Ssh.Native;

public enum NativeSftpTransferDirection
{
    Upload,
    Download
}

public enum NativeSftpTransferKind
{
    File,
    Directory
}

public sealed class NativeSftpTransferOptions
{
    public NativeSftpTransferDirection Direction { get; init; }
    public NativeSftpTransferKind Kind { get; init; }
    public string? LocalPath { get; init; }
    public string? RemotePath { get; init; }

    public void Validate()
    {
        if (!Enum.IsDefined(Direction))
        {
            throw new ArgumentOutOfRangeException(nameof(Direction), "Transfer direction must be a defined value.");
        }

        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind), "Transfer kind must be a defined value.");
        }

        if (string.IsNullOrWhiteSpace(LocalPath))
        {
            throw new ArgumentException("A local path is required for native SFTP transfers.", nameof(LocalPath));
        }

        if (string.IsNullOrWhiteSpace(RemotePath))
        {
            throw new ArgumentException("A remote path is required for native SFTP transfers.", nameof(RemotePath));
        }
    }
}

public sealed class NativeSftpTransferProgress
{
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public string? CurrentPath { get; init; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSftpTransferProgressCallbackData
{
    public ulong BytesDone;
    public ulong BytesTotal;
    public IntPtr CurrentPath;
}

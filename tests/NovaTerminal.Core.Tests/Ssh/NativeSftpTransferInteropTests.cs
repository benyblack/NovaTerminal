using NovaTerminal.Core.Ssh.Native;

namespace NovaTerminal.Core.Tests.Ssh;

public sealed class NativeSftpTransferInteropTests
{
    [Fact]
    public void TransferOptions_RequireLocalAndRemotePaths()
    {
        NativeSftpTransferOptions options = new()
        {
            Direction = NativeSftpTransferDirection.Download,
            Kind = NativeSftpTransferKind.File
        };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void TransferOptions_AcceptExplicitLocalAndRemotePaths()
    {
        NativeSftpTransferOptions options = new()
        {
            Direction = NativeSftpTransferDirection.Upload,
            Kind = NativeSftpTransferKind.Directory,
            LocalPath = @"C:\downloads\logs",
            RemotePath = "/var/tmp/logs"
        };

        options.Validate();

        Assert.Equal(NativeSftpTransferDirection.Upload, options.Direction);
        Assert.Equal(NativeSftpTransferKind.Directory, options.Kind);
        Assert.Equal(@"C:\downloads\logs", options.LocalPath);
        Assert.Equal("/var/tmp/logs", options.RemotePath);
    }

    [Fact]
    public void RunSftpTransfer_UsesNativeBackendStubResponse()
    {
        INativeSshInterop interop = new NativeSshInterop();
        NativeSshConnectionOptions connectionOptions = new()
        {
            Host = "example.com",
            User = "nova"
        };
        NativeSftpTransferOptions transferOptions = new()
        {
            Direction = NativeSftpTransferDirection.Download,
            Kind = NativeSftpTransferKind.File,
            LocalPath = @"C:\downloads\report.txt",
            RemotePath = "/tmp/report.txt"
        };

        Action act = () => interop.RunSftpTransfer(
            connectionOptions,
            transferOptions,
            progress: null,
            CancellationToken.None);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(act);

        Assert.Contains("result -5", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("native backend stub", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not implemented", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

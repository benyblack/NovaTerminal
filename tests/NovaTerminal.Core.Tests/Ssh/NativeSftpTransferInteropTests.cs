using NovaTerminal.Core.Ssh.Native;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NovaTerminal.Core.Tests.Ssh;

public sealed class NativeSftpTransferInteropTests
{
    [Fact]
    public void ProgressPayload_AllowsKnownTotalAndCurrentPath()
    {
        NativeSftpTransferProgress progress = new()
        {
            BytesDone = 128,
            BytesTotal = 1024,
            CurrentPath = "/tmp/report.txt"
        };

        Assert.Equal(128, progress.BytesDone);
        Assert.Equal(1024, progress.BytesTotal);
        Assert.Equal("/tmp/report.txt", progress.CurrentPath);
    }

    [Fact]
    public void NativeSshInterop_ForwardsNativeSftpProgressCallbackToManagedProgressHandler()
    {
        Action<NativeSftpTransferProgress>? managedProgress = null;
        NativeSftpTransferProgress? observed = null;
        managedProgress = progress => observed = progress;

        using NativeSftpTransferProgressCallbackDataHandle nativeProgress = new(128, 1024, "/tmp/report.txt");
        MethodInfo? forwardMethod = typeof(NativeSshInterop)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .SingleOrDefault(static method =>
            {
                ParameterInfo[] parameters = method.GetParameters();
                return method.ReturnType == typeof(void)
                    && parameters.Length == 2
                    && parameters[0].ParameterType == typeof(Action<NativeSftpTransferProgress>)
                    && parameters[1].ParameterType == typeof(NativeSftpTransferProgressCallbackData);
            });

        Assert.True(
            forwardMethod is not null,
            "Expected a nonpublic static native SFTP progress translator with signature (Action<NativeSftpTransferProgress>, NativeSftpTransferProgressCallbackData).");

        forwardMethod!.Invoke(null, [managedProgress, nativeProgress.Data]);

        Assert.NotNull(observed);
        Assert.Equal(128, observed!.BytesDone);
        Assert.Equal(1024, observed.BytesTotal);
        Assert.Equal("/tmp/report.txt", observed.CurrentPath);
    }

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
    public void RunSftpTransfer_RequiresKnownHostsStorePath()
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

        ArgumentException ex = Assert.Throws<ArgumentException>(act);

        Assert.Contains("known-hosts", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunSftpTransfer_ReturnsClearErrorForUnsupportedModes()
    {
        INativeSshInterop interop = new NativeSshInterop();
        NativeSshConnectionOptions connectionOptions = new()
        {
            Host = "example.com",
            User = "nova",
            Password = "secret",
            KnownHostsFilePath = @"C:\known-hosts.json"
        };
        NativeSftpTransferOptions transferOptions = new()
        {
            Direction = (NativeSftpTransferDirection)99,
            Kind = NativeSftpTransferKind.Directory,
            LocalPath = @"C:\downloads\report.txt",
            RemotePath = "/tmp/report.txt"
        };

        Action act = () => interop.RunSftpTransfer(
            connectionOptions,
            transferOptions,
            progress: null,
            CancellationToken.None);

        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(act);

        Assert.Contains("direction", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NativeSftpTransferProgressCallbackDataHandle : IDisposable
    {
        private IntPtr currentPath;

        public NativeSftpTransferProgressCallbackDataHandle(ulong bytesDone, ulong bytesTotal, string currentPath)
        {
            this.currentPath = Marshal.StringToCoTaskMemUTF8(currentPath);
            Data = new NativeSftpTransferProgressCallbackData
            {
                BytesDone = bytesDone,
                BytesTotal = bytesTotal,
                CurrentPath = this.currentPath
            };
        }

        public NativeSftpTransferProgressCallbackData Data { get; }

        public void Dispose()
        {
            if (currentPath != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(currentPath);
                currentPath = IntPtr.Zero;
            }
        }
    }
}

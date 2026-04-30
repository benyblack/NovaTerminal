using NovaTerminal.Core.Ssh.Launch;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Native;
using NovaTerminal.Core.Ssh.Sessions;
using NovaTerminal.Core.Ssh.Storage;

namespace NovaTerminal.Core.Tests.Ssh;

public sealed class SshSessionFactoryTests
{
    [Fact]
    public void Create_ForNativeProfileRoutesToNativeSession()
    {
        var profileId = Guid.Parse("e94d09da-1269-4ecf-86b2-81bd4ec483cc");
        var store = new InMemorySshProfileStore(new SshProfile
        {
            Id = profileId,
            Name = "native",
            Host = "native.internal",
            User = "nova",
            BackendKind = SshBackendKind.Native
        });

        var factory = new SshSessionFactory(store, launcher: null, nativeInterop: new StubNativeSshInterop());
        using ITerminalSession session = factory.Create(profileId);

        Assert.IsType<NativeSshSession>(session);
    }

    [Fact]
    public void Create_ForNativeJumpHostProfile_LogsSelectedPath()
    {
        var profileId = Guid.Parse("d786b616-6b8a-4f57-b095-2e8f8b0d6907");
        var store = new InMemorySshProfileStore(new SshProfile
        {
            Id = profileId,
            Name = "native-jump",
            Host = "target.internal",
            User = "nova",
            BackendKind = SshBackendKind.Native,
            JumpHops =
            [
                new SshJumpHop { Host = "jump.internal" }
            ]
        });
        var logs = new List<string>();

        var factory = new SshSessionFactory(store, launcher: null, nativeInterop: new StubNativeSshInterop());
        using ITerminalSession session = factory.Create(profileId, log: logs.Add);

        Assert.IsType<NativeSshSession>(session);
        Assert.Contains(logs, message => message.Contains("backend=native", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs, message => message.Contains("path=jump-host", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Create_ForNativeProfileWhenExperimentalToggleDisabled_ThrowsClearError()
    {
        var profileId = Guid.Parse("76b0d0aa-cdde-4eb5-835d-f94df74485b6");
        var store = new InMemorySshProfileStore(new SshProfile
        {
            Id = profileId,
            Name = "native-disabled",
            Host = "native.internal",
            User = "nova",
            BackendKind = SshBackendKind.Native
        });

        var factory = new SshSessionFactory(store, launcher: null, nativeInterop: new StubNativeSshInterop(), nativeSshEnabled: false);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => factory.Create(profileId));

        Assert.Contains("disabled", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OpenSSH", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class InMemorySshProfileStore : ISshProfileStore
    {
        public InMemorySshProfileStore(SshProfile profile)
        {
            Profile = profile;
        }

        public SshProfile Profile { get; }

        public IReadOnlyList<SshProfile> GetProfiles() => new[] { Profile };

        public SshProfile? GetProfile(Guid profileId) => Profile.Id == profileId ? Profile : null;

        public void SaveProfile(SshProfile profile) => throw new NotSupportedException();

        public bool DeleteProfile(Guid profileId) => throw new NotSupportedException();
    }

    private sealed class StubNativeSshInterop : INativeSshInterop
    {
        public IntPtr Connect(NativeSshConnectionOptions options) => new(1);
        public IReadOnlyList<NativeRemotePathEntry> ListRemoteDirectory(NativeSshConnectionOptions connectionOptions, string remotePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void RunSftpTransfer(NativeSshConnectionOptions connectionOptions, NativeSftpTransferOptions transferOptions, Action<NativeSftpTransferProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public NativeSshEvent? PollEvent(IntPtr sessionHandle) => null;
        public void Write(IntPtr sessionHandle, ReadOnlySpan<byte> data)
        {
        }

        public void Resize(IntPtr sessionHandle, int cols, int rows)
        {
        }

        public int OpenDirectTcpIp(IntPtr sessionHandle, NativePortForwardOpenOptions options) => 1;

        public void WriteChannel(IntPtr sessionHandle, int channelId, ReadOnlySpan<byte> data)
        {
        }

        public void SendChannelEof(IntPtr sessionHandle, int channelId)
        {
        }

        public void CloseChannel(IntPtr sessionHandle, int channelId)
        {
        }

        public void Close(IntPtr sessionHandle)
        {
        }

        public void SubmitResponse(IntPtr sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data)
        {
        }
    }
}

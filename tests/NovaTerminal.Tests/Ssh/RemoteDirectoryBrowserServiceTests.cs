using NovaTerminal.Core;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Native;
using NovaTerminal.Core.Ssh.Storage;
using NovaTerminal.Models;
using NovaTerminal.Services.Ssh;

namespace NovaTerminal.Tests.Ssh;

public sealed class RemoteDirectoryBrowserServiceTests
{
    [Fact]
    public async Task ListDirectoryAsync_WhenActiveNativeSessionExists_ReturnsDirectoriesBeforeFiles()
    {
        Guid profileId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        var registry = new ActiveSshSessionRegistry();
        registry.Register(new ActiveSshSessionDescriptor(sessionId, profileId, SshBackendKind.Native));

        var interop = new RecordingNativeSshInterop(
            new[]
            {
                new NativeRemotePathEntry("b.txt", "/srv/b.txt", false),
                new NativeRemotePathEntry("alpha", "/srv/alpha", true)
            });

        var service = CreateService(registry, interop, CreateSshService(profileId));
        RemoteSidebarListingResult result = await service.ListDirectoryAsync(profileId, sessionId, "/srv", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Collection(
            result.Entries,
            entry => Assert.Equal("alpha", entry.Name),
            entry => Assert.Equal("b.txt", entry.Name));
    }

    [Fact]
    public async Task ListDirectoryAsync_PreservesModifiedTimeMetadata()
    {
        DateTime modifiedAtUtc = new(2026, 5, 4, 20, 15, 0, DateTimeKind.Utc);
        Guid profileId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        var registry = new ActiveSshSessionRegistry();
        registry.Register(new ActiveSshSessionDescriptor(sessionId, profileId, SshBackendKind.Native));

        var interop = new RecordingNativeSshInterop(
            new[]
            {
                new NativeRemotePathEntry("access.log", "/srv/access.log", false, modifiedAtUtc)
            });

        var service = CreateService(registry, interop, CreateSshService(profileId));
        RemoteSidebarListingResult result = await service.ListDirectoryAsync(profileId, sessionId, "/srv", CancellationToken.None);

        RemoteSidebarEntry entry = Assert.Single(result.Entries);
        Assert.Equal(modifiedAtUtc, entry.ModifiedAtUtc);
    }

    [Fact]
    public async Task ListDirectoryAsync_WhenNoActiveNativeSessionExists_ReturnsFailureWithoutThrowing()
    {
        Guid profileId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        var service = CreateService(
            new ActiveSshSessionRegistry(),
            new RecordingNativeSshInterop(Array.Empty<NativeRemotePathEntry>()),
            CreateSshService(profileId));

        RemoteSidebarListingResult result = await service.ListDirectoryAsync(profileId, sessionId, "/srv", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("/srv", result.ResolvedPath);
        Assert.Empty(result.Entries);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task ListDirectoryAsync_WhenSessionIsNotNative_ReturnsFailureWithoutThrowing()
    {
        Guid profileId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        var registry = new ActiveSshSessionRegistry();
        registry.Register(new ActiveSshSessionDescriptor(sessionId, profileId, SshBackendKind.OpenSsh));
        var service = CreateService(
            registry,
            new RecordingNativeSshInterop(Array.Empty<NativeRemotePathEntry>()),
            CreateSshService(profileId));

        RemoteSidebarListingResult result = await service.ListDirectoryAsync(profileId, sessionId, "/srv", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("/srv", result.ResolvedPath);
        Assert.Empty(result.Entries);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task ListDirectoryAsync_WhenListingFails_ReturnsInlineErrorPayload()
    {
        Guid profileId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        var registry = new ActiveSshSessionRegistry();
        registry.Register(new ActiveSshSessionDescriptor(sessionId, profileId, SshBackendKind.Native));
        var service = CreateService(
            registry,
            new ThrowingNativeSshInterop(new InvalidOperationException("permission denied")),
            CreateSshService(profileId));

        RemoteSidebarListingResult result = await service.ListDirectoryAsync(profileId, sessionId, "/srv", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("/srv", result.ResolvedPath);
        Assert.Empty(result.Entries);
        Assert.Equal("permission denied", result.ErrorMessage);
    }

    [Fact]
    public async Task ListDirectoryAsync_WhenRemotePathIsBlank_NormalizesToHomeDirectory()
    {
        Guid profileId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        var registry = new ActiveSshSessionRegistry();
        registry.Register(new ActiveSshSessionDescriptor(sessionId, profileId, SshBackendKind.Native));
        var interop = new RecordingNativeSshInterop(
            new[]
            {
                new NativeRemotePathEntry("logs", "~/logs", true)
            });
        var service = CreateService(registry, interop, CreateSshService(profileId));

        RemoteSidebarListingResult result = await service.ListDirectoryAsync(profileId, sessionId, "   ", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("~", result.ResolvedPath);
        Assert.Equal("~", interop.LastRemotePath);
    }

    [Fact]
    public async Task ListDirectoryAsync_WhenRemotePathIsNonBlank_PreservesOriginalWhitespace()
    {
        Guid profileId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        var registry = new ActiveSshSessionRegistry();
        registry.Register(new ActiveSshSessionDescriptor(sessionId, profileId, SshBackendKind.Native));
        var interop = new RecordingNativeSshInterop(
            new[]
            {
                new NativeRemotePathEntry("logs", "/srv/logs", true)
            });
        const string remotePath = "  /srv/app  ";
        var service = CreateService(registry, interop, CreateSshService(profileId));

        RemoteSidebarListingResult result = await service.ListDirectoryAsync(profileId, sessionId, remotePath, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(remotePath, result.ResolvedPath);
        Assert.Equal(remotePath, interop.LastRemotePath);
    }

    [Fact]
    public async Task ListDirectoryAsync_PropagatesKeepAliveSettings_ToNativeListingConnection()
    {
        Guid profileId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        var registry = new ActiveSshSessionRegistry();
        registry.Register(new ActiveSshSessionDescriptor(sessionId, profileId, SshBackendKind.Native));
        var interop = new RecordingNativeSshInterop(
            new[]
            {
                new NativeRemotePathEntry("logs", "/srv/logs", true)
            });
        var service = CreateService(
            registry,
            interop,
            CreateSshService(profileId, keepAliveIntervalSeconds: 45, keepAliveCountMax: 6));

        RemoteSidebarListingResult result = await service.ListDirectoryAsync(profileId, sessionId, "/srv", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(interop.LastConnectionOptions);
        Assert.Equal(45, interop.LastConnectionOptions!.KeepAliveIntervalSeconds);
        Assert.Equal(6, interop.LastConnectionOptions.KeepAliveCountMax);
    }

    private static RemoteDirectoryBrowserService CreateService(
        ActiveSshSessionRegistry registry,
        INativeSshInterop interop,
        SshConnectionService sshService,
        Func<TerminalProfile, string?>? passwordResolver = null)
    {
        return new RemoteDirectoryBrowserService(
            interop,
            registry,
            () => sshService,
            passwordResolver);
    }

    private static SshConnectionService CreateSshService(
        Guid profileId,
        int keepAliveIntervalSeconds = 30,
        int keepAliveCountMax = 3)
    {
        var store = new TestSshProfileStore();
        store.SaveProfile(new SshProfile
        {
            Id = profileId,
            Name = "server3",
            BackendKind = SshBackendKind.Native,
            Host = "prod.internal",
            User = "ops",
            Port = 2200,
            AuthMode = SshAuthMode.Default,
            ServerAliveIntervalSeconds = keepAliveIntervalSeconds,
            ServerAliveCountMax = keepAliveCountMax
        });

        return new SshConnectionService(store);
    }

    private sealed class TestSshProfileStore : ISshProfileStore
    {
        private readonly Dictionary<Guid, SshProfile> _profiles = new();

        public IReadOnlyList<SshProfile> GetProfiles() => _profiles.Values.ToList();

        public SshProfile? GetProfile(Guid profileId)
        {
            return _profiles.TryGetValue(profileId, out SshProfile? profile) ? profile : null;
        }

        public void SaveProfile(SshProfile profile)
        {
            _profiles[profile.Id] = profile;
        }

        public bool DeleteProfile(Guid profileId) => _profiles.Remove(profileId);
    }

    private sealed class RecordingNativeSshInterop : INativeSshInterop
    {
        private readonly IReadOnlyList<NativeRemotePathEntry> _entries;

        public RecordingNativeSshInterop(IReadOnlyList<NativeRemotePathEntry> entries)
        {
            _entries = entries;
        }

        public string? LastRemotePath { get; private set; }
        public NativeSshConnectionOptions? LastConnectionOptions { get; private set; }

        public IntPtr Connect(NativeSshConnectionOptions options) => throw new NotSupportedException();

        public IReadOnlyList<NativeRemotePathEntry> ListRemoteDirectory(
            NativeSshConnectionOptions connectionOptions,
            string remotePath,
            CancellationToken cancellationToken)
        {
            LastConnectionOptions = connectionOptions;
            LastRemotePath = remotePath;
            return _entries;
        }

        public void RunSftpTransfer(NativeSshConnectionOptions connectionOptions, NativeSftpTransferOptions transferOptions, Action<NativeSftpTransferProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public NativeSshEvent? PollEvent(IntPtr sessionHandle) => throw new NotSupportedException();
        public void Write(IntPtr sessionHandle, ReadOnlySpan<byte> data) => throw new NotSupportedException();
        public void Resize(IntPtr sessionHandle, int cols, int rows) => throw new NotSupportedException();
        public int OpenDirectTcpIp(IntPtr sessionHandle, NativePortForwardOpenOptions options) => throw new NotSupportedException();
        public void WriteChannel(IntPtr sessionHandle, int channelId, ReadOnlySpan<byte> data) => throw new NotSupportedException();
        public void SendChannelEof(IntPtr sessionHandle, int channelId) => throw new NotSupportedException();
        public void CloseChannel(IntPtr sessionHandle, int channelId) => throw new NotSupportedException();
        public void SubmitResponse(IntPtr sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data) => throw new NotSupportedException();
        public void Close(IntPtr sessionHandle) => throw new NotSupportedException();
    }

    private sealed class ThrowingNativeSshInterop : INativeSshInterop
    {
        private readonly Exception _exception;

        public ThrowingNativeSshInterop(Exception exception)
        {
            _exception = exception;
        }

        public IntPtr Connect(NativeSshConnectionOptions options) => throw new NotSupportedException();

        public IReadOnlyList<NativeRemotePathEntry> ListRemoteDirectory(
            NativeSshConnectionOptions connectionOptions,
            string remotePath,
            CancellationToken cancellationToken)
        {
            throw _exception;
        }

        public void RunSftpTransfer(NativeSshConnectionOptions connectionOptions, NativeSftpTransferOptions transferOptions, Action<NativeSftpTransferProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public NativeSshEvent? PollEvent(IntPtr sessionHandle) => throw new NotSupportedException();
        public void Write(IntPtr sessionHandle, ReadOnlySpan<byte> data) => throw new NotSupportedException();
        public void Resize(IntPtr sessionHandle, int cols, int rows) => throw new NotSupportedException();
        public int OpenDirectTcpIp(IntPtr sessionHandle, NativePortForwardOpenOptions options) => throw new NotSupportedException();
        public void WriteChannel(IntPtr sessionHandle, int channelId, ReadOnlySpan<byte> data) => throw new NotSupportedException();
        public void SendChannelEof(IntPtr sessionHandle, int channelId) => throw new NotSupportedException();
        public void CloseChannel(IntPtr sessionHandle, int channelId) => throw new NotSupportedException();
        public void SubmitResponse(IntPtr sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data) => throw new NotSupportedException();
        public void Close(IntPtr sessionHandle) => throw new NotSupportedException();
    }
}

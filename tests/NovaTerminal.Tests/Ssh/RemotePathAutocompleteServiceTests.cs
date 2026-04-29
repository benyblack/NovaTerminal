using NovaTerminal.Core;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Native;
using NovaTerminal.Core.Ssh.Storage;
using NovaTerminal.Models;
using NovaTerminal.Services.Ssh;

namespace NovaTerminal.Tests.Ssh;

public sealed class RemotePathAutocompleteServiceTests
{
    [Fact]
    public async Task GetSuggestionsAsync_WhenNoActiveNativeSessionExists_ReturnsEmpty()
    {
        Guid profileId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        var registry = new ActiveSshSessionRegistry();
        var service = CreateService(
            registry,
            new RecordingNativeSshInterop(Array.Empty<NativeRemotePathEntry>()),
            CreateSshService(profileId));

        IReadOnlyList<RemotePathSuggestion> results = await service.GetSuggestionsAsync(
            profileId,
            sessionId,
            "/mnt/media/mov",
            CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetSuggestionsAsync_FiltersNativeEntriesUsingResolvedParentPath()
    {
        Guid profileId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        var registry = new ActiveSshSessionRegistry();
        registry.Register(new ActiveSshSessionDescriptor(sessionId, profileId, SshBackendKind.Native));
        var interop = new RecordingNativeSshInterop(
            new[]
            {
                new NativeRemotePathEntry("movies", "/mnt/media/movies", true),
                new NativeRemotePathEntry("music", "/mnt/media/music", true)
            });
        var service = CreateService(registry, interop, CreateSshService(profileId));

        IReadOnlyList<RemotePathSuggestion> results = await service.GetSuggestionsAsync(
            profileId,
            sessionId,
            "/mnt/media/mov",
            CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("/mnt/media", interop.LastRemotePath);
        Assert.Equal("/mnt/media/movies", results[0].FullPath);
    }

    [Fact]
    public async Task GetSuggestionsAsync_UsesResolvedPassword_ForPasswordBasedProfiles()
    {
        Guid profileId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        var registry = new ActiveSshSessionRegistry();
        registry.Register(new ActiveSshSessionDescriptor(sessionId, profileId, SshBackendKind.Native));
        var interop = new RecordingNativeSshInterop(
            new[]
            {
                new NativeRemotePathEntry("movies", "/mnt/media/movies", true)
            });
        var service = CreateService(
            registry,
            interop,
            CreateSshService(profileId),
            _ => "top-secret");

        await service.GetSuggestionsAsync(
            profileId,
            sessionId,
            "/mnt/media/mov",
            CancellationToken.None);

        Assert.NotNull(interop.LastConnectionOptions);
        Assert.Equal("top-secret", interop.LastConnectionOptions!.Password);
    }

    [Fact]
    public async Task GetSuggestionsAsync_UsesRuntimeSessionPassword_WhenVaultPasswordIsUnavailable()
    {
        Guid profileId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        var registry = new ActiveSshSessionRegistry();
        registry.Register(new ActiveSshSessionDescriptor(sessionId, profileId, SshBackendKind.Native));
        registry.SetRuntimePassword(sessionId, "runtime-secret");
        var interop = new RecordingNativeSshInterop(
            new[]
            {
                new NativeRemotePathEntry("movies", "/mnt/media/movies", true)
            });
        var service = CreateService(
            registry,
            interop,
            CreateSshService(profileId),
            _ => null);

        await service.GetSuggestionsAsync(
            profileId,
            sessionId,
            "/mnt/media/mov",
            CancellationToken.None);

        Assert.NotNull(interop.LastConnectionOptions);
        Assert.Equal("runtime-secret", interop.LastConnectionOptions!.Password);
    }

    [Fact]
    public async Task GetSuggestionsAsync_SetsKnownHostsPath_ForNativeListing()
    {
        Guid profileId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        var registry = new ActiveSshSessionRegistry();
        registry.Register(new ActiveSshSessionDescriptor(sessionId, profileId, SshBackendKind.Native));
        var interop = new RecordingNativeSshInterop(
            new[]
            {
                new NativeRemotePathEntry("code", "/home/nova/code", true)
            });
        var service = CreateService(
            registry,
            interop,
            CreateSshService(profileId),
            _ => null);

        await service.GetSuggestionsAsync(
            profileId,
            sessionId,
            "~/cod",
            CancellationToken.None);

        Assert.NotNull(interop.LastConnectionOptions);
        Assert.Equal(AppPaths.NativeKnownHostsFilePath, interop.LastConnectionOptions!.KnownHostsFilePath);
    }

    [Fact]
    public async Task GetSuggestionsAsync_WhenPathEndsWithSeparator_ListsDirectoryChildren()
    {
        Guid profileId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        var registry = new ActiveSshSessionRegistry();
        registry.Register(new ActiveSshSessionDescriptor(sessionId, profileId, SshBackendKind.Native));
        var interop = new RecordingNativeSshInterop(
            new[]
            {
                new NativeRemotePathEntry("server", "/home/nova/code/server", true)
            });
        var service = CreateService(
            registry,
            interop,
            CreateSshService(profileId),
            _ => null);

        IReadOnlyList<RemotePathSuggestion> results = await service.GetSuggestionsAsync(
            profileId,
            sessionId,
            "~/code/",
            CancellationToken.None);

        Assert.Equal("~/code", interop.LastRemotePath);
        Assert.Single(results);
        Assert.Equal("server", results[0].DisplayName);
    }

    [Fact]
    public async Task GetSuggestionsAsync_WhenPrefixIsEmpty_ReturnsTopCappedEntries()
    {
        Guid profileId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        var registry = new ActiveSshSessionRegistry();
        registry.Register(new ActiveSshSessionDescriptor(sessionId, profileId, SshBackendKind.Native));
        var entries = Enumerable.Range(1, 20)
            .Select(index => new NativeRemotePathEntry($"item{index:00}", $"/home/nova/code/item{index:00}", index % 2 == 0))
            .ToArray();
        var interop = new RecordingNativeSshInterop(entries);
        var service = CreateService(
            registry,
            interop,
            CreateSshService(profileId),
            _ => null);

        IReadOnlyList<RemotePathSuggestion> results = await service.GetSuggestionsAsync(
            profileId,
            sessionId,
            "~/code/",
            CancellationToken.None);

        Assert.Equal(12, results.Count);
    }

    [Fact]
    public async Task GetSuggestionsAsync_ReturnsBeforeBlockingNativeLookupCompletes()
    {
        Guid profileId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        var registry = new ActiveSshSessionRegistry();
        registry.Register(new ActiveSshSessionDescriptor(sessionId, profileId, SshBackendKind.Native));
        var interop = new BlockingNativeSshInterop(
            new[]
            {
                new NativeRemotePathEntry("code", "/home/nova/code", true)
            });
        var service = CreateService(
            registry,
            interop,
            CreateSshService(profileId),
            _ => null);

        Task<IReadOnlyList<RemotePathSuggestion>>? returnedTask = null;
        Task invocation = Task.Run(() =>
        {
            returnedTask = service.GetSuggestionsAsync(
                profileId,
                sessionId,
                "~/cod",
                CancellationToken.None);
        });

        Assert.True(interop.Started.Wait(TimeSpan.FromSeconds(1)));
        Task completed = await Task.WhenAny(invocation, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(invocation, completed);
        Assert.NotNull(returnedTask);
        Assert.False(returnedTask!.IsCompleted);

        interop.Release.Set();

        IReadOnlyList<RemotePathSuggestion> results = await returnedTask;

        Assert.Single(results);
        Assert.Equal("/home/nova/code", results[0].FullPath);
    }

    private static RemotePathAutocompleteService CreateService(
        ActiveSshSessionRegistry registry,
        INativeSshInterop interop,
        SshConnectionService sshService,
        Func<TerminalProfile, string?>? passwordResolver = null)
    {
        return new RemotePathAutocompleteService(
            interop,
            registry,
            () => sshService,
            passwordResolver);
    }

    private static SshConnectionService CreateSshService(Guid profileId)
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
            AuthMode = SshAuthMode.Default
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

    private sealed class BlockingNativeSshInterop : INativeSshInterop
    {
        private readonly IReadOnlyList<NativeRemotePathEntry> _entries;

        public BlockingNativeSshInterop(IReadOnlyList<NativeRemotePathEntry> entries)
        {
            _entries = entries;
        }

        public ManualResetEventSlim Started { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);

        public IntPtr Connect(NativeSshConnectionOptions options) => throw new NotSupportedException();

        public IReadOnlyList<NativeRemotePathEntry> ListRemoteDirectory(
            NativeSshConnectionOptions connectionOptions,
            string remotePath,
            CancellationToken cancellationToken)
        {
            Started.Set();
            Release.Wait(cancellationToken);
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
}

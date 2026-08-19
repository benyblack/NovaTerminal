using System.Collections.Concurrent;
using System.Text;
using NovaTerminal.Platform.Ssh.Models;
using NovaTerminal.Platform.Ssh.Native;
using NovaTerminal.Platform.Ssh.Sessions;

namespace NovaTerminal.Platform.Tests.Ssh;

public sealed class NativeSshSessionTests
{
    [Fact]
    public async Task OutputBytesAreDecodedIncrementallyAcrossPollEvents()
    {
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(NativeSshEvent.Data(new byte[] { 0xE2, 0x82 }));
        interop.Enqueue(NativeSshEvent.Data(new byte[] { 0xAC }));
        interop.Enqueue(NativeSshEvent.ExitStatus(0));
        interop.Enqueue(NativeSshEvent.Closed(Array.Empty<byte>()));

        var outputs = new List<string>();
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        session.OnOutputReceived += outputs.Add;
        session.OnExit += code => exit.TrySetResult(code);

        Assert.Equal(0, await exit.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(new[] { "€" }, outputs);
    }

    [Fact]
    public async Task EarlyOutput_IsBufferedAndReplayedToLateSubscriber_InOrder()
    {
        // Regression: output produced before the first subscriber attaches must be
        // buffered and replayed in order — not dropped, and never delivered
        // concurrently with the replay (the invocation-lock hardening).
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("first ")));
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("second")));
        interop.Enqueue(NativeSshEvent.ExitStatus(0));
        interop.Enqueue(NativeSshEvent.Closed(Array.Empty<byte>()));

        using var session = new NativeSshSession(CreateProfile(), interop: interop);

        // Signal-driven, not clock-driven: once the session has exited, the poll
        // loop has fully processed both Data events (decoded and buffered), so a
        // late output subscriber must receive them via replay — never live. OnExit
        // replays its code to a late subscriber, so subscribing here is race-free.
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnExit += code => exit.TrySetResult(code);
        Assert.Equal(0, await exit.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        var outputs = new ConcurrentQueue<string>();
        session.OnOutputReceived += outputs.Enqueue; // late subscriber → replay fires here

        await WaitUntilAsync(() => string.Concat(outputs) == "first second");
        Assert.Equal("first second", string.Concat(outputs));
    }

    [Fact]
    public void Constructor_AllowsDynamicForwardProfiles()
    {
        SshProfile profile = CreateProfile();
        profile.Forwards.Add(new PortForward
        {
            Kind = PortForwardKind.Dynamic,
            BindAddress = "127.0.0.1",
            SourcePort = 0
        });

        using var session = new NativeSshSession(profile, interop: new FakeNativeSshInterop());

        Assert.NotNull(session);
    }

    [Fact]
    public async Task SendInputForwardsUtf8BytesThroughInterop()
    {
        var interop = new FakeNativeSshInterop();
        using var session = new NativeSshSession(CreateProfile(), interop: interop);

        session.SendInput("echo €\n");

        await WaitUntilAsync(() => interop.Writes.Count > 0);
        Assert.Equal(Encoding.UTF8.GetBytes("echo €\n"), interop.Writes.Single());
    }

    [Fact]
    public async Task ResizeForwardsTerminalDimensions()
    {
        var interop = new FakeNativeSshInterop();
        using var session = new NativeSshSession(CreateProfile(), interop: interop);

        session.Resize(132, 43);

        await WaitUntilAsync(() => interop.Resizes.Count > 0);
        Assert.Equal((132, 43), interop.Resizes.Single());
    }

    [Fact]
    public async Task ResizeBurst_RecordsLatestDimensionsAsEffectiveInteropIntent()
    {
        var interop = new FakeNativeSshInterop();
        using var session = new NativeSshSession(CreateProfile(), interop: interop);

        session.Resize(120, 30);
        session.Resize(140, 40);
        session.Resize(160, 50);

        await WaitUntilAsync(() => interop.Resizes.Count >= 3);

        Assert.Equal(3, interop.Resizes.Count);
        Assert.Equal((160, 50), interop.Resizes[^1]);
    }

    [Fact]
    public async Task ExitAndClosedEventsOnlyRaiseOnExitOnce()
    {
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(NativeSshEvent.ExitStatus(23));
        interop.Enqueue(NativeSshEvent.Closed(Array.Empty<byte>()));

        var exits = new List<int>();
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        session.OnExit += code =>
        {
            exits.Add(code);
            exit.TrySetResult(code);
        };

        Assert.Equal(23, await exit.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await WaitUntilAsync(() => interop.CloseCallCount > 0);
        Assert.Equal(new[] { 23 }, exits);
    }

    [Fact]
    public async Task LateOnExitSubscriberReceivesRecordedExitCode()
    {
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(NativeSshEvent.ExitStatus(17));
        interop.Enqueue(NativeSshEvent.Closed(Array.Empty<byte>()));
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        await WaitUntilAsync(() => interop.CloseCallCount > 0);

        session.OnExit += code => exit.TrySetResult(code);

        Assert.Equal(17, await exit.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task LateOutputSubscriberReceivesBufferedOutput()
    {
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("hello")));
        interop.Enqueue(NativeSshEvent.ExitStatus(0));
        interop.Enqueue(NativeSshEvent.Closed(Array.Empty<byte>()));
        var outputs = new List<string>();

        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        await WaitUntilAsync(() => interop.CloseCallCount > 0);

        session.OnOutputReceived += outputs.Add;

        Assert.Equal(new[] { "hello" }, outputs);
    }

    [Fact]
    public async Task DisposeClosesNativeHandleAndStopsPollLoop()
    {
        var interop = new FakeNativeSshInterop();
        var session = new NativeSshSession(CreateProfile(), interop: interop);

        session.Dispose();

        await WaitUntilAsync(() => interop.CloseCallCount > 0);
        Assert.Equal(1, interop.CloseCallCount);
    }

    [Fact]
    public async Task Connect_UsesProfileKeepAliveSettingsForNativeSession()
    {
        var interop = new FakeNativeSshInterop();
        SshProfile profile = CreateProfile();
        profile.ServerAliveIntervalSeconds = 15;
        profile.ServerAliveCountMax = 7;

        using var session = new NativeSshSession(profile, interop: interop);

        await WaitUntilAsync(() => interop.LastConnectOptions != null);

        Assert.NotNull(interop.LastConnectOptions);
        Assert.Equal(15, interop.LastConnectOptions!.KeepAliveIntervalSeconds);
        Assert.Equal(7, interop.LastConnectOptions.KeepAliveCountMax);
    }

    [Fact]
    public async Task Connect_WithAutoRemoteShellKind_PassesProbeAndSupportedBootstraps()
    {
        var interop = new FakeNativeSshInterop();
        SshProfile profile = CreateProfile();
        profile.RemoteShellKind = RemoteShellKind.Auto;

        using var session = new NativeSshSession(profile, interop: interop);

        await WaitUntilAsync(() => interop.LastConnectOptions != null);

        Assert.NotNull(interop.LastConnectOptions);
        Assert.Equal(RemoteShellKind.Auto, interop.LastConnectOptions!.RemoteShellKind);
        Assert.Contains("sh -lc", interop.LastConnectOptions.ShellDetectionCommand, StringComparison.Ordinal);
        Assert.Contains("PROMPT_COMMAND", interop.LastConnectOptions.BashCwdBootstrap, StringComparison.Ordinal);
        Assert.Contains("add-zsh-hook precmd", interop.LastConnectOptions.ZshCwdBootstrap, StringComparison.Ordinal);
        Assert.Contains("fish_prompt", interop.LastConnectOptions.FishCwdBootstrap, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_WithExplicitBashRemoteShellKind_SkipsProbeButPassesBootstrap()
    {
        var interop = new FakeNativeSshInterop();
        SshProfile profile = CreateProfile();
        profile.RemoteShellKind = RemoteShellKind.Bash;

        using var session = new NativeSshSession(profile, interop: interop);

        await WaitUntilAsync(() => interop.LastConnectOptions != null);

        Assert.NotNull(interop.LastConnectOptions);
        Assert.Equal(RemoteShellKind.Bash, interop.LastConnectOptions!.RemoteShellKind);
        Assert.Null(interop.LastConnectOptions.ShellDetectionCommand);
        Assert.Contains("PROMPT_COMMAND", interop.LastConnectOptions.BashCwdBootstrap, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsolicitedIncomingForwardChannel_WithNoForwardsConfigured_IsClosedNotLeaked()
    {
        // A profile with no forwards has no NativePortForwardSession, so before this fix the
        // announcement was silently dropped — the channel stayed registered and open on the
        // native side, and a hostile server could grow that without bound. The event must be
        // answered with a close, not ignored.
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(NativeSshEvent.ForwardChannelIncoming(
            41,
            Encoding.UTF8.GetBytes("{\"connectedAddress\":\"localhost\",\"connectedPort\":18080,\"originatorAddress\":\"192.0.2.1\",\"originatorPort\":50000}")));

        using var session = new NativeSshSession(CreateProfile(), interop: interop);

        await WaitUntilAsync(() => interop.ClosedChannelIds.Contains(41));
    }

    [Fact]
    public async Task RemoteForward_IsRequestedWhenTheConnectedEventArrives()
    {
        var interop = new FakeNativeSshInterop();
        SshProfile profile = CreateProfile();
        profile.Forwards.Add(new PortForward
        {
            Kind = PortForwardKind.Remote,
            SourcePort = 18081,
            DestinationHost = "127.0.0.1",
            DestinationPort = 9000
        });

        using var session = new NativeSshSession(profile, interop: interop);

        // Nothing to request until the session exists; the Connected event is what says it does.
        await Task.Delay(100);
        Assert.Empty(interop.RemoteForwardRequests);

        interop.Enqueue(new NativeSshEvent(NativeSshEventKind.Connected, []));

        await WaitUntilAsync(() => interop.RemoteForwardRequests.Count == 1);
        Assert.Equal(("localhost", 18081), interop.RemoteForwardRequests[0]);
    }

    [Fact]
    public async Task RemoteForward_ARefusedRequestWarnsInTheTerminal()
    {
        // ssh -R prints "Warning: remote port forwarding failed" into the session; the native
        // backend keeps the session alive the same way, so the warning must be equally visible.
        var interop = new FakeNativeSshInterop { RefuseRemoteForwards = true };
        SshProfile profile = CreateProfile();
        profile.Forwards.Add(new PortForward
        {
            Kind = PortForwardKind.Remote,
            SourcePort = 18082,
            DestinationHost = "127.0.0.1",
            DestinationPort = 9001
        });

        var outputs = new ConcurrentQueue<string>();
        using var session = new NativeSshSession(profile, interop: interop);
        session.OnOutputReceived += outputs.Enqueue;

        interop.Enqueue(new NativeSshEvent(NativeSshEventKind.Connected, []));

        await WaitUntilAsync(() => string.Concat(outputs).Contains("localhost:18082", StringComparison.Ordinal));
        Assert.Contains("Warning", string.Concat(outputs), StringComparison.Ordinal);
    }

    private static SshProfile CreateProfile()
    {
        return new SshProfile
        {
            Id = Guid.Parse("2f56e099-14f4-4219-9b64-fd16465d84fb"),
            BackendKind = SshBackendKind.Native,
            Host = "native.example",
            User = "nova",
            Port = 22
        };
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(predicate(), "Condition was not met before timeout.");
    }

    internal sealed class FakeNativeSshInterop : INativeSshInterop
    {
        private readonly ConcurrentQueue<NativeSshEvent> _events = new();
        private int _nextHandle = 1;

        public List<byte[]> Writes { get; } = new();
        public List<(int Cols, int Rows)> Resizes { get; } = new();
        public int CloseCallCount { get; private set; }
        public NativeSshConnectionOptions? LastConnectOptions { get; private set; }
        public Exception? ResizeException { get; set; }
        public bool RefuseRemoteForwards { get; set; }

        // Written from the poll loop / request task while tests poll them; ConcurrentQueue keeps
        // the reads safe without a lock.
        private readonly ConcurrentQueue<int> _closedChannelIds = new();
        private readonly ConcurrentQueue<(string BindAddress, int Port)> _remoteForwardRequests = new();

        public IReadOnlyList<int> ClosedChannelIds => _closedChannelIds.ToArray();
        public IReadOnlyList<(string BindAddress, int Port)> RemoteForwardRequests => _remoteForwardRequests.ToArray();

        public NovaSshSafeHandle Connect(NativeSshConnectionOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            LastConnectOptions = options;
            return new NovaSshSafeHandle(new IntPtr(_nextHandle++), ownsHandle: false);
        }

        public void RunSftpTransfer(NativeSshConnectionOptions connectionOptions, NativeSftpTransferOptions transferOptions, Action<NativeSftpTransferProgress>? progress, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public IReadOnlyList<NativeRemotePathEntry> ListRemoteDirectory(NativeSshConnectionOptions connectionOptions, string remotePath, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public NativeSshEvent? PollEvent(NovaSshSafeHandle sessionHandle)
        {
            if (sessionHandle is null || sessionHandle.IsInvalid)
            {
                throw new InvalidOperationException("Unexpected null handle.");
            }

            return _events.TryDequeue(out NativeSshEvent? nextEvent)
                ? nextEvent
                : null;
        }

        public void Write(NovaSshSafeHandle sessionHandle, ReadOnlySpan<byte> data)
        {
            Writes.Add(data.ToArray());
        }

        public void Resize(NovaSshSafeHandle sessionHandle, int cols, int rows)
        {
            if (ResizeException != null)
            {
                throw ResizeException;
            }

            Resizes.Add((cols, rows));
        }

        public int OpenDirectTcpIp(NovaSshSafeHandle sessionHandle, NativePortForwardOpenOptions options)
        {
            return 1;
        }

        public void WriteChannel(NovaSshSafeHandle sessionHandle, int channelId, ReadOnlySpan<byte> data)
        {
        }

        public void SendChannelEof(NovaSshSafeHandle sessionHandle, int channelId)
        {
        }

        public void CloseChannel(NovaSshSafeHandle sessionHandle, int channelId)
        {
            _closedChannelIds.Enqueue(channelId);
        }

        public int RequestRemoteForward(NovaSshSafeHandle sessionHandle, string bindAddress, int port)
        {
            if (RefuseRemoteForwards)
            {
                throw new InvalidOperationException($"The server refused the remote forward on {bindAddress}:{port}.");
            }

            _remoteForwardRequests.Enqueue((bindAddress, port));
            return port;
        }

        public void Close(NovaSshSafeHandle sessionHandle)
        {
            CloseCallCount++;
        }

        public void SubmitResponse(NovaSshSafeHandle sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data)
        {
        }

        public void Enqueue(NativeSshEvent nextEvent)
        {
            _events.Enqueue(nextEvent);
        }
    }
}

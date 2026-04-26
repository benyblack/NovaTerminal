using System.Collections.Concurrent;
using System.Text;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Native;
using NovaTerminal.Core.Ssh.Sessions;

namespace NovaTerminal.Core.Tests.Ssh;

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

        public IntPtr Connect(NativeSshConnectionOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            LastConnectOptions = options;
            return new IntPtr(_nextHandle++);
        }

        public void RunSftpTransfer(NativeSshConnectionOptions connectionOptions, NativeSftpTransferOptions transferOptions, Action<NativeSftpTransferProgress>? progress, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public NativeSshEvent? PollEvent(IntPtr sessionHandle)
        {
            if (sessionHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Unexpected null handle.");
            }

            return _events.TryDequeue(out NativeSshEvent? nextEvent)
                ? nextEvent
                : null;
        }

        public void Write(IntPtr sessionHandle, ReadOnlySpan<byte> data)
        {
            Writes.Add(data.ToArray());
        }

        public void Resize(IntPtr sessionHandle, int cols, int rows)
        {
            if (ResizeException != null)
            {
                throw ResizeException;
            }

            Resizes.Add((cols, rows));
        }

        public int OpenDirectTcpIp(IntPtr sessionHandle, NativePortForwardOpenOptions options)
        {
            return 1;
        }

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
            CloseCallCount++;
        }

        public void SubmitResponse(IntPtr sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data)
        {
        }

        public void Enqueue(NativeSshEvent nextEvent)
        {
            _events.Enqueue(nextEvent);
        }
    }
}

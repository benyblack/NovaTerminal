using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Native;

namespace NovaTerminal.Core.Tests.Ssh;

public sealed class NativePortForwardSessionTests
{
    [Fact]
    public async Task LocalForwardListenersBindFromConfiguredForwards()
    {
        var interop = new FakeNativeSshInterop();
        int firstPort = GetFreePort();
        int secondPort = GetFreePort();

        using var session = new NativePortForwardSession(
            new IntPtr(7),
            [
                CreateForward(firstPort, "svc-one.internal", 8080),
                CreateForward(secondPort, "svc-two.internal", 9090)
            ],
            interop);

        using TcpClient firstClient = await ConnectLoopbackAsync(firstPort);
        using TcpClient secondClient = await ConnectLoopbackAsync(secondPort);

        await WaitUntilAsync(() => interop.OpenRequests.Count == 2);

        Assert.Contains(interop.OpenRequests, request =>
            request.HostToConnect == "svc-one.internal" &&
            request.PortToConnect == 8080 &&
            request.OriginatorAddress == "127.0.0.1" &&
            request.OriginatorPort > 0);
        Assert.Contains(interop.OpenRequests, request =>
            request.HostToConnect == "svc-two.internal" &&
            request.PortToConnect == 9090 &&
            request.OriginatorAddress == "127.0.0.1" &&
            request.OriginatorPort > 0);
    }

    [Fact]
    public async Task MultipleForwardConnectionsCanCoexist()
    {
        var interop = new FakeNativeSshInterop();
        int listenPort = GetFreePort();

        using var session = new NativePortForwardSession(
            new IntPtr(9),
            [CreateForward(listenPort, "svc.internal", 7000)],
            interop);

        using TcpClient firstClient = await ConnectLoopbackAsync(listenPort);
        using TcpClient secondClient = await ConnectLoopbackAsync(listenPort);

        await WaitUntilAsync(() => interop.OpenRequests.Count == 2);
        Assert.Equal(2, interop.OpenRequests.Count);
    }

    [Fact]
    public async Task DisposeClosesForwardChannelsAndListeners()
    {
        var interop = new FakeNativeSshInterop();
        int listenPort = GetFreePort();

        var session = new NativePortForwardSession(
            new IntPtr(11),
            [CreateForward(listenPort, "svc.internal", 5432)],
            interop);

        using TcpClient client = await ConnectLoopbackAsync(listenPort);
        await WaitUntilAsync(() => interop.OpenRequests.Count == 1);

        session.Dispose();

        await WaitUntilAsync(() => interop.ClosedChannelIds.Count == 1);
        Assert.Single(interop.ClosedChannelIds);
        await Assert.ThrowsAnyAsync<SocketException>(() => ConnectLoopbackAsync(listenPort));
    }

    [Fact]
    public void Constructor_WhenAnyBindFails_ThrowsDeterministicallyAndCleansUpEarlierListeners()
    {
        int firstPort = GetFreePort();
        int occupiedPort = GetFreePort();
        using var occupied = new TcpListener(IPAddress.Loopback, occupiedPort);
        occupied.Start();

        var interop = new FakeNativeSshInterop();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            new NativePortForwardSession(
                new IntPtr(13),
                [
                    CreateForward(firstPort, "svc-one.internal", 80),
                    CreateForward(occupiedPort, "svc-two.internal", 81)
                ],
                interop));

        Assert.Contains(occupiedPort.ToString(), ex.Message, StringComparison.Ordinal);

        using var probe = new TcpListener(IPAddress.Loopback, firstPort);
        probe.Start();
    }

    private static PortForward CreateForward(int sourcePort, string destinationHost, int destinationPort)
    {
        return new PortForward
        {
            Kind = PortForwardKind.Local,
            BindAddress = "127.0.0.1",
            SourcePort = sourcePort,
            DestinationHost = destinationHost,
            DestinationPort = destinationPort
        };
    }

    private static async Task<TcpClient> ConnectLoopbackAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
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

    private sealed class FakeNativeSshInterop : INativeSshInterop
    {
        private readonly ConcurrentQueue<NativeSshEvent> _events = new();
        private int _nextChannelId = 100;

        public List<NativePortForwardOpenOptions> OpenRequests { get; } = [];
        public List<int> ClosedChannelIds { get; } = [];

        public IntPtr Connect(NativeSshConnectionOptions options) => new(1);

        public NativeSshEvent? PollEvent(IntPtr sessionHandle)
        {
            return _events.TryDequeue(out NativeSshEvent? nextEvent)
                ? nextEvent
                : null;
        }

        public void Write(IntPtr sessionHandle, ReadOnlySpan<byte> data)
        {
        }

        public void Resize(IntPtr sessionHandle, int cols, int rows)
        {
        }

        public void SubmitResponse(IntPtr sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data)
        {
        }

        public int OpenDirectTcpIp(IntPtr sessionHandle, NativePortForwardOpenOptions options)
        {
            OpenRequests.Add(options);
            return Interlocked.Increment(ref _nextChannelId);
        }

        public void WriteChannel(IntPtr sessionHandle, int channelId, ReadOnlySpan<byte> data)
        {
        }

        public void SendChannelEof(IntPtr sessionHandle, int channelId)
        {
        }

        public void CloseChannel(IntPtr sessionHandle, int channelId)
        {
            ClosedChannelIds.Add(channelId);
        }

        public void Close(IntPtr sessionHandle)
        {
        }
    }
}

using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
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

    [Fact]
    public async Task DynamicForward_SocksConnectOpensRequestedTargetChannel()
    {
        var interop = new FakeNativeSshInterop();
        int listenPort = GetFreePort();

        using var session = new NativePortForwardSession(
            new IntPtr(15),
            [CreateDynamicForward(listenPort)],
            interop);

        using TcpClient client = await ConnectLoopbackAsync(listenPort);
        NetworkStream stream = client.GetStream();

        await SendSocksGreetingAsync(stream);
        await ReadExactlyAsync(stream, 2, TimeSpan.FromSeconds(2));

        await SendSocksConnectRequestAsync(stream, "svc.internal", 443);
        byte[] reply = await ReadExactlyAsync(stream, 10, TimeSpan.FromSeconds(2));

        await WaitUntilAsync(() => interop.OpenRequests.Count == 1);

        Assert.Equal(new byte[] { 0x05, 0x00 }, reply[..2]);
        Assert.Single(interop.OpenRequests);
        Assert.Equal("svc.internal", interop.OpenRequests[0].HostToConnect);
        Assert.Equal(443, interop.OpenRequests[0].PortToConnect);
    }

    [Fact]
    public async Task LocalAndDynamicForwardsCanStartTogether()
    {
        var interop = new FakeNativeSshInterop();
        int localPort = GetFreePort();
        int dynamicPort = GetFreePort();

        using var session = new NativePortForwardSession(
            new IntPtr(17),
            [
                CreateForward(localPort, "svc-one.internal", 8080),
                CreateDynamicForward(dynamicPort)
            ],
            interop);

        using TcpClient localClient = await ConnectLoopbackAsync(localPort);
        await WaitUntilAsync(() => interop.OpenRequests.Count == 1);

        Assert.Single(interop.OpenRequests);
        Assert.Equal("svc-one.internal", interop.OpenRequests[0].HostToConnect);
        Assert.Equal(8080, interop.OpenRequests[0].PortToConnect);

        using TcpClient dynamicClient = await ConnectLoopbackAsync(dynamicPort);
        NetworkStream stream = dynamicClient.GetStream();

        await SendSocksGreetingAsync(stream);
        await ReadExactlyAsync(stream, 2, TimeSpan.FromSeconds(2));

        await SendSocksConnectRequestAsync(stream, "svc-dynamic.internal", 8443);
        byte[] reply = await ReadExactlyAsync(stream, 10, TimeSpan.FromSeconds(2));

        await WaitUntilAsync(() => interop.OpenRequests.Count == 2);

        Assert.Equal(new byte[] { 0x05, 0x00 }, reply[..2]);
        Assert.Equal(2, interop.OpenRequests.Count);
        Assert.Contains(interop.OpenRequests, request =>
            request.HostToConnect == "svc-dynamic.internal" &&
            request.PortToConnect == 8443);
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

    private static PortForward CreateDynamicForward(int sourcePort)
    {
        return new PortForward
        {
            Kind = PortForwardKind.Dynamic,
            BindAddress = "127.0.0.1",
            SourcePort = sourcePort
        };
    }

    private static async Task<TcpClient> ConnectLoopbackAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client;
    }

    private static async Task SendSocksGreetingAsync(NetworkStream stream)
    {
        byte[] greeting = [0x05, 0x01, 0x00];
        await stream.WriteAsync(greeting);
        await stream.FlushAsync();
    }

    private static async Task SendSocksConnectRequestAsync(NetworkStream stream, string host, int port)
    {
        byte[] hostBytes = Encoding.ASCII.GetBytes(host);
        byte[] request = new byte[7 + hostBytes.Length];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = 0x03;
        request[4] = (byte)hostBytes.Length;
        Buffer.BlockCopy(hostBytes, 0, request, 5, hostBytes.Length);
        request[5 + hostBytes.Length] = (byte)((port >> 8) & 0xFF);
        request[6 + hostBytes.Length] = (byte)(port & 0xFF);

        await stream.WriteAsync(request);
        await stream.FlushAsync();
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int length, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        byte[] buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cts.Token);
            if (read == 0)
            {
                throw new EndOfStreamException("Socket closed before expected bytes were read.");
            }

            offset += read;
        }

        return buffer;
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

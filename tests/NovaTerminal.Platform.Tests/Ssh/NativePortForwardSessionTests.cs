using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using NovaTerminal.Platform.Ssh.Models;
using NovaTerminal.Platform.Ssh.Native;

namespace NovaTerminal.Platform.Tests.Ssh;

public sealed class NativePortForwardSessionTests
{
    [Fact]
    public async Task LocalForwardListenersBindFromConfiguredForwards()
    {
        var interop = new FakeNativeSshInterop();
        int firstPort = GetFreePort();
        int secondPort = GetFreePort();

        using var session = new NativePortForwardSession(
            FakeHandle(7),
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
            FakeHandle(9),
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
            FakeHandle(11),
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
                FakeHandle(13),
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
            FakeHandle(15),
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
    public async Task DynamicForward_SocksConnectWithIpv4TargetOpensRequestedChannel()
    {
        var interop = new FakeNativeSshInterop();
        int listenPort = GetFreePort();

        using var session = new NativePortForwardSession(
            FakeHandle(16),
            [CreateDynamicForward(listenPort)],
            interop);

        using TcpClient client = await ConnectLoopbackAsync(listenPort);
        NetworkStream stream = client.GetStream();

        await SendSocksGreetingAsync(stream);
        await ReadExactlyAsync(stream, 2, TimeSpan.FromSeconds(2));

        await SendSocksConnectRequestAsync(stream, IPAddress.Parse("10.20.30.40"), 9443);
        byte[] reply = await ReadExactlyAsync(stream, 10, TimeSpan.FromSeconds(2));

        await WaitUntilAsync(() => interop.OpenRequests.Count == 1);

        Assert.Equal(new byte[] { 0x05, 0x00 }, reply[..2]);
        Assert.Equal("10.20.30.40", interop.OpenRequests[0].HostToConnect);
        Assert.Equal(9443, interop.OpenRequests[0].PortToConnect);
    }

    [Fact]
    public async Task DynamicForward_UnsupportedCommandReturnsFailureWithoutOpeningChannel()
    {
        var interop = new FakeNativeSshInterop();
        int listenPort = GetFreePort();

        using var session = new NativePortForwardSession(
            FakeHandle(18),
            [CreateDynamicForward(listenPort)],
            interop);

        using TcpClient client = await ConnectLoopbackAsync(listenPort);
        NetworkStream stream = client.GetStream();

        await SendSocksGreetingAsync(stream);
        await ReadExactlyAsync(stream, 2, TimeSpan.FromSeconds(2));

        await SendSocksCommandRequestAsync(stream, 0x02, "svc.internal", 443);
        byte[] reply = await ReadExactlyAsync(stream, 10, TimeSpan.FromSeconds(2));

        Assert.Equal(0x05, reply[0]);
        Assert.Equal(0x07, reply[1]);
        // Unsupported commands are rejected before any direct-tcpip open is attempted.
        Assert.Empty(interop.OpenRequests);
    }

    [Fact]
    public async Task DynamicForward_NonZeroReservedByteReturnsProtocolFailureWithoutOpeningChannel()
    {
        var interop = new FakeNativeSshInterop();
        int listenPort = GetFreePort();

        using var session = new NativePortForwardSession(
            FakeHandle(19),
            [CreateDynamicForward(listenPort)],
            interop);

        using TcpClient client = await ConnectLoopbackAsync(listenPort);
        NetworkStream stream = client.GetStream();

        await SendSocksGreetingAsync(stream);
        await ReadExactlyAsync(stream, 2, TimeSpan.FromSeconds(2));

        await SendSocksCommandRequestAsync(stream, 0x01, "svc.internal", 443, reserved: 0x01);
        byte[] reply = await ReadExactlyAsync(stream, 10, TimeSpan.FromSeconds(2));

        Assert.Equal(0x05, reply[0]);
        Assert.Equal(0x01, reply[1]);
        Assert.Empty(interop.OpenRequests);
    }

    [Fact]
    public async Task Dispose_ClosesDynamicForwardChannelAndListener()
    {
        var interop = new FakeNativeSshInterop();
        int dynamicPort = GetFreePort();

        var session = new NativePortForwardSession(
            FakeHandle(20),
            [CreateDynamicForward(dynamicPort)],
            interop);

        using TcpClient dynamicClient = await ConnectLoopbackAsync(dynamicPort);
        NetworkStream stream = dynamicClient.GetStream();

        await SendSocksGreetingAsync(stream);
        await ReadExactlyAsync(stream, 2, TimeSpan.FromSeconds(2));

        await SendSocksConnectRequestAsync(stream, "svc-dynamic.internal", 8443);
        await ReadExactlyAsync(stream, 10, TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => interop.OpenRequests.Count == 1);

        session.Dispose();

        await WaitUntilAsync(() => interop.ClosedChannelIds.Count == 1);
        Assert.Single(interop.ClosedChannelIds);
        await Assert.ThrowsAnyAsync<SocketException>(() => ConnectLoopbackAsync(dynamicPort));
    }

    [Fact]
    public async Task DynamicForward_SuccessReplyWriteFailureClosesOpenedChannel()
    {
        var interop = new FakeNativeSshInterop();
        int dynamicPort = GetFreePort();

        using var session = CreateSessionWithReplyWriter(
            FakeHandle(21),
            [CreateDynamicForward(dynamicPort)],
            interop,
            (_, _, _) => throw new IOException("Injected reply write failure."));

        using TcpClient dynamicClient = await ConnectLoopbackAsync(dynamicPort);
        NetworkStream stream = dynamicClient.GetStream();

        await SendSocksGreetingAsync(stream);
        await ReadExactlyAsync(stream, 2, TimeSpan.FromSeconds(2));

        await SendSocksConnectRequestAsync(stream, "svc-abort.internal", 8443);

        await WaitUntilAsync(() => interop.OpenRequests.Count == 1);
        await WaitUntilAsync(() => interop.ClosedChannelIds.Count == 1);

        Assert.Single(interop.ClosedChannelIds);
    }

    [Fact]
    public async Task MalformedDynamicRequestDoesNotBreakUnrelatedLocalOrDynamicForwards()
    {
        var interop = new FakeNativeSshInterop();
        int localPort = GetFreePort();
        int dynamicPort = GetFreePort();

        using var session = new NativePortForwardSession(
            FakeHandle(22),
            [
                CreateForward(localPort, "svc-local.internal", 8080),
                CreateDynamicForward(dynamicPort)
            ],
            interop);

        using (TcpClient badDynamicClient = await ConnectLoopbackAsync(dynamicPort))
        {
            NetworkStream stream = badDynamicClient.GetStream();
            await SendSocksGreetingAsync(stream);
            await ReadExactlyAsync(stream, 2, TimeSpan.FromSeconds(2));

            await SendSocksCommandRequestAsync(stream, 0x01, "svc-bad.internal", 443, reserved: 0x01);
            byte[] reply = await ReadExactlyAsync(stream, 10, TimeSpan.FromSeconds(2));

            Assert.Equal(0x05, reply[0]);
            Assert.Equal(0x01, reply[1]);
        }

        using (TcpClient localClient = await ConnectLoopbackAsync(localPort))
        {
            await WaitUntilAsync(() => interop.OpenRequests.Any(request => request.HostToConnect == "svc-local.internal"));
        }

        using (TcpClient goodDynamicClient = await ConnectLoopbackAsync(dynamicPort))
        {
            NetworkStream stream = goodDynamicClient.GetStream();
            await SendSocksGreetingAsync(stream);
            await ReadExactlyAsync(stream, 2, TimeSpan.FromSeconds(2));

            await SendSocksConnectRequestAsync(stream, "svc-good.internal", 9443);
            byte[] reply = await ReadExactlyAsync(stream, 10, TimeSpan.FromSeconds(2));

            Assert.Equal(0x05, reply[0]);
            Assert.Equal(0x00, reply[1]);
        }

        Assert.Contains(interop.OpenRequests, request => request.HostToConnect == "svc-local.internal" && request.PortToConnect == 8080);
        Assert.Contains(interop.OpenRequests, request => request.HostToConnect == "svc-good.internal" && request.PortToConnect == 9443);
        Assert.DoesNotContain(interop.OpenRequests, request => request.HostToConnect == "svc-bad.internal");
    }

    [Fact]
    public async Task LocalAndDynamicForwardsCanStartTogether()
    {
        var interop = new FakeNativeSshInterop();
        int localPort = GetFreePort();
        int dynamicPort = GetFreePort();

        using var session = new NativePortForwardSession(
            FakeHandle(17),
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

    [Fact]
    public async Task HandleEvent_WithALocalPeerThatNeverReads_DoesNotBlockTheCaller()
    {
        // The regression this pins: HandleEvent used to write straight to the local socket from
        // NativeSshSession's poll loop, so a forwarded port whose consumer stopped reading froze all
        // terminal output for the session (#173 item 2). Sixteen megabytes at a peer that reads nothing
        // is far past anything the kernel will buffer, so the old code would block here.
        var interop = new FakeNativeSshInterop();
        int listenPort = GetFreePort();

        using var session = new NativePortForwardSession(
            FakeHandle(23),
            [CreateForward(listenPort, "svc.internal", 9000)],
            interop);

        using TcpClient client = await ConnectLoopbackAsync(listenPort);
        client.ReceiveBufferSize = 1024;

        await WaitUntilAsync(() => interop.OpenedChannelIds.Count == 1);
        int channelId = interop.OpenedChannelIds[0];

        byte[] chunk = new byte[64 * 1024];
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 256; i++)
        {
            session.HandleEvent(NativeSshEvent.ForwardChannelData(channelId, chunk));
        }

        stopwatch.Stop();

        // Real cost is microseconds; the ceiling is loose enough to survive a loaded CI runner while
        // still failing outright on a blocking write.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"HandleEvent blocked for {stopwatch.Elapsed} while the local peer was not reading.");

        // And the channel that could not keep up is closed rather than buffered without limit.
        await WaitUntilAsync(() => interop.ClosedChannelIds.Contains(channelId));
    }

    [Fact]
    public async Task ForwardChannelEof_ArrivesAfterTheDataQueuedAheadOfIt()
    {
        // EOF travels through the same queue as the data. Acting on it immediately would shut the send
        // side down while bytes were still queued behind it, truncating the proxied stream.
        var interop = new FakeNativeSshInterop();
        int listenPort = GetFreePort();

        using var session = new NativePortForwardSession(
            FakeHandle(24),
            [CreateForward(listenPort, "svc.internal", 9001)],
            interop);

        using TcpClient client = await ConnectLoopbackAsync(listenPort);
        await WaitUntilAsync(() => interop.OpenedChannelIds.Count == 1);
        int channelId = interop.OpenedChannelIds[0];

        byte[] payload = Encoding.ASCII.GetBytes("forwarded-payload");
        session.HandleEvent(NativeSshEvent.ForwardChannelData(channelId, payload));
        session.HandleEvent(NativeSshEvent.ForwardChannelEof(channelId));

        NetworkStream stream = client.GetStream();
        byte[] received = await ReadExactlyAsync(stream, payload.Length, TimeSpan.FromSeconds(10));
        Assert.Equal(payload, received);

        // Only now may the peer see end-of-stream.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int read = await stream.ReadAsync(new byte[1], cts.Token);
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task ForwardChannelClosed_FlushesQueuedDataAndDoesNotCloseTheSshChannelAgain()
    {
        var interop = new FakeNativeSshInterop();
        int listenPort = GetFreePort();

        using var session = new NativePortForwardSession(
            FakeHandle(25),
            [CreateForward(listenPort, "svc.internal", 9002)],
            interop);

        using TcpClient client = await ConnectLoopbackAsync(listenPort);
        await WaitUntilAsync(() => interop.OpenedChannelIds.Count == 1);
        int channelId = interop.OpenedChannelIds[0];

        byte[] payload = Encoding.ASCII.GetBytes("last-bytes-before-close");
        session.HandleEvent(NativeSshEvent.ForwardChannelData(channelId, payload));
        session.HandleEvent(NativeSshEvent.ForwardChannelClosed(channelId));

        NetworkStream stream = client.GetStream();
        byte[] received = await ReadExactlyAsync(stream, payload.Length, TimeSpan.FromSeconds(10));
        Assert.Equal(payload, received);

        // The SSH channel closed on its own; asking the native side to close it again would be wrong.
        Assert.DoesNotContain(channelId, interop.ClosedChannelIds);
    }

    [Fact]
    public async Task ClientToSshPump_RetriesRefusedWritesInsteadOfDroppingThem()
    {
        // The native side refuses a write when a forward channel is over its queued-byte budget
        // toward the remote (Codex review on #325). The pump must treat that as "try again", not as
        // "delivered": dropping the bytes would silently corrupt the proxied stream, and forcing them
        // through would restore the unbounded queue the budget exists to prevent.
        var interop = new BackpressuringNativeSshInterop(refusalsPerWrite: 3);
        int listenPort = GetFreePort();

        using var session = new NativePortForwardSession(
            FakeHandle(26),
            [CreateForward(listenPort, "svc.internal", 9003)],
            interop);

        using TcpClient client = await ConnectLoopbackAsync(listenPort);
        await WaitUntilAsync(() => interop.OpenedChannelIds.Count == 1);

        byte[] payload = Encoding.ASCII.GetBytes("bytes-that-must-survive-backpressure");
        await client.GetStream().WriteAsync(payload);
        await client.GetStream().FlushAsync();

        await WaitUntilAsync(() => interop.WrittenBytes.Count >= payload.Length);

        Assert.Equal(payload, interop.WrittenBytes.Take(payload.Length).ToArray());
        // Refused attempts must not have been counted as delivered.
        Assert.True(interop.RefusedAttempts >= 3, $"Expected the pump to absorb refusals; saw {interop.RefusedAttempts}.");
    }

    /// <summary>
    /// Refuses the first <c>refusalsPerWrite</c> attempts of every distinct write, then accepts,
    /// standing in for a native queue that is briefly over budget.
    /// </summary>
    [Fact]
    public async Task RemoteForward_RequestsAListenerFromTheServer()
    {
        var interop = new FakeNativeSshInterop();

        using var session = new NativePortForwardSession(
            FakeHandle(30),
            [CreateRemoteForward(19000, "127.0.0.1", 9200)],
            interop);

        // Off the constructor by design: the request can only be answered once the session is
        // established, so it runs on a background task.
        await WaitUntilAsync(() => interop.RemoteForwardRequests.Count == 1);
        Assert.Equal(("localhost", 19000), interop.RemoteForwardRequests[0]);
    }

    [Fact]
    public async Task IncomingForwardChannel_DialsTheDestinationAndCarriesBytesBothWays()
    {
        var interop = new FakeNativeSshInterop();
        int destinationPort = GetFreePort();
        var destination = new TcpListener(IPAddress.Loopback, destinationPort);
        destination.Start();

        try
        {
            using var session = new NativePortForwardSession(
                FakeHandle(31),
                [CreateRemoteForward(19001, "127.0.0.1", destinationPort)],
                interop);

            // The data event is queued immediately behind the announcement, before the dial to the
            // destination can possibly have completed. Nothing may be lost to that gap: the channel
            // state (and its ordered outbound queue) must exist from the announcement onward.
            byte[] payload = Encoding.ASCII.GetBytes("remote-forwarded-request");
            session.HandleEvent(NativeSshEvent.ForwardChannelIncoming(77, IncomingPayload(19001)));
            session.HandleEvent(NativeSshEvent.ForwardChannelData(77, payload));

            using TcpClient accepted = await destination.AcceptTcpClientAsync();
            NetworkStream stream = accepted.GetStream();
            byte[] received = await ReadExactlyAsync(stream, payload.Length, TimeSpan.FromSeconds(10));
            Assert.Equal(payload, received);

            // And the reply direction rides the same channel toward the remote.
            byte[] reply = Encoding.ASCII.GetBytes("remote-forwarded-reply");
            await stream.WriteAsync(reply);
            await stream.FlushAsync();
            await WaitUntilAsync(() => interop.ChannelWrites(77).Length == reply.Length);
            Assert.Equal(reply, interop.ChannelWrites(77));
        }
        finally
        {
            destination.Stop();
        }
    }

    [Fact]
    public async Task IncomingForwardChannel_WithNoMatchingRule_IsRefused()
    {
        var interop = new FakeNativeSshInterop();

        using var session = new NativePortForwardSession(
            FakeHandle(32),
            [CreateRemoteForward(19002, "127.0.0.1", 9300)],
            interop);

        // An announcement for a port no rule asked about has no destination it is allowed to reach.
        session.HandleEvent(NativeSshEvent.ForwardChannelIncoming(78, IncomingPayload(28000)));

        await WaitUntilAsync(() => interop.ClosedChannelIds.Contains(78));
    }

    [Fact]
    public async Task IncomingForwardChannel_WhenTheDestinationRefuses_ClosesTheChannel()
    {
        var interop = new FakeNativeSshInterop();
        // A freshly probed free port with nothing listening: the dial must fail fast.
        int deadPort = GetFreePort();

        using var session = new NativePortForwardSession(
            FakeHandle(33),
            [CreateRemoteForward(19003, "127.0.0.1", deadPort)],
            interop);

        session.HandleEvent(NativeSshEvent.ForwardChannelIncoming(79, IncomingPayload(19003)));

        await WaitUntilAsync(() => interop.ClosedChannelIds.Contains(79));
    }

    private static byte[] IncomingPayload(int connectedPort) =>
        Encoding.UTF8.GetBytes(
            $"{{\"connectedAddress\":\"localhost\",\"connectedPort\":{connectedPort},\"originatorAddress\":\"192.0.2.10\",\"originatorPort\":50000}}");

    private static PortForward CreateRemoteForward(int sourcePort, string destinationHost, int destinationPort)
    {
        return new PortForward
        {
            Kind = PortForwardKind.Remote,
            SourcePort = sourcePort,
            DestinationHost = destinationHost,
            DestinationPort = destinationPort
        };
    }

    private sealed class BackpressuringNativeSshInterop : FakeNativeSshInterop
    {
        private readonly int _refusalsPerWrite;
        private readonly object _writeLock = new();
        private readonly List<byte> _written = [];
        private int _pendingRefusals;
        private int _refusedAttempts;

        public BackpressuringNativeSshInterop(int refusalsPerWrite)
        {
            _refusalsPerWrite = refusalsPerWrite;
            _pendingRefusals = refusalsPerWrite;
        }

        public IReadOnlyList<byte> WrittenBytes
        {
            get { lock (_writeLock) { return _written.ToArray(); } }
        }

        public int RefusedAttempts
        {
            get { lock (_writeLock) { return _refusedAttempts; } }
        }

        public override bool TryWriteChannel(NovaSshSafeHandle sessionHandle, int channelId, ReadOnlySpan<byte> data)
        {
            lock (_writeLock)
            {
                if (_pendingRefusals > 0)
                {
                    _pendingRefusals--;
                    _refusedAttempts++;
                    return false;
                }

                _written.AddRange(data.ToArray());
                _pendingRefusals = _refusalsPerWrite;
                return true;
            }
        }
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
        await SendSocksCommandRequestAsync(stream, 0x01, host, port);
    }

    private static async Task SendSocksConnectRequestAsync(NetworkStream stream, IPAddress address, int port)
    {
        byte[] addressBytes = address.GetAddressBytes();
        byte atyp = address.AddressFamily switch
        {
            AddressFamily.InterNetwork => 0x01,
            AddressFamily.InterNetworkV6 => 0x04,
            _ => throw new ArgumentOutOfRangeException(nameof(address), "Unsupported IP address family.")
        };

        byte[] request = new byte[6 + addressBytes.Length];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = atyp;
        Buffer.BlockCopy(addressBytes, 0, request, 4, addressBytes.Length);
        request[4 + addressBytes.Length] = (byte)((port >> 8) & 0xFF);
        request[5 + addressBytes.Length] = (byte)(port & 0xFF);

        await stream.WriteAsync(request);
        await stream.FlushAsync();
    }

    private static async Task SendSocksCommandRequestAsync(NetworkStream stream, byte command, string host, int port, byte reserved = 0x00)
    {
        byte[] hostBytes = Encoding.ASCII.GetBytes(host);
        byte[] request = new byte[7 + hostBytes.Length];
        request[0] = 0x05;
        request[1] = command;
        request[2] = reserved;
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

    // Generous default ceiling: the predicate flips in milliseconds on an unloaded
    // box, so a large timeout only affects wall time on a genuine failure. The old
    // 3s ceiling flaked on loaded CI runners where the accept -> OpenDirectTcpIp
    // pipeline lagged behind the connect (issue #130).
    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutSeconds = 30)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
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

    // Not sealed: BackpressuringNativeSshInterop overrides only the write path.
    private class FakeNativeSshInterop : INativeSshInterop
    {
        private readonly ConcurrentQueue<NativeSshEvent> _events = new();
        private int _nextChannelId = 100;

        // OpenDirectTcpIp/CloseChannel are invoked from the per-listener accept
        // loops (Task.Run in NativePortForwardSession.StartListener), so with two
        // forwards two threads mutate these lists concurrently. List<T> is not
        // thread-safe: racing Add calls can clobber the size field, dropping an
        // entry so OpenRequests.Count never reaches 2 and the WaitUntilAsync
        // predicate hangs to its full ceiling. Serialize the writes, and expose
        // reads as locked snapshots — WaitUntilAsync polls these while the accept
        // loops are still adding, so a live List<T> would risk stale counts or a
        // "collection modified during enumeration" throw in predicates like .Any.
        private readonly object _collectionsLock = new();
        private readonly List<NativePortForwardOpenOptions> _openRequests = [];
        private readonly List<int> _closedChannelIds = [];
        private readonly List<int> _openedChannelIds = [];
        private readonly List<(string BindAddress, int Port)> _remoteForwardRequests = [];
        private readonly Dictionary<int, List<byte>> _channelWrites = [];

        public IReadOnlyList<NativePortForwardOpenOptions> OpenRequests
        {
            get { lock (_collectionsLock) { return _openRequests.ToArray(); } }
        }

        /// <summary>Ids handed out by <see cref="OpenDirectTcpIp"/>, so tests can address a channel
        /// without assuming the counter's starting value.</summary>
        public IReadOnlyList<int> OpenedChannelIds
        {
            get { lock (_collectionsLock) { return _openedChannelIds.ToArray(); } }
        }

        public IReadOnlyList<int> ClosedChannelIds
        {
            get { lock (_collectionsLock) { return _closedChannelIds.ToArray(); } }
        }

        public IReadOnlyList<(string BindAddress, int Port)> RemoteForwardRequests
        {
            get { lock (_collectionsLock) { return _remoteForwardRequests.ToArray(); } }
        }

        /// <summary>Everything written toward the remote on one channel, in order.</summary>
        public byte[] ChannelWrites(int channelId)
        {
            lock (_collectionsLock)
            {
                return _channelWrites.TryGetValue(channelId, out List<byte>? bytes)
                    ? bytes.ToArray()
                    : [];
            }
        }

        public NovaSshSafeHandle Connect(NativeSshConnectionOptions options) => new(new IntPtr(1), ownsHandle: false);

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
            return _events.TryDequeue(out NativeSshEvent? nextEvent)
                ? nextEvent
                : null;
        }

        public void Write(NovaSshSafeHandle sessionHandle, ReadOnlySpan<byte> data)
        {
        }

        public void Resize(NovaSshSafeHandle sessionHandle, int cols, int rows)
        {
        }

        public void SubmitResponse(NovaSshSafeHandle sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data)
        {
        }

        public int OpenDirectTcpIp(NovaSshSafeHandle sessionHandle, NativePortForwardOpenOptions options)
        {
            int channelId = Interlocked.Increment(ref _nextChannelId);
            lock (_collectionsLock)
            {
                _openRequests.Add(options);
                _openedChannelIds.Add(channelId);
            }
            return channelId;
        }

        public void WriteChannel(NovaSshSafeHandle sessionHandle, int channelId, ReadOnlySpan<byte> data)
        {
            byte[] copied = data.ToArray();
            lock (_collectionsLock)
            {
                if (!_channelWrites.TryGetValue(channelId, out List<byte>? bytes))
                {
                    bytes = [];
                    _channelWrites[channelId] = bytes;
                }

                bytes.AddRange(copied);
            }
        }

        public int RequestRemoteForward(NovaSshSafeHandle sessionHandle, string bindAddress, int port)
        {
            lock (_collectionsLock)
            {
                _remoteForwardRequests.Add((bindAddress, port));
            }

            return port;
        }

        // Declared (rather than left to the interface's default) so a derived fake can override it.
        public virtual bool TryWriteChannel(NovaSshSafeHandle sessionHandle, int channelId, ReadOnlySpan<byte> data)
        {
            WriteChannel(sessionHandle, channelId, data);
            return true;
        }

        public void SendChannelEof(NovaSshSafeHandle sessionHandle, int channelId)
        {
        }

        public void CloseChannel(NovaSshSafeHandle sessionHandle, int channelId)
        {
            lock (_collectionsLock)
            {
                _closedChannelIds.Add(channelId);
            }
        }

        public void Close(NovaSshSafeHandle sessionHandle)
        {
        }
    }

    private static NovaSshSafeHandle FakeHandle(int value) =>
        new(new IntPtr(value), ownsHandle: false);

    private static NativePortForwardSession CreateSessionWithReplyWriter(
        NovaSshSafeHandle sessionHandle,
        IReadOnlyList<PortForward> forwards,
        INativeSshInterop interop,
        Func<NetworkStream, byte[], CancellationToken, Task> replyWriter)
    {
        ConstructorInfo ctor = typeof(NativePortForwardSession).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [
                typeof(NovaSshSafeHandle),
                typeof(IReadOnlyList<PortForward>),
                typeof(INativeSshInterop),
                typeof(Action<string>),
                typeof(Func<NetworkStream, byte[], CancellationToken, Task>)
            ],
            modifiers: null)
            ?? throw new InvalidOperationException("Could not find NativePortForwardSession private reply-writer constructor.");

        return (NativePortForwardSession)ctor.Invoke([sessionHandle, forwards, interop, null!, replyWriter]);
    }
}

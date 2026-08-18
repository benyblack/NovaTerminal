using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using NovaTerminal.Platform.Ssh.Models;

namespace NovaTerminal.Platform.Ssh.Native;

public sealed class NativePortForwardSession : IDisposable
{
    /// <summary>
    /// Per-channel ceiling on bytes waiting to reach the local socket. Exceeding it closes that one
    /// forward channel — see <see cref="EnqueueOutbound"/> for why neither waiting nor dropping is an
    /// option. Sized so a stalled channel costs about a megabyte rather than the whole stream.
    /// </summary>
    private const int MaxQueuedOutboundBytesPerChannel = 1024 * 1024;

    /// <summary>
    /// How long to wait before retrying a forward-channel write the native side refused for want of
    /// queue space. Polling because the FFI has no completion signal to await; short enough that it
    /// costs throughput only while a forward is genuinely backed up.
    /// </summary>
    private static readonly TimeSpan ForwardWriteRetryDelay = TimeSpan.FromMilliseconds(5);

    private const byte SocksVersion5 = 0x05;
    private const byte SocksCommandConnect = 0x01;
    private const byte SocksAuthNoAuthentication = 0x00;
    private const byte SocksAuthNoAcceptableMethods = 0xFF;
    private const byte SocksAddressTypeIpv4 = 0x01;
    private const byte SocksAddressTypeDomainName = 0x03;
    private const byte SocksAddressTypeIpv6 = 0x04;
    private const byte SocksReplySucceeded = 0x00;
    private const byte SocksReplyGeneralFailure = 0x01;
    private const byte SocksReplyCommandNotSupported = 0x07;
    private const byte SocksReplyAddressTypeNotSupported = 0x08;

    private readonly NovaSshSafeHandle _sessionHandle;
    private readonly INativeSshInterop _interop;
    private readonly Action<string> _log;
    private readonly Func<NetworkStream, byte[], CancellationToken, Task> _socksReplyWriter;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly List<TcpListener> _listeners = [];
    private readonly ConcurrentDictionary<int, ForwardChannelState> _channels = new();
    private int _disposed;

    public NativePortForwardSession(
        NovaSshSafeHandle sessionHandle,
        IReadOnlyList<PortForward> forwards,
        INativeSshInterop interop,
        Action<string>? log = null)
        : this(sessionHandle, forwards, interop, log, WriteSocksReplyAsync)
    {
    }

    private NativePortForwardSession(
        NovaSshSafeHandle sessionHandle,
        IReadOnlyList<PortForward> forwards,
        INativeSshInterop interop,
        Action<string>? log,
        Func<NetworkStream, byte[], CancellationToken, Task> socksReplyWriter)
    {
        if (sessionHandle is null || sessionHandle.IsInvalid || sessionHandle.IsClosed)
        {
            throw new ArgumentException("Native port forwarding requires a valid SSH session handle.", nameof(sessionHandle));
        }

        ArgumentNullException.ThrowIfNull(forwards);
        ArgumentNullException.ThrowIfNull(interop);

        _sessionHandle = sessionHandle;
        _interop = interop;
        _log = log ?? (_ => { });
        _socksReplyWriter = socksReplyWriter ?? throw new ArgumentNullException(nameof(socksReplyWriter));

        // Checked up front rather than per-forward inside the loop: an unsupported kind is a property
        // of the profile, not of a bind, so there is no reason to open listeners we are about to tear
        // down again. Wording comes from NativeSshCapability so it matches what the editor showed.
        NativeSshCapabilityResult capability = NativeSshCapability.Evaluate(forwards, jumpHops: null);
        if (!capability.IsSupported)
        {
            throw new NotSupportedException(capability.Explanation);
        }

        try
        {
            foreach (PortForward forward in forwards)
            {
                StartListener(forward);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Hands a forward-channel event to the owning channel's outbound queue. Called from
    /// <c>NativeSshSession.PollLoopAsync</c>, so it must never block: this used to write straight to
    /// the local socket, which meant one slow consumer of a forwarded port froze *all* terminal output
    /// for the session until it drained (#173 item 2). Enqueueing keeps the poll loop moving and lets
    /// a dedicated pump per channel absorb the blocking write.
    /// </summary>
    public void HandleEvent(NativeSshEvent nextEvent)
    {
        if (nextEvent == null)
        {
            return;
        }

        if (!_channels.TryGetValue(nextEvent.StatusCode, out ForwardChannelState? channel))
        {
            return;
        }

        // EOF and close travel through the same queue as the data rather than acting immediately.
        // Ordering is the whole point: shutting the send side down (or disposing the socket) while
        // bytes were still queued behind it would truncate the proxied stream.
        switch (nextEvent.Kind)
        {
            case NativeSshEventKind.ForwardChannelData:
                EnqueueOutbound(channel, new ForwardOutbound(ForwardOutboundKind.Data, nextEvent.Payload));
                break;
            case NativeSshEventKind.ForwardChannelEof:
                EnqueueOutbound(channel, new ForwardOutbound(ForwardOutboundKind.Eof, []));
                break;
            case NativeSshEventKind.ForwardChannelClosed:
                EnqueueOutbound(channel, new ForwardOutbound(ForwardOutboundKind.Closed, []));
                break;
        }
    }

    private void EnqueueOutbound(ForwardChannelState channel, ForwardOutbound item)
    {
        if (channel.TryEnqueueOutbound(item, MaxQueuedOutboundBytesPerChannel))
        {
            return;
        }

        // Two ways to land here: the queue is over budget, or teardown completed it between the
        // lookup and the write. Both end with the channel gone, but only the first is a real event —
        // RemoveChannel drops the channel from _channels before completing the queue, so a still-
        // present channel means genuine overflow.
        //
        // Closing the channel is the only honest response to overflow: waiting would reintroduce the
        // poll-loop stall this queue exists to remove, and discarding bytes mid-stream would silently
        // corrupt what is, from the peer's view, a plain TCP connection. A dead forward is
        // diagnosable; a corrupted one is not.
        if (_channels.ContainsKey(channel.ChannelId))
        {
            _log($"[NativePortForwardSession] Forward channel {channel.ChannelId} exceeded its {MaxQueuedOutboundBytesPerChannel}-byte outbound budget ({channel.QueuedOutboundBytes} queued); closing it.");
        }

        RemoveChannel(channel.ChannelId, closeInteropChannel: true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetimeCts.Cancel();

        foreach (TcpListener listener in _listeners)
        {
            try
            {
                listener.Stop();
            }
            catch
            {
            }
        }

        foreach (int channelId in _channels.Keys.ToArray())
        {
            RemoveChannel(channelId, closeInteropChannel: true);
        }

        _lifetimeCts.Dispose();
    }

    private void StartListener(PortForward forward)
    {
        IPAddress address = ResolveBindAddress(forward.BindAddress);
        var listener = new TcpListener(address, forward.SourcePort);

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to bind {DescribeForward(forward, address)}: {ex.Message}", ex);
        }

        _listeners.Add(listener);
        _ = forward.Kind switch
        {
            PortForwardKind.Local => Task.Run(() => AcceptLocalLoopAsync(listener, forward, _lifetimeCts.Token)),
            PortForwardKind.Dynamic => Task.Run(() => AcceptDynamicLoopAsync(listener, forward, _lifetimeCts.Token)),
            _ => throw new NotSupportedException(
                NativeSshCapability.Evaluate([forward], jumpHops: null).Explanation)
        };
    }

    private async Task AcceptLocalLoopAsync(TcpListener listener, PortForward forward, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;

            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                IPEndPoint remoteEndPoint = (IPEndPoint)(client.Client.RemoteEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0));
                int channelId = _interop.OpenDirectTcpIp(
                    _sessionHandle,
                    new NativePortForwardOpenOptions
                    {
                        HostToConnect = forward.DestinationHost,
                        PortToConnect = forward.DestinationPort,
                        OriginatorAddress = remoteEndPoint.Address.ToString(),
                        OriginatorPort = remoteEndPoint.Port
                    });

                var state = new ForwardChannelState(channelId, client);
                if (!_channels.TryAdd(channelId, state))
                {
                    client.Dispose();
                    _interop.CloseChannel(_sessionHandle, channelId);
                    continue;
                }

                // Token passed to Task.Run as well as into the pump: once the session is being torn
                // down there is no reason to schedule a pump at all, and it matches what the dynamic
                // accept path already does for the same two calls.
                _ = Task.Run(() => PumpClientToSshAsync(state, cancellationToken), cancellationToken);
                _ = Task.Run(() => PumpSshToClientAsync(state, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                client?.Dispose();
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                client?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                client?.Dispose();
                _log($"[NativePortForwardSession] Accept loop for {forward} failed: {ex.Message}");
            }
        }
    }

    private async Task AcceptDynamicLoopAsync(TcpListener listener, PortForward forward, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;

            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                TcpClient acceptedClient = client;
                _ = Task.Run(() => HandleDynamicClientAsync(acceptedClient, forward, cancellationToken), cancellationToken);
                client = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                client?.Dispose();
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                client?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                client?.Dispose();
                _log($"[NativePortForwardSession] Dynamic accept loop for {forward} failed: {ex.Message}");
            }
        }
    }

    private async Task HandleDynamicClientAsync(TcpClient client, PortForward forward, CancellationToken cancellationToken)
    {
        try
        {
            NetworkStream stream = client.GetStream();
            if (!await NegotiateSocks5Async(stream, cancellationToken).ConfigureAwait(false))
            {
                client.Dispose();
                return;
            }

            SocksConnectRequest request = await ReadSocksRequestAsync(stream, cancellationToken).ConfigureAwait(false);
            if (request.Command != SocksCommandConnect)
            {
                await SendSocksReplyAsync(stream, SocksReplyCommandNotSupported, cancellationToken).ConfigureAwait(false);
                client.Dispose();
                return;
            }

            IPEndPoint remoteEndPoint = (IPEndPoint)(client.Client.RemoteEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0));

            int channelId;
            try
            {
                channelId = _interop.OpenDirectTcpIp(
                    _sessionHandle,
                    new NativePortForwardOpenOptions
                    {
                        HostToConnect = request.Host,
                        PortToConnect = request.Port,
                        OriginatorAddress = remoteEndPoint.Address.ToString(),
                        OriginatorPort = remoteEndPoint.Port
                    });
            }
            catch (Exception ex)
            {
                _log($"[NativePortForwardSession] Failed to open dynamic forward channel for {request.Host}:{request.Port}: {ex.Message}");
                await SendSocksReplyAsync(stream, SocksReplyGeneralFailure, cancellationToken).ConfigureAwait(false);
                client.Dispose();
                return;
            }

            var state = new ForwardChannelState(channelId, client);
            if (!_channels.TryAdd(channelId, state))
            {
                await SendSocksReplyAsync(stream, SocksReplyGeneralFailure, cancellationToken).ConfigureAwait(false);
                client.Dispose();
                _interop.CloseChannel(_sessionHandle, channelId);
                return;
            }

            try
            {
                await SendSocksReplyAsync(stream, SocksReplySucceeded, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"[NativePortForwardSession] Failed to write dynamic SOCKS success reply for {request.Host}:{request.Port}: {ex.Message}");
                RemoveChannel(channelId, closeInteropChannel: true);
                return;
            }

            _ = Task.Run(() => PumpClientToSshAsync(state, cancellationToken), cancellationToken);
            _ = Task.Run(() => PumpSshToClientAsync(state, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
        }
        catch (SocksProtocolException ex)
        {
            _log($"[NativePortForwardSession] Dynamic SOCKS handshake for {forward} failed: {ex.Message}");
            try
            {
                await SendSocksReplyAsync(client.GetStream(), ex.ReplyCode, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }

            client.Dispose();
        }
        catch (IOException ex)
        {
            _log($"[NativePortForwardSession] Dynamic client IO for {forward} failed: {ex.Message}");
            client.Dispose();
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
        }
        catch (Exception ex)
        {
            _log($"[NativePortForwardSession] Dynamic client setup for {forward} failed: {ex.Message}");
            client.Dispose();
        }
    }

    /// <summary>
    /// Drains one channel's outbound queue into its local socket. Runs on its own task so a blocking
    /// write here cannot reach the SSH poll loop; the queue in front of it is what makes that safe.
    /// </summary>
    private async Task PumpSshToClientAsync(ForwardChannelState channel, CancellationToken cancellationToken)
    {
        try
        {
            while (await channel.Outbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channel.Outbound.Reader.TryRead(out ForwardOutbound item))
                {
                    if (item.Kind == ForwardOutboundKind.Data)
                    {
                        await channel.Stream.WriteAsync(item.Payload, cancellationToken).ConfigureAwait(false);
                        channel.OnOutboundWritten(item.Payload.Length);
                        continue;
                    }

                    // Everything queued ahead of this marker has now been written.
                    await channel.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);

                    if (item.Kind == ForwardOutboundKind.Eof)
                    {
                        TryShutdown(channel.Client, SocketShutdown.Send);
                        continue;
                    }

                    // Closed: the remote end is gone and the local socket has seen everything it was
                    // owed. closeInteropChannel: false — the SSH channel closed on its own.
                    RemoveChannel(channel.ChannelId, closeInteropChannel: false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Dispose() is tearing every channel down already.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // Local peer went away mid-write. Close the SSH side too, so the remote is not left
            // holding a half-open channel.
            RemoveChannel(channel.ChannelId, closeInteropChannel: true);
        }
        catch (Exception ex)
        {
            _log($"[NativePortForwardSession] Outbound pump for channel {channel.ChannelId} failed: {ex.Message}");
            RemoveChannel(channel.ChannelId, closeInteropChannel: true);
        }
    }

    private async Task PumpClientToSshAsync(ForwardChannelState channel, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await channel.Stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                // Retry rather than force the write through. A refusal means the native side already
                // has more queued toward the remote than its budget allows, so the right response is
                // to stop reading this socket — which is exactly what looping here does, and which
                // lets TCP flow control throttle the local peer. Neither bytes nor the channel are
                // sacrificed for a remote that is merely slow.
                while (!_interop.TryWriteChannel(_sessionHandle, channel.ChannelId, buffer.AsSpan(0, bytesRead)))
                {
                    await Task.Delay(ForwardWriteRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _log($"[NativePortForwardSession] Local socket pump for channel {channel.ChannelId} failed: {ex.Message}");
        }
        finally
        {
            try
            {
                _interop.SendChannelEof(_sessionHandle, channel.ChannelId);
            }
            catch (Exception ex)
            {
                _log($"[NativePortForwardSession] Failed to send EOF for channel {channel.ChannelId}: {ex.Message}");
            }
        }
    }

    private void RemoveChannel(int channelId, bool closeInteropChannel)
    {
        if (!_channels.TryRemove(channelId, out ForwardChannelState? channel))
        {
            return;
        }

        // Releases the outbound pump's WaitToReadAsync so the task ends instead of lingering until
        // session teardown. Anything still queued is deliberately abandoned — the socket is going.
        channel.CompleteOutbound();

        if (closeInteropChannel)
        {
            try
            {
                _interop.CloseChannel(_sessionHandle, channelId);
            }
            catch (Exception ex)
            {
                _log($"[NativePortForwardSession] Failed to close channel {channelId}: {ex.Message}");
            }
        }

        try
        {
            channel.Client.Dispose();
        }
        catch
        {
        }
    }

    private static async Task<bool> NegotiateSocks5Async(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] header = await ReadExactlyAsync(stream, 2, cancellationToken).ConfigureAwait(false);
        if (header[0] != SocksVersion5)
        {
            throw new SocksProtocolException("Only SOCKS5 is supported.", SocksReplyGeneralFailure);
        }

        int methodCount = header[1];
        byte[] methods = await ReadExactlyAsync(stream, methodCount, cancellationToken).ConfigureAwait(false);
        bool hasNoAuth = methods.Contains(SocksAuthNoAuthentication);
        byte selectedMethod = hasNoAuth ? SocksAuthNoAuthentication : SocksAuthNoAcceptableMethods;

        byte[] response = [SocksVersion5, selectedMethod];
        await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return hasNoAuth;
    }

    private static async Task<SocksConnectRequest> ReadSocksRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] header = await ReadExactlyAsync(stream, 4, cancellationToken).ConfigureAwait(false);
        if (header[0] != SocksVersion5)
        {
            throw new SocksProtocolException("Only SOCKS5 is supported.", SocksReplyGeneralFailure);
        }

        if (header[2] != 0x00)
        {
            throw new SocksProtocolException("SOCKS reserved byte must be zero.", SocksReplyGeneralFailure);
        }

        string host = header[3] switch
        {
            SocksAddressTypeIpv4 => new IPAddress(await ReadExactlyAsync(stream, 4, cancellationToken).ConfigureAwait(false)).ToString(),
            SocksAddressTypeDomainName => await ReadDomainNameAsync(stream, cancellationToken).ConfigureAwait(false),
            SocksAddressTypeIpv6 => new IPAddress(await ReadExactlyAsync(stream, 16, cancellationToken).ConfigureAwait(false)).ToString(),
            _ => throw new SocksProtocolException("SOCKS address type is not supported.", SocksReplyAddressTypeNotSupported)
        };

        byte[] portBytes = await ReadExactlyAsync(stream, 2, cancellationToken).ConfigureAwait(false);
        int port = (portBytes[0] << 8) | portBytes[1];
        return new SocksConnectRequest(header[1], host, port);
    }

    private static async Task<string> ReadDomainNameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] lengthBuffer = await ReadExactlyAsync(stream, 1, cancellationToken).ConfigureAwait(false);
        int length = lengthBuffer[0];
        byte[] hostBytes = await ReadExactlyAsync(stream, length, cancellationToken).ConfigureAwait(false);
        return Encoding.ASCII.GetString(hostBytes);
    }

    private async Task SendSocksReplyAsync(NetworkStream stream, byte replyCode, CancellationToken cancellationToken)
    {
        byte[] reply =
        [
            SocksVersion5,
            replyCode,
            0x00,
            SocksAddressTypeIpv4,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00
        ];

        await _socksReplyWriter(stream, reply, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSocksReplyAsync(NetworkStream stream, byte[] reply, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int length, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[length];
        int offset = 0;

        while (offset < length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new SocksProtocolException("Socket closed before the SOCKS request completed.", SocksReplyGeneralFailure);
            }

            offset += read;
        }

        return buffer;
    }

    private static string DescribeForward(PortForward forward, IPAddress address)
    {
        return forward.Kind switch
        {
            PortForwardKind.Local => $"local forward {address}:{forward.SourcePort} -> {forward.DestinationHost}:{forward.DestinationPort}",
            PortForwardKind.Dynamic => $"dynamic forward {address}:{forward.SourcePort}",
            _ => $"forward {address}:{forward.SourcePort}"
        };
    }

    private static IPAddress ResolveBindAddress(string? bindAddress)
    {
        if (string.IsNullOrWhiteSpace(bindAddress) || bindAddress == "localhost")
        {
            return IPAddress.Loopback;
        }

        if (IPAddress.TryParse(bindAddress, out IPAddress? parsed))
        {
            return parsed;
        }

        IPAddress[] addresses = Dns.GetHostAddresses(bindAddress);
        return addresses.First(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6);
    }

    private static void TryShutdown(TcpClient client, SocketShutdown how)
    {
        try
        {
            client.Client.Shutdown(how);
        }
        catch
        {
        }
    }

    private readonly record struct SocksConnectRequest(byte Command, string Host, int Port);

    private sealed class SocksProtocolException : Exception
    {
        public SocksProtocolException(string message, byte replyCode)
            : base(message)
        {
            ReplyCode = replyCode;
        }

        public byte ReplyCode { get; }
    }

    private enum ForwardOutboundKind
    {
        Data = 0,
        Eof = 1,
        Closed = 2
    }

    private readonly record struct ForwardOutbound(ForwardOutboundKind Kind, byte[] Payload);

    private sealed class ForwardChannelState
    {
        private int _queuedOutboundBytes;

        public ForwardChannelState(int channelId, TcpClient client)
        {
            ChannelId = channelId;
            Client = client;
            Stream = client.GetStream();

            // Unbounded channel with our own byte budget, rather than a bounded channel: the budget
            // has to apply to data only. Eof/Closed markers must always be admittable, or a channel
            // that hit its cap could never be told the stream ended — the same control-event carve-out
            // the native event queue needs (#173 item 1).
            Outbound = Channel.CreateUnbounded<ForwardOutbound>(new UnboundedChannelOptions
            {
                // Exactly one reader: this channel's outbound pump.
                SingleReader = true

                // SingleWriter deliberately left false. Writes only ever come from the poll loop, but
                // CompleteOutbound is also a writer-side operation and is called from teardown on other
                // threads (Dispose, the pump itself), so the single-writer guarantee would not hold.
            });
        }

        public int ChannelId { get; }
        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public Channel<ForwardOutbound> Outbound { get; }

        public int QueuedOutboundBytes => Volatile.Read(ref _queuedOutboundBytes);

        public bool TryEnqueueOutbound(ForwardOutbound item, int maxQueuedBytes)
        {
            bool isData = item.Kind == ForwardOutboundKind.Data;

            if (isData)
            {
                // Check-then-add without a CAS loop is safe here: HandleEvent is the only writer, and
                // the reader only ever decreases the counter, so a concurrent decrement can make this
                // admit a payload it would otherwise refuse — never the other way round.
                int queued = Volatile.Read(ref _queuedOutboundBytes);

                // Always admit into an empty queue, even an oversized payload: refusing it forever
                // would kill a channel for a chunk that nothing is actually blocking.
                if (queued > 0 && queued + item.Payload.Length > maxQueuedBytes)
                {
                    return false;
                }

                Interlocked.Add(ref _queuedOutboundBytes, item.Payload.Length);
            }

            if (Outbound.Writer.TryWrite(item))
            {
                return true;
            }

            // The queue was completed by teardown between the lookup and here.
            if (isData)
            {
                Interlocked.Add(ref _queuedOutboundBytes, -item.Payload.Length);
            }

            return false;
        }

        public void OnOutboundWritten(int byteCount) =>
            Interlocked.Add(ref _queuedOutboundBytes, -byteCount);

        public void CompleteOutbound() => Outbound.Writer.TryComplete();
    }
}

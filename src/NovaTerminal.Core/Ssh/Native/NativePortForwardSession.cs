using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NovaTerminal.Core.Ssh.Models;

namespace NovaTerminal.Core.Ssh.Native;

public sealed class NativePortForwardSession : IDisposable
{
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

    private readonly IntPtr _sessionHandle;
    private readonly INativeSshInterop _interop;
    private readonly Action<string> _log;
    private readonly Func<NetworkStream, byte[], CancellationToken, Task> _socksReplyWriter;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly List<TcpListener> _listeners = [];
    private readonly ConcurrentDictionary<int, ForwardChannelState> _channels = new();
    private int _disposed;

    public NativePortForwardSession(
        IntPtr sessionHandle,
        IReadOnlyList<PortForward> forwards,
        INativeSshInterop interop,
        Action<string>? log = null)
        : this(sessionHandle, forwards, interop, log, WriteSocksReplyAsync)
    {
    }

    private NativePortForwardSession(
        IntPtr sessionHandle,
        IReadOnlyList<PortForward> forwards,
        INativeSshInterop interop,
        Action<string>? log,
        Func<NetworkStream, byte[], CancellationToken, Task> socksReplyWriter)
    {
        if (sessionHandle == IntPtr.Zero)
        {
            throw new ArgumentException("Native port forwarding requires a valid SSH session handle.", nameof(sessionHandle));
        }

        ArgumentNullException.ThrowIfNull(forwards);
        ArgumentNullException.ThrowIfNull(interop);

        _sessionHandle = sessionHandle;
        _interop = interop;
        _log = log ?? (_ => { });
        _socksReplyWriter = socksReplyWriter ?? throw new ArgumentNullException(nameof(socksReplyWriter));

        try
        {
            foreach (PortForward forward in forwards)
            {
                if (forward.Kind is not PortForwardKind.Local and not PortForwardKind.Dynamic)
                {
                    throw new NotSupportedException("Native SSH currently supports local and dynamic port forwards only.");
                }

                StartListener(forward);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

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

        try
        {
            switch (nextEvent.Kind)
            {
                case NativeSshEventKind.ForwardChannelData:
                    channel.Stream.Write(nextEvent.Payload, 0, nextEvent.Payload.Length);
                    break;
                case NativeSshEventKind.ForwardChannelEof:
                    TryShutdown(channel.Client, SocketShutdown.Send);
                    break;
                case NativeSshEventKind.ForwardChannelClosed:
                    RemoveChannel(channel.ChannelId, closeInteropChannel: false);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log($"[NativePortForwardSession] Forward channel {channel.ChannelId} failed: {ex.Message}");
            RemoveChannel(channel.ChannelId, closeInteropChannel: true);
        }
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
            _ => throw new NotSupportedException("Native SSH currently supports local and dynamic port forwards only.")
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

                _ = Task.Run(() => PumpClientToSshAsync(state, cancellationToken));
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

                _interop.WriteChannel(_sessionHandle, channel.ChannelId, buffer.AsSpan(0, bytesRead));
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

    private sealed class ForwardChannelState
    {
        public ForwardChannelState(int channelId, TcpClient client)
        {
            ChannelId = channelId;
            Client = client;
            Stream = client.GetStream();
        }

        public int ChannelId { get; }
        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
    }
}

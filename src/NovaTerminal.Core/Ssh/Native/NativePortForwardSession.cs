using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NovaTerminal.Core.Ssh.Models;

namespace NovaTerminal.Core.Ssh.Native;

public sealed class NativePortForwardSession : IDisposable
{
    private readonly IntPtr _sessionHandle;
    private readonly INativeSshInterop _interop;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly List<TcpListener> _listeners = [];
    private readonly ConcurrentDictionary<int, ForwardChannelState> _channels = new();
    private int _disposed;

    public NativePortForwardSession(
        IntPtr sessionHandle,
        IReadOnlyList<PortForward> forwards,
        INativeSshInterop interop,
        Action<string>? log = null)
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

        try
        {
            foreach (PortForward forward in forwards)
            {
                if (forward.Kind != PortForwardKind.Local)
                {
                    throw new NotSupportedException("Native SSH currently supports local port forwards only.");
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
            // Startup policy for Task 6: fail the entire native session setup on any bind error.
            throw new InvalidOperationException(
                $"Failed to bind local forward {address}:{forward.SourcePort} -> {forward.DestinationHost}:{forward.DestinationPort}: {ex.Message}",
                ex);
        }

        _listeners.Add(listener);
        _ = Task.Run(() => AcceptLoopAsync(listener, forward, _lifetimeCts.Token));
    }

    private async Task AcceptLoopAsync(TcpListener listener, PortForward forward, CancellationToken cancellationToken)
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

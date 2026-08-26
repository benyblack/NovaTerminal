using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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

    // Events that arrived for a locally-opened channel before it was registered, keyed by channel
    // id, plus a count of opens currently in flight. See TryResolveOrPark for why both exist: the
    // open blocks until the server answers, so data can be waiting for the poll loop before the
    // accept loop has published the channel. Empty in steady state.
    private readonly ConcurrentDictionary<int, PendingChannelEvents> _pendingByChannel = new();
    private int _opensInFlight;

    // Serialises the in-flight count, parking, publishing and the cleanup sweep. Never held across
    // the blocking open call — that would stall the poll loop, which is the freeze the outbound
    // queue exists to prevent (#173 item 2). See EndOpen for why one gate replaced an earlier
    // lock-free counter-and-generation scheme.
    private readonly object _registrationGate = new();

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

    // User-facing warnings (terminal output), as distinct from the diagnostic _log. A remote
    // forward the server refused must be as visible as ssh -R's "Warning: remote port forwarding
    // failed" — a session that looks healthy while a requested listener silently does not exist
    // is exactly the silent degradation the native backend promises not to have.
    private readonly Action<string> _warn;
    private readonly Func<NetworkStream, byte[], CancellationToken, Task> _socksReplyWriter;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly List<TcpListener> _listeners = [];

    // Remote-forward rules, so an incoming forwarded-tcpip channel can be matched back to the
    // rule whose listener it arrived on. Populated only in the constructor; read from HandleEvent.
    private readonly List<PortForward> _remoteForwards = [];
    private readonly ConcurrentDictionary<int, ForwardChannelState> _channels = new();
    private int _disposed;
    private int _remoteForwardsRequested;

    public NativePortForwardSession(
        NovaSshSafeHandle sessionHandle,
        IReadOnlyList<PortForward> forwards,
        INativeSshInterop interop,
        Action<string>? log = null,
        Action<string>? warn = null)
        : this(sessionHandle, forwards, interop, log, warn, WriteSocksReplyAsync)
    {
    }

    private NativePortForwardSession(
        NovaSshSafeHandle sessionHandle,
        IReadOnlyList<PortForward> forwards,
        INativeSshInterop interop,
        Action<string>? log,
        Action<string>? warn,
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
        _warn = warn ?? (_ => { });
        _socksReplyWriter = socksReplyWriter ?? throw new ArgumentNullException(nameof(socksReplyWriter));

        try
        {
            foreach (PortForward forward in forwards)
            {
                // Remote forwards have no local listener — the listener lives on the server, and
                // the request for it can only be made once the session is established (see
                // NotifySessionEstablished). Only collected here.
                if (forward.Kind == PortForwardKind.Remote)
                {
                    CollectRemoteForward(forward);
                }
                else
                {
                    StartListener(forward);
                }
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
        // Dropping events after disposal is safe (the native close tears every channel down), and
        // it keeps a late event off the disposed _lifetimeCts that the incoming path would touch.
        if (nextEvent == null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // Incoming channels are the one event about a channel this map has never seen: the
        // announcement is what creates the entry, so it must be handled before the lookup.
        if (nextEvent.Kind == NativeSshEventKind.ForwardChannelIncoming)
        {
            HandleIncomingForwardChannel(nextEvent);
            return;
        }

        if (!_channels.TryGetValue(nextEvent.StatusCode, out ForwardChannelState? channel)
            && !TryResolveOrPark(nextEvent, out channel))
        {
            return;
        }

        DispatchToChannel(channel, nextEvent);
    }

    /// <summary>
    /// EOF and close travel through the same queue as the data rather than acting immediately.
    /// Ordering is the whole point: shutting the send side down (or disposing the socket) while
    /// bytes were still queued behind it would truncate the proxied stream.
    /// </summary>
    private void DispatchToChannel(ForwardChannelState channel, NativeSshEvent nextEvent)
    {
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

    /// <summary>
    /// Handles an event whose channel is not in <see cref="_channels"/> yet: either it appeared
    /// while we looked (return it), or an open is in flight and this event belongs to it (park it
    /// for <see cref="PublishChannel"/> to replay), or the id is genuinely unknown (drop it).
    /// </summary>
    /// <remarks>
    /// This exists because a locally-initiated open is synchronous all the way to the server:
    /// nova_ssh_open_direct_tcpip waits on the worker's reply, so when the accept loop finally
    /// learns the channel id, the server may already have sent its first bytes — and the poll
    /// loop delivers those on a different thread, which can beat the accept loop to registration.
    /// Dropping them silently loses the head of a forwarded stream, which for any protocol whose
    /// server speaks first (SMTP, MySQL, IMAP) breaks the connection in a way that looks like a
    /// broken server rather than a bug here.
    ///
    /// Parking rather than locking around the open call is deliberate: the open blocks for a
    /// network round trip, and holding a lock across it would stall the poll loop — reintroducing
    /// exactly the freeze this class's queue exists to prevent (#173 item 2). The lock here is
    /// per-channel, in-memory, and uncontended once published.
    /// </remarks>
    private bool TryResolveOrPark(
        NativeSshEvent nextEvent,
        [NotNullWhen(true)] out ForwardChannelState? channel)
    {
        channel = null;

        lock (_registrationGate)
        {
            // Everything that touches this state - the in-flight count, parking, publishing, and
            // the cleanup sweep - runs under this one gate, so the checks below cannot be
            // invalidated between here and the decision. Two earlier attempts tried to keep this
            // lock-free by reading the counter (and then a generation stamp) with Volatile/
            // Interlocked, and both had the same shape of hole: another thread could change the
            // state being inferred from, between the read and the action. Serialising the four
            // operations is simpler than making that inference sound.
            if (_opensInFlight == 0)
            {
                // No open in flight, so no registration can be racing us: an unknown id is simply
                // unknown (a stale or already-torn-down channel) and dropping it is correct. This
                // is also what keeps _pendingByChannel from accumulating entries forever.
                return false;
            }

            // PublishChannel adds to _channels under this same gate, so re-checking here closes the
            // window rather than narrowing it.
            if (_channels.TryGetValue(nextEvent.StatusCode, out channel))
            {
                return true;
            }

            PendingChannelEvents pending = _pendingByChannel.GetOrAdd(
                nextEvent.StatusCode,
                static _ => new PendingChannelEvents());

            if (pending.Published)
            {
                // Registered and already torn down again; there is nothing to replay into.
                return false;
            }

            if (pending.Failed)
            {
                // Already over budget. Keep dropping; PublishChannel will tear the channel down
                // rather than hand the peer a stream with a hole in it.
                return false;
            }

            int payloadLength = nextEvent.Payload?.Length ?? 0;
            if (pending.QueuedBytes > 0 &&
                pending.QueuedBytes + payloadLength > MaxQueuedOutboundBytesPerChannel)
            {
                // Same budget as the outbound queue, and the same response to breaching it: fail the
                // channel, do not truncate it. EnqueueOutbound closes on overflow precisely because
                // "discarding bytes mid-stream would silently corrupt what is, from the peer's view,
                // a plain TCP connection" - and parked events are the same bytes, so dropping them
                // and then replaying only the prefix would produce exactly that corruption, with the
                // channel still live. A dead forward is diagnosable; a corrupted one is not.
                pending.Failed = true;
                pending.Events.Clear();
                pending.QueuedBytes = 0;
                _log($"[NativePortForwardSession] Pre-registration buffer for channel {nextEvent.StatusCode} exceeded its {MaxQueuedOutboundBytesPerChannel}-byte budget; the channel will be closed instead of delivering a truncated stream.");
                return false;
            }

            pending.Events.Add(nextEvent);
            pending.QueuedBytes += payloadLength;
            return false;
        }
    }

    /// <summary>
    /// Publishes a locally-opened channel, replaying anything that arrived for it before it was
    /// registered. Returns false when the id is already taken, leaving the caller to tear down.
    /// </summary>
    private bool PublishChannel(int channelId, ForwardChannelState state)
    {
        bool added;
        lock (_registrationGate)
        {
            PendingChannelEvents pending = _pendingByChannel.GetOrAdd(
                channelId,
                static _ => new PendingChannelEvents());

            if (pending.Failed)
            {
                // Bytes were lost before we could register, so this channel cannot be served
                // correctly. Refuse to publish it: the caller's failure path closes the SSH channel
                // and disposes the socket, which surfaces as a failed connection rather than a
                // silently truncated one.
                pending.Events.Clear();
                pending.QueuedBytes = 0;
                pending.Published = true;
                _pendingByChannel.TryRemove(channelId, out _);
                return false;
            }

            // Replay BEFORE publishing, not after. Once the channel is in _channels a concurrent
            // HandleEvent dispatches without taking this gate, and if that happened while parked
            // events were still being replayed the stream would be reordered - a subtler corruption
            // than the loss this fixes.
            foreach (NativeSshEvent parked in pending.Events)
            {
                DispatchToChannel(state, parked);
            }

            pending.Events.Clear();
            pending.QueuedBytes = 0;

            added = _channels.TryAdd(channelId, state);
            pending.Published = added;
        }

        _pendingByChannel.TryRemove(channelId, out _);
        return added;
    }

    /// <summary>
    /// Opens the window in which a locally-issued channel id exists but is not registered yet.
    /// </summary>
    private void BeginOpen()
    {
        lock (_registrationGate)
        {
            _opensInFlight++;
        }
    }

    /// <summary>
    /// Closes that window. When the last one finishes, anything still parked belongs to a channel
    /// that never registered (a failed open, or an id we will never see again), so drop it rather
    /// than let it sit — that is what keeps <see cref="_pendingByChannel"/> empty in steady state.
    /// </summary>
    /// <remarks>
    /// The decrement, the zero test and the sweep all happen under the same gate that
    /// <see cref="BeginOpen"/>, <see cref="TryResolveOrPark"/> and <see cref="PublishChannel"/>
    /// take, and that is the entire correctness argument: a new open cannot begin, and cannot park
    /// anything, part-way through a sweep.
    ///
    /// Two earlier attempts got this wrong in the same way. Dropping everything on a lock-free zero
    /// transition let a new open's events be swept; stamping entries with a generation counter still
    /// lost, because an open could bump the generation and be descheduled before bumping the count,
    /// so a sweeper read the NEW generation, saw zero in flight, and swept entries stamped with
    /// exactly that generation. Both tried to infer "abandoned" from state other threads were
    /// concurrently changing. Serialising the four operations removes the inference.
    ///
    /// A <c>Failed</c> entry is safe to remove here for the same reason: publishing only happens
    /// inside an open window, so at a zero transition under this gate nothing is about to publish.
    /// </remarks>
    private void EndOpen()
    {
        lock (_registrationGate)
        {
            if (--_opensInFlight != 0)
            {
                return;
            }

            foreach (KeyValuePair<int, PendingChannelEvents> entry in _pendingByChannel)
            {
                if (entry.Value.Published || _channels.ContainsKey(entry.Key))
                {
                    _pendingByChannel.TryRemove(entry.Key, out _);
                    continue;
                }

                if (entry.Value.Events.Count > 0)
                {
                    _log($"[NativePortForwardSession] Discarding {entry.Value.Events.Count} pre-registration event(s) for channel {entry.Key}: no open is in flight, so it will never be registered.");
                }

                entry.Value.Events.Clear();
                entry.Value.QueuedBytes = 0;
                _pendingByChannel.TryRemove(entry.Key, out _);
            }
        }
    }

    /// <summary>
    /// Events that arrived for a channel between its id being issued and its registration. Every
    /// member is read and written under <c>_registrationGate</c>, so none of them needs its own
    /// synchronisation.
    /// </summary>
    private sealed class PendingChannelEvents
    {
        public bool Published { get; set; }

        /// <summary>
        /// Bytes were lost before this channel could be registered, so it must be closed rather
        /// than published with a hole in its stream. See <see cref="TryResolveOrPark"/>.
        /// </summary>
        public bool Failed { get; set; }

        public List<NativeSshEvent> Events { get; } = [];

        public int QueuedBytes { get; set; }
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
            // Remote never reaches this method (the constructor routes it to CollectRemoteForward),
            // so only a kind this code has never heard of lands here.
            _ => throw new NotSupportedException($"Unsupported port-forward kind '{forward.Kind}'.")
        };
    }

    /// <summary>
    /// The bind address a remote rule asks the server for. Empty means the OpenSSH default for
    /// -R: the server's loopback. Also the string incoming announcements are matched against, so
    /// request and match can never disagree about the default.
    /// </summary>
    private static string RemoteBindAddress(PortForward forward) =>
        string.IsNullOrWhiteSpace(forward.BindAddress)
            ? "localhost"
            : forward.BindAddress.Trim();

    private void CollectRemoteForward(PortForward forward)
    {
        // Two rules asking the server for the very same listener cannot both be honored: their
        // connections would be indistinguishable, so whichever rule matched first would take the
        // second rule's traffic to the wrong destination. First rule wins, deterministically, and
        // the loser is refused by name rather than raced.
        string bindAddress = RemoteBindAddress(forward);
        if (_remoteForwards.Any(existing =>
                existing.SourcePort == forward.SourcePort
                && string.Equals(RemoteBindAddress(existing), bindAddress, StringComparison.OrdinalIgnoreCase)))
        {
            _log($"[NativePortForwardSession] Duplicate remote forward {forward} ignored: an earlier rule already claims {bindAddress}:{forward.SourcePort}.");
            _warn($"Warning: duplicate remote forward for {bindAddress}:{forward.SourcePort} ignored; the first rule wins.");
            return;
        }

        _remoteForwards.Add(forward);
    }

    /// <summary>
    /// The session is established (the Connected event arrived); ask the server for the remote
    /// listeners now. Not in the constructor, deliberately: the worker only answers commands once
    /// connect and auth (which can involve prompts) have finished, and the interop call blocks its
    /// thread until the server's verdict arrives — requesting from the constructor parked one
    /// thread-pool worker per rule for the whole handshake, which on a constrained pool could
    /// starve the very poll loop that answers the auth prompts. One task, sequential requests,
    /// only once there is a session to ask. Idempotent.
    /// </summary>
    public void NotifySessionEstablished()
    {
        if (_remoteForwards.Count == 0
            || Interlocked.Exchange(ref _remoteForwardsRequested, 1) != 0
            || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        CancellationToken lifetimeToken;
        try
        {
            lifetimeToken = _lifetimeCts.Token;
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check above and here; there is no session left to ask.
            return;
        }

        _ = Task.Run(() => RequestRemoteForwards(lifetimeToken), lifetimeToken);
    }

    private void RequestRemoteForwards(CancellationToken cancellationToken)
    {
        foreach (PortForward forward in _remoteForwards)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            string bindAddress = RemoteBindAddress(forward);

            // A refusal is loud but not fatal — the same behavior ssh itself has for -R
            // ("Warning: remote port forwarding failed") — so the session survives, the log
            // carries the detail, and the warning reaches the terminal where the user can see
            // which forward is not listening.
            try
            {
                int boundPort = _interop.RequestRemoteForward(_sessionHandle, bindAddress, forward.SourcePort);
                _log($"[NativePortForwardSession] Remote forward listening on {bindAddress}:{boundPort} for {forward}.");
            }
            catch (Exception ex)
            {
                _log($"[NativePortForwardSession] Remote forward {forward} failed: {ex.Message}");
                _warn($"Warning: remote port forwarding failed for {bindAddress}:{forward.SourcePort}.");
            }
        }
    }

    /// <summary>
    /// A connection arrived on a remote-forward listener. The channel state is registered
    /// immediately, on the poll loop — its data events may be queued right behind this one, and
    /// the state's outbound queue is what holds them in order while the local destination is
    /// still being dialled. The dial itself happens on a background task.
    /// </summary>
    private void HandleIncomingForwardChannel(NativeSshEvent nextEvent)
    {
        int channelId = nextEvent.StatusCode;

        string connectedAddress;
        int connectedPort;
        try
        {
            using JsonDocument payload = JsonDocument.Parse(nextEvent.Payload);
            connectedAddress = payload.RootElement.GetProperty("connectedAddress").GetString() ?? string.Empty;
            connectedPort = payload.RootElement.GetProperty("connectedPort").GetInt32();
        }
        catch (Exception ex)
        {
            _log($"[NativePortForwardSession] Unreadable incoming forward payload for channel {channelId}: {ex.Message}");
            TryCloseInteropChannel(channelId);
            return;
        }

        PortForward? rule = MatchRemoteForwardRule(connectedAddress, connectedPort);
        if (rule == null)
        {
            // Unsolicited or ambiguous: either no rule asked for this listener, or more than one
            // could have and the address decides nothing. Refuse rather than guess a destination.
            _log($"[NativePortForwardSession] Incoming forward channel {channelId} from listener {connectedAddress}:{connectedPort} matches no remote forward rule unambiguously; closing it.");
            TryCloseInteropChannel(channelId);
            return;
        }

        var state = new ForwardChannelState(channelId);
        if (!_channels.TryAdd(channelId, state))
        {
            TryCloseInteropChannel(channelId);
            return;
        }

        // Same disposal-race guard as NotifySessionEstablished: HandleEvent's _disposed check can
        // be overtaken by a Dispose that finishes (including the CTS disposal) before this line —
        // the acknowledged tail of NativeSshSession.Dispose's bounded wait. A disposed CTS here
        // must tear the just-registered channel down, not throw out of the poll loop.
        CancellationToken lifetimeToken;
        try
        {
            lifetimeToken = _lifetimeCts.Token;
        }
        catch (ObjectDisposedException)
        {
            RemoveChannel(channelId, closeInteropChannel: true);
            return;
        }

        _ = Task.Run(() => ConnectIncomingForwardAsync(state, rule, lifetimeToken), lifetimeToken);
    }

    /// <summary>
    /// Which remote rule an incoming connection belongs to. Exact (address, port) first: rules can
    /// legitimately share a port across different bind addresses, and the requests race — the rule
    /// whose listener actually bound may not be the first one with the port, so a port-only pick
    /// could route traffic to the wrong local service. The port-only fallback exists because some
    /// servers normalize the address they echo ("localhost" requested, "127.0.0.1" announced), and
    /// it is taken only while it cannot choose wrongly — exactly one rule on the port.
    /// </summary>
    private PortForward? MatchRemoteForwardRule(string connectedAddress, int connectedPort)
    {
        // Exactly one exact match or none: CollectRemoteForward refuses duplicate (address, port)
        // rules at setup, so more than one here means that invariant broke — refuse rather than
        // route a connection to a destination that is a coin flip.
        List<PortForward> exact = _remoteForwards
            .Where(rule =>
                rule.SourcePort == connectedPort
                && string.Equals(RemoteBindAddress(rule), connectedAddress, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }

        if (exact.Count > 1)
        {
            return null;
        }

        List<PortForward> byPort = _remoteForwards.Where(rule => rule.SourcePort == connectedPort).ToList();
        return byPort.Count == 1 ? byPort[0] : null;
    }

    private async Task ConnectIncomingForwardAsync(ForwardChannelState state, PortForward rule, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(rule.DestinationHost, rule.DestinationPort, cancellationToken).ConfigureAwait(false);
            state.AttachTransport(client);
        }
        catch (Exception ex)
        {
            client.Dispose();
            _log($"[NativePortForwardSession] Remote forward channel {state.ChannelId} could not reach {rule.DestinationHost}:{rule.DestinationPort}: {ex.Message}");
            RemoveChannel(state.ChannelId, closeInteropChannel: true);
            return;
        }

        // Teardown may have removed the channel while the dial was in flight; its RemoveChannel saw
        // a transport-less state, so the socket is ours to close. A removal racing past this check
        // instead lands the pumps on a disposed socket, which they already treat as end-of-channel.
        if (!_channels.ContainsKey(state.ChannelId))
        {
            client.Dispose();
            return;
        }

        _ = Task.Run(() => PumpClientToSshAsync(state, cancellationToken), cancellationToken);
        _ = Task.Run(() => PumpSshToClientAsync(state, cancellationToken), cancellationToken);
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

                // The in-flight window has to open BEFORE the id is issued and close only after the
                // channel is published: everything in between is time in which the poll loop can be
                // handed data for an id this map does not know yet. See TryResolveOrPark.
                int channelId;
                ForwardChannelState state;
                BeginOpen();
                try
                {
                    channelId = _interop.OpenDirectTcpIp(
                        _sessionHandle,
                        new NativePortForwardOpenOptions
                        {
                            HostToConnect = forward.DestinationHost,
                            PortToConnect = forward.DestinationPort,
                            OriginatorAddress = remoteEndPoint.Address.ToString(),
                            OriginatorPort = remoteEndPoint.Port
                        });

                    state = new ForwardChannelState(channelId, client);
                    if (!PublishChannel(channelId, state))
                    {
                        client.Dispose();
                        _interop.CloseChannel(_sessionHandle, channelId);
                        continue;
                    }
                }
                finally
                {
                    EndOpen();
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

            // Same pre-registration window as the local accept loop, for the same reason - the open
            // blocks until the server answers, so data can reach the poll loop before this channel
            // is published. See TryResolveOrPark.
            int channelId;
            ForwardChannelState state;
            bool published;
            BeginOpen();
            try
            {
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

                state = new ForwardChannelState(channelId, client);
                published = PublishChannel(channelId, state);
            }
            finally
            {
                EndOpen();
            }

            if (!published)
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
            TryCloseInteropChannel(channelId);
        }

        try
        {
            channel.Client?.Dispose();
        }
        catch
        {
        }
    }

    private void TryCloseInteropChannel(int channelId)
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

    private static void TryShutdown(TcpClient? client, SocketShutdown how)
    {
        try
        {
            client?.Client.Shutdown(how);
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

        /// <summary>
        /// For incoming (remote-forward) channels, whose local transport does not exist yet: the
        /// SSH side is live immediately, so the state — above all its outbound queue — must exist
        /// before the dial to the destination completes. Pumps start only after
        /// <see cref="AttachTransport"/>, so they never observe the null transport.
        /// </summary>
        public ForwardChannelState(int channelId)
        {
            ChannelId = channelId;

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

        public ForwardChannelState(int channelId, TcpClient client)
            : this(channelId)
        {
            AttachTransport(client);
        }

        private NetworkStream? _stream;

        public int ChannelId { get; }
        public TcpClient? Client { get; private set; }
        public Channel<ForwardOutbound> Outbound { get; }

        /// <summary>
        /// The local transport. Throws rather than null-refs if read before
        /// <see cref="AttachTransport"/>: the pumps are only ever started after the transport is
        /// attached, so reaching the throw means that contract was broken — a loud, named failure
        /// beats a NullReferenceException three calls later.
        /// </summary>
        public NetworkStream Stream =>
            _stream ?? throw new InvalidOperationException(
                $"Forward channel {ChannelId} has no transport attached yet.");

        public void AttachTransport(TcpClient client)
        {
            Client = client;
            _stream = client.GetStream();
        }

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

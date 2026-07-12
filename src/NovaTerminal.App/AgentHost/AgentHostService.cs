using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.AgentHost.Contracts;
using NovaTerminal.Replay;
using NovaTerminal.VT;

namespace NovaTerminal.AgentHost
{
    /// <summary>
    /// Local IPC endpoint for the agent-host observe surface (milestone A1/PR3,
    /// docs/plans/2026-07-07-agent-host-a1-observe-design.md).
    ///
    /// Off by default: nothing listens until <see cref="Apply"/> is called with
    /// <c>TerminalSettings.AgentAccessObserveEnabled == true</c>. When enabled it
    /// serves <c>listSessions</c> / <c>readScreen</c> / <c>readScrollback</c> over a
    /// per-user local endpoint — a named pipe with <see cref="PipeOptions.CurrentUserOnly"/>
    /// on Windows, a unix domain socket (mode 0600) elsewhere — and writes an
    /// <see cref="EndpointDescriptor"/> discovery file next to settings.json.
    ///
    /// The protocol is observe-only by construction: no input, spawn, or close
    /// methods exist in v1. Screen reads go through the deterministic
    /// <see cref="BufferSnapshot"/> path under the buffer's read lock — the same
    /// snapshot boundary the replay/parity tests use.
    /// </summary>
    public sealed class AgentHostService : IDisposable
    {
        /// <summary>Process-wide instance used by the app wiring. Tests construct their own.</summary>
        public static AgentHostService Instance { get; } = new(AgentSessionRegistry.Instance);

        private readonly AgentSessionRegistry _registry;
        private readonly string? _endpointOverride;
        private readonly string? _discoveryDirectoryOverride;
        private readonly string? _exportDirectoryOverride;
        private readonly AgentActivityJournal _journal;
        private readonly object _gate = new();

        private CancellationTokenSource? _cts;
        private Task? _acceptLoop;
        private Socket? _unixListener;
        private string? _unixSocketPath;
        private string? _discoveryFilePath;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Stream, byte> _activeClients = new();

        // A2 status plumbing — exists only while the endpoint is running
        // (off-is-off: no ring, no timer, no subscriptions when disabled).
        private AgentEventRing? _eventRing;
        private Timer? _sweepTimer;
        private readonly Dictionary<AgentSessionRegistration, Action<AgentSessionStatusEvent>> _statusSubscriptions = new();

        public AgentHostService(
            AgentSessionRegistry registry,
            string? endpointOverride = null,
            string? discoveryDirectoryOverride = null,
            string? exportDirectoryOverride = null,
            AgentActivityJournal? journal = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _endpointOverride = endpointOverride;
            _discoveryDirectoryOverride = discoveryDirectoryOverride;
            _exportDirectoryOverride = exportDirectoryOverride;
            _journal = journal ?? AgentActivityJournal.Instance;
        }

        // Volatile: written by the UI thread (settings apply), read by the IPC
        // thread per exportReplay request — the store/load barrier makes a
        // toggle visible to in-flight connections immediately.
        private volatile bool _replayExportEnabled;

        /// <summary>
        /// Second default-off gate for <c>exportReplay</c> (A4): mirrors
        /// <c>TerminalSettings.AgentReplayExportEnabled</c>, pushed by
        /// MainWindow alongside <see cref="Apply"/>. Both the observe toggle
        /// (endpoint running) and this flag must be on for an export to
        /// succeed — the "explicit export action" tier of the DIRECTION
        /// permission table.
        /// </summary>
        public bool ReplayExportEnabled
        {
            get => _replayExportEnabled;
            set => _replayExportEnabled = value;
        }

        // A3 act gate. Volatile for the same UI-writes/IPC-reads reason as the
        // export gate above.
        private volatile bool _actEnabled;

        /// <summary>
        /// Separate default-off opt-in for the acting surface (A3):
        /// <c>sendInput</c> and later spawn/close. Mirrors
        /// <c>TerminalSettings.AgentAccessActEnabled</c>, pushed by MainWindow
        /// alongside <see cref="Apply"/>. On top of observe; SSH targets need
        /// per-profile allowlisting as well (see <see cref="AllowsAgentActOnProfile"/>).
        /// </summary>
        public bool ActEnabled
        {
            get => _actEnabled;
            set => _actEnabled = value;
        }

        // Per-profile SSH allowlist probe, published by MainWindow (reads the
        // SSH profile store). Null (no probe wired) denies every SSH profile —
        // fail closed. Volatile: published from the UI thread, read on IPC.
        private volatile Func<Guid, bool>? _sshProfileAllowlist;

        /// <summary>Publishes (or clears) the per-profile SSH allowlist probe. UI thread.</summary>
        public void SetSshProfileAllowlist(Func<Guid, bool>? probe) => _sshProfileAllowlist = probe;

        private bool AllowsAgentActOnProfile(Guid profileId)
        {
            var probe = _sshProfileAllowlist;
            if (probe == null) return false; // fail closed
            try
            {
                return probe(profileId);
            }
            catch
            {
                return false;
            }
        }

        public bool IsRunning
        {
            get { lock (_gate) { return _cts != null; } }
        }

        /// <summary>The active endpoint (pipe name or socket path), or null when stopped.</summary>
        public string? EndpointName { get; private set; }

        /// <summary>Starts or stops the endpoint to match the observe setting. Safe to call repeatedly.</summary>
        public void Apply(bool enabled)
        {
            if (enabled) Start(); else Stop();
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_cts != null) return;

                // AgentHostDiscovery mirrors AppPaths.RootDirectory; using it here
                // keeps writer (app) and reader (MCP server) on one path by construction.
                var discoveryDir = _discoveryDirectoryOverride ?? AgentHostDiscovery.GetDefaultDirectory();
                var discoveryPath = Path.Combine(discoveryDir, AgentHostProtocol.DiscoveryFileName);

                // First instance wins: if another live NovaTerminal already
                // advertises an endpoint, leave it alone.
                if (TryReadForeignLiveDescriptor(discoveryPath))
                {
                    Debug.WriteLine("[AgentHost] Another live instance owns the endpoint; not starting.");
                    return;
                }

                var endpoint = _endpointOverride ?? DefaultEndpointName();
                _cts = new CancellationTokenSource();
                var token = _cts.Token;

                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        _acceptLoop = Task.Run(() => AcceptNamedPipeLoopAsync(endpoint, token), token);
                    }
                    else
                    {
                        StartUnixListener(endpoint);
                        _acceptLoop = Task.Run(() => AcceptUnixLoopAsync(token), token);
                    }

                    Directory.CreateDirectory(discoveryDir);
                    var descriptor = new EndpointDescriptor
                    {
                        Version = AgentHostProtocol.Version,
                        Endpoint = endpoint,
                        Pid = Environment.ProcessId,
                    };
                    File.WriteAllText(
                        discoveryPath,
                        JsonSerializer.Serialize(descriptor, AgentHostJsonContext.Default.EndpointDescriptor));
                    _discoveryFilePath = discoveryPath;
                    EndpointName = endpoint;

                    StartStatusPlumbingLocked();
                }
                catch (Exception ex)
                {
                    // The agent host is an optional surface: a failure to bind
                    // (permissions, stale endpoint, another listener) must never
                    // take the terminal down. Leave the app fully functional.
                    StopLocked();
                    Debug.WriteLine($"[AgentHost] failed to start observe endpoint: {ex.Message}");
                }
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                StopLocked();
            }
        }

        public void Dispose() => Stop();

        private void StopLocked()
        {
            StopStatusPlumbingLocked();

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _acceptLoop = null;
            EndpointName = null;

            // Disabling Agent Access must revoke access *now*: cancelling the
            // accept loop is not enough, because already-accepted connections
            // could otherwise keep reading screens on their open stream.
            foreach (var client in _activeClients.Keys)
            {
                try { client.Dispose(); } catch { /* best effort */ }
            }
            _activeClients.Clear();

            try { _unixListener?.Dispose(); } catch { /* best effort */ }
            _unixListener = null;
            if (_unixSocketPath != null)
            {
                try { File.Delete(_unixSocketPath); } catch { /* best effort */ }
                _unixSocketPath = null;
            }

            if (_discoveryFilePath != null)
            {
                ReleaseDiscoveryFile(_discoveryFilePath);
                _discoveryFilePath = null;
            }

            // Release the SSH allowlist probe. It closes over MainWindow (via the
            // instance method it points at); this static singleton outlives the
            // window, so holding the delegate would pin the closed window (and its
            // tabs, PTYs, controls) in memory. MainWindow re-publishes it before
            // each Apply, so clearing here is safe. Bool gates are value types and
            // do not leak, so they are left as-is.
            _sshProfileAllowlist = null;
        }

        /// <summary>
        /// Retires our discovery descriptor without racing other instances.
        /// The pid check and the release happen under one exclusive file handle
        /// (FileShare.None), so another instance cannot write its descriptor
        /// between "it's ours" and the clear. We truncate instead of deleting:
        /// a delete would have to happen after the handle closes, reopening the
        /// window — while an empty file is already treated as a stale
        /// descriptor by <see cref="TryReadForeignLiveDescriptor"/> and by
        /// clients, and is rewritten in place by the next Start().
        /// </summary>
        private static void ReleaseDiscoveryFile(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                EndpointDescriptor? descriptor = null;
                try
                {
                    using var reader = new StreamReader(fs, leaveOpen: true);
                    descriptor = JsonSerializer.Deserialize(
                        reader.ReadToEnd(), AgentHostJsonContext.Default.EndpointDescriptor);
                }
                catch (JsonException)
                {
                    // Unreadable == stale == safe to clear.
                }

                if (descriptor == null || descriptor.Pid == Environment.ProcessId)
                {
                    fs.SetLength(0);
                    fs.Flush();
                }
                // else: another instance took over the endpoint — leave its
                // descriptor untouched.
            }
            catch
            {
                // Missing file, or a foreign instance holds the lock right now:
                // either way there is nothing of ours left to clean up.
            }
        }

        // ── A2 status plumbing ───────────────────────────────────────────────

        private void StartStatusPlumbingLocked()
        {
            _eventRing = new AgentEventRing();
            _registry.SessionRegistered += OnSessionRegistered;
            _registry.SessionUnregistered += OnSessionUnregistered;

            // Sessions that were already open when the endpoint started get
            // forwarding but no synthetic sessionOpened: a fresh client learns
            // about them via listSessions, not via a burst of stale events.
            // They also start flight recording (A4): the ring exists exactly
            // while the observe endpoint runs — off-is-off.
            foreach (var registration in _registry.GetRegistrations())
            {
                AttachStatusForwarding(registration);
                registration.EnableFlightRecording(AgentHostProtocol.FlightRecorderMaxBytesPerSession);
            }

            _sweepTimer = new Timer(_ => SweepStatuses(), null,
                dueTime: TimeSpan.FromSeconds(1), period: TimeSpan.FromSeconds(1));
        }

        private void StopStatusPlumbingLocked()
        {
            _sweepTimer?.Dispose();
            _sweepTimer = null;

            _registry.SessionRegistered -= OnSessionRegistered;
            _registry.SessionUnregistered -= OnSessionUnregistered;

            // Off-is-off: disabling Agent Access drops every flight ring now —
            // no in-memory retention survives the toggle.
            foreach (var registration in _registry.GetRegistrations())
            {
                registration.DisableFlightRecording();
            }

            foreach (var (registration, handler) in _statusSubscriptions)
            {
                registration.StatusMachine.EventEmitted -= handler;
            }
            _statusSubscriptions.Clear();
            _eventRing = null;
        }

        private void OnSessionRegistered(AgentSessionRegistration registration)
        {
            AgentEventRing? ring;
            lock (_gate)
            {
                ring = _eventRing;
                if (ring == null) return;
                AttachStatusForwardingLocked(registration, ring);
                registration.EnableFlightRecording(AgentHostProtocol.FlightRecorderMaxBytesPerSession);
            }

            var status = registration.StatusMachine.Snapshot();
            ring.Append(registration.PaneId, AgentHostProtocol.EventTypes.SessionOpened, status.Kind.ToWire(), DateTimeOffset.UtcNow);
        }

        private void OnSessionUnregistered(AgentSessionRegistration registration)
        {
            AgentEventRing? ring;
            lock (_gate)
            {
                ring = _eventRing;
                if (_statusSubscriptions.Remove(registration, out var handler))
                {
                    registration.StatusMachine.EventEmitted -= handler;
                }
            }

            var status = registration.StatusMachine.Snapshot();
            ring?.Append(registration.PaneId, AgentHostProtocol.EventTypes.SessionClosed, status.Kind.ToWire(), DateTimeOffset.UtcNow, status.ExitCode);
        }

        private void AttachStatusForwarding(AgentSessionRegistration registration)
        {
            // Caller holds _gate (Start path).
            if (_eventRing is { } ring)
            {
                AttachStatusForwardingLocked(registration, ring);
            }
        }

        private void AttachStatusForwardingLocked(AgentSessionRegistration registration, AgentEventRing ring)
        {
            if (_statusSubscriptions.ContainsKey(registration)) return;

            // PaneId is read at event time (it can be re-keyed by session
            // restore); the ring instance is captured so a stopped endpoint's
            // orphaned events can never land in a newer ring.
            void Handler(AgentSessionStatusEvent evt) => ring.Append(
                registration.PaneId,
                evt.Type.ToWire(),
                evt.Status.ToWire(),
                evt.Timestamp,
                evt.ExitCode,
                evt.Duration is { } d ? (long)d.TotalMilliseconds : null);

            _statusSubscriptions[registration] = Handler;
            registration.StatusMachine.EventEmitted += Handler;
        }

        private void SweepStatuses()
        {
            foreach (var registration in _registry.GetRegistrations())
            {
                try
                {
                    registration.StatusMachine.Sweep(registration.ProbeHasActiveChildProcesses());
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AgentHost] status sweep failed for {registration.PaneId}: {ex.Message}");
                }
            }
        }

        private static string DefaultEndpointName()
        {
            if (OperatingSystem.IsWindows())
            {
                // CurrentUserOnly enforces the ACL; the user name only namespaces
                // the pipe so different users on one machine don't collide.
                var user = new string(Array.FindAll(
                    Environment.UserName.ToLowerInvariant().ToCharArray(), char.IsLetterOrDigit));
                return AgentHostProtocol.WindowsPipeNamePrefix + user;
            }

            return Path.Combine(NovaTerminal.Shell.AppPaths.RootDirectory, AgentHostProtocol.UnixSocketFileName);
        }

        private static bool TryReadForeignLiveDescriptor(string discoveryPath)
        {
            try
            {
                if (!File.Exists(discoveryPath)) return false;
                var descriptor = JsonSerializer.Deserialize(
                    File.ReadAllText(discoveryPath), AgentHostJsonContext.Default.EndpointDescriptor);
                if (descriptor == null || descriptor.Pid == Environment.ProcessId) return false;

                // Guard against PID recycling: the pid must be alive AND be a
                // NovaTerminal process before we defer to it.
                var process = Process.GetProcessById(descriptor.Pid);
                using var current = Process.GetCurrentProcess();
                return !process.HasExited
                    && string.Equals(process.ProcessName, current.ProcessName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Unreadable descriptor or dead pid: stale, safe to replace.
                return false;
            }
        }

        // ── Transport ────────────────────────────────────────────────────────

        private async Task AcceptNamedPipeLoopAsync(string pipeName, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    var connected = server;
                    server = null;
                    _ = Task.Run(() => HandleClientAsync(connected, token), token);
                }
                catch (OperationCanceledException)
                {
                    server?.Dispose();
                    return;
                }
                catch (Exception ex)
                {
                    server?.Dispose();
                    if (token.IsCancellationRequested) return;
                    Debug.WriteLine($"[AgentHost] pipe accept failed: {ex.Message}");
                    try { await Task.Delay(250, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        private void StartUnixListener(string socketPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);

            // Never unlink a live socket: if a file is present, probe it first.
            // Only a socket nobody answers on is stale and safe to remove.
            if (File.Exists(socketPath))
            {
                if (IsUnixSocketAlive(socketPath))
                {
                    throw new IOException($"Another live agent-host endpoint is listening on '{socketPath}'.");
                }
                File.Delete(socketPath);
            }

            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(backlog: 4);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            _unixListener = listener;
            _unixSocketPath = socketPath;
        }

        private static bool IsUnixSocketAlive(string socketPath)
        {
            try
            {
                using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                probe.Connect(new UnixDomainSocketEndPoint(socketPath));
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private async Task AcceptUnixLoopAsync(CancellationToken token)
        {
            var listener = _unixListener!;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptAsync(token).ConfigureAwait(false);
                    var stream = new NetworkStream(client, ownsSocket: true);
                    _ = Task.Run(() => HandleClientAsync(stream, token), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return; // Stop() disposed the listener
                }
                catch (Exception ex)
                {
                    // Transient accept failure: log and retry, mirroring the
                    // named-pipe loop, so the service never ends up half-running
                    // (IsRunning true with a dead accept loop).
                    if (token.IsCancellationRequested) return;
                    Debug.WriteLine($"[AgentHost] socket accept failed: {ex.Message}");
                    try { await Task.Delay(250, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        private async Task HandleClientAsync(Stream stream, CancellationToken token)
        {
            _activeClients.TryAdd(stream, 0);
            try
            {
                using var _ = stream;
                using var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    if (line == null) return;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var response = await HandleRequestLineAsync(line, token).ConfigureAwait(false);
                    await writer.WriteLineAsync(
                        JsonSerializer.Serialize(response, AgentHostJsonContext.Default.AgentHostResponse))
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgentHost] client connection ended: {ex.Message}");
            }
            finally
            {
                _activeClients.TryRemove(stream, out _);
            }
        }

        // ── Request handling ─────────────────────────────────────────────────

        internal async Task<AgentHostResponse> HandleRequestLineAsync(string line, CancellationToken cancellationToken)
        {
            AgentHostRequest? request;
            try
            {
                request = JsonSerializer.Deserialize(line, AgentHostJsonContext.Default.AgentHostRequest);
            }
            catch (JsonException ex)
            {
                return Error(0, AgentHostProtocol.ErrorCodes.MalformedRequest, $"Unparseable frame: {ex.Message}");
            }

            if (request == null)
            {
                return Error(0, AgentHostProtocol.ErrorCodes.MalformedRequest, "Empty frame.");
            }

            if (request.Version != AgentHostProtocol.Version)
            {
                return Error(
                    request.Id,
                    AgentHostProtocol.ErrorCodes.VersionMismatch,
                    $"Protocol version {request.Version} is not supported; this endpoint speaks version {AgentHostProtocol.Version}.");
            }

            try
            {
                return request.Method switch
                {
                    AgentHostProtocol.Methods.ListSessions => HandleListSessions(request),
                    AgentHostProtocol.Methods.ReadScreen => HandleReadScreen(request),
                    AgentHostProtocol.Methods.ReadScrollback => HandleReadScrollback(request),
                    AgentHostProtocol.Methods.GetSessionStatus => HandleGetSessionStatus(request),
                    AgentHostProtocol.Methods.WaitForEvents => await HandleWaitForEventsAsync(request, cancellationToken).ConfigureAwait(false),
                    AgentHostProtocol.Methods.ExportReplay => HandleExportReplay(request),
                    AgentHostProtocol.Methods.SendInput => HandleSendInput(request),
                    _ => Error(request.Id, AgentHostProtocol.ErrorCodes.UnknownMethod, $"Unknown method '{request.Method}'."),
                };
            }
            catch (OperationCanceledException)
            {
                throw; // endpoint stopping / client gone — the connection loop owns this
            }
            catch (JsonException ex)
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest, $"Bad params: {ex.Message}");
            }
            catch (Exception ex)
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.Internal, ex.Message);
            }
        }

        private AgentHostResponse HandleGetSessionStatus(AgentHostRequest request)
        {
            var p = DeserializeParams(request, AgentHostJsonContext.Default.GetSessionStatusParams);
            if (p == null)
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest, "getSessionStatus requires params with a paneId.");
            }
            if (!_registry.TryGet(p.PaneId, out var registration))
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.SessionNotFound, $"No live session with paneId '{p.PaneId}'.");
            }

            var dto = registration.StatusMachine.Snapshot().ToDto(registration.PaneId);
            return Ok(request.Id, JsonSerializer.SerializeToElement(dto, AgentHostJsonContext.Default.SessionStatusDto));
        }

        private async Task<AgentHostResponse> HandleWaitForEventsAsync(AgentHostRequest request, CancellationToken cancellationToken)
        {
            var p = DeserializeParams(request, AgentHostJsonContext.Default.WaitForEventsParams);
            if (p == null)
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest, "waitForEvents requires params with sinceSeq and timeoutMs.");
            }

            var ring = _eventRing;
            if (ring == null)
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.Internal, "Event channel is not available.");
            }

            var sinceSeq = Math.Max(0, p.SinceSeq);
            var timeout = TimeSpan.FromMilliseconds(Math.Clamp(p.TimeoutMs, 0, AgentHostProtocol.MaxWaitForEventsTimeoutMs));
            var result = await ring.WaitSinceAsync(sinceSeq, timeout, cancellationToken).ConfigureAwait(false);
            return Ok(request.Id, JsonSerializer.SerializeToElement(result, AgentHostJsonContext.Default.WaitForEventsResult));
        }

        private AgentHostResponse HandleExportReplay(AgentHostRequest request)
        {
            var p = DeserializeParams(request, AgentHostJsonContext.Default.ExportReplayParams);
            if (p == null)
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest, "exportReplay requires params with a paneId.");
            }

            // Second default-off gate (DIRECTION permission table: "observe
            // permission + explicit export action"). The observe toggle got the
            // caller this far; replay export needs its own opt-in.
            if (!ReplayExportEnabled)
            {
                return Error(
                    request.Id,
                    AgentHostProtocol.ErrorCodes.ExportDisabled,
                    "Replay export is disabled. Enable Settings → Agent access (observe) → Agent replay export in NovaTerminal, then retry. Exports contain terminal output and resizes only — never typed input.");
            }

            if (!_registry.TryGet(p.PaneId, out var registration))
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.SessionNotFound, $"No live session with paneId '{p.PaneId}'.");
            }

            var exportDir = _exportDirectoryOverride
                ?? Path.Combine(NovaTerminal.Shell.AppPaths.RecordingsDirectory, AgentHostProtocol.AgentExportsSubdirectory);
            Directory.CreateDirectory(exportDir);
            // Fresh random suffix per export (same scheme as manual recordings):
            // the timestamp alone has one-second resolution, so repeated exports
            // for one pane within a second must not compute the same path — the
            // writer truncates, which would silently destroy the earlier file.
            var fileName = Controls.TerminalPane.BuildRecordingFileName(DateTime.Now, Guid.NewGuid().ToString("N"));
            var filePath = Path.Combine(exportDir, fileName);

            if (!registration.TryExportFlightRecording(filePath, out var info))
            {
                return Error(
                    request.Id,
                    AgentHostProtocol.ErrorCodes.ExportUnavailable,
                    "No flight recording is available for this session right now (the session may still be starting, already closed, or the file could not be written).");
            }

            var result = new ExportReplayResult
            {
                FilePath = Path.GetFullPath(filePath),
                EventCount = info.EventCount,
                FirstEventMs = info.FirstEventMs,
                LastEventMs = info.LastEventMs,
                TruncatedAtStart = info.TruncatedAtStart,
            };
            return Ok(request.Id, JsonSerializer.SerializeToElement(result, AgentHostJsonContext.Default.ExportReplayResult));
        }

        private AgentHostResponse HandleSendInput(AgentHostRequest request)
        {
            SendInputParams? p;
            try
            {
                p = DeserializeParams(request, AgentHostJsonContext.Default.SendInputParams);
            }
            catch (JsonException)
            {
                // Missing required fields / bad shapes throw here rather than
                // returning null. Journal it: a malformed acting attempt is still
                // an externally reachable acting attempt the user should see.
                p = null;
            }
            if (p == null || p.Text == null)
            {
                return Journaled(request, AgentHostProtocol.Methods.SendInput, p?.PaneId, "input",
                    Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest, "sendInput requires params with a paneId and text."));
            }

            // Size cap before any lookup so a flood is rejected cheaply.
            int byteCount = Encoding.UTF8.GetByteCount(p.Text);
            if (byteCount > AgentHostProtocol.MaxSendInputBytes)
            {
                return Journaled(request, AgentHostProtocol.Methods.SendInput, p.PaneId, "input",
                    Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest,
                        $"Input exceeds the {AgentHostProtocol.MaxSendInputBytes}-byte per-call limit."));
            }

            // Separate act opt-in (DIRECTION: acting never rides the observe toggle).
            if (!_actEnabled)
            {
                return Journaled(request, AgentHostProtocol.Methods.SendInput, p.PaneId, "input",
                    Error(request.Id, AgentHostProtocol.ErrorCodes.ActDisabled,
                        "Acting is disabled. Enable Settings → Agent access (observe) → Agent access (act) in NovaTerminal, then retry."));
            }

            if (!_registry.TryGet(p.PaneId, out var registration))
            {
                return Journaled(request, AgentHostProtocol.Methods.SendInput, p.PaneId, "input",
                    Error(request.Id, AgentHostProtocol.ErrorCodes.SessionNotFound, $"No live session with paneId '{p.PaneId}'."));
            }

            // Per-profile SSH allowlist: acting on a remote reaches another
            // machine with the user's credentials, so it is opt-in per profile.
            if (string.Equals(registration.Kind, "ssh", StringComparison.Ordinal))
            {
                if (registration.ProfileId is not { } profileId || !AllowsAgentActOnProfile(profileId))
                {
                    return Journaled(request, AgentHostProtocol.Methods.SendInput, p.PaneId, registration.ProfileName,
                        Error(request.Id, AgentHostProtocol.ErrorCodes.ProfileNotAllowed,
                            $"The SSH profile '{registration.ProfileName}' is not allowlisted for agent access. Enable it in the connection's settings, then retry."));
                }
            }

            if (!registration.TrySendInput(p.Text))
            {
                return Journaled(request, AgentHostProtocol.Methods.SendInput, p.PaneId, registration.ProfileName,
                    Error(request.Id, AgentHostProtocol.ErrorCodes.SessionNotRunning,
                        "The session is not accepting input (its process has exited or is being torn down)."));
            }

            var result = new SendInputResult { BytesSent = byteCount };
            return Journaled(request, AgentHostProtocol.Methods.SendInput, p.PaneId, registration.ProfileName,
                Ok(request.Id, JsonSerializer.SerializeToElement(result, AgentHostJsonContext.Default.SendInputResult)));
        }

        /// <summary>
        /// Records an acting attempt to the journal (allowed or denied) and
        /// returns the response unchanged. Outcome = "ok" or the error code, so
        /// the user sees everything an agent tried.
        /// </summary>
        private AgentHostResponse Journaled(AgentHostRequest request, string method, Guid? paneId, string target, AgentHostResponse response)
        {
            _journal.Record(method, paneId, target, response.Error?.Code ?? "ok");
            return response;
        }

        private AgentHostResponse HandleListSessions(AgentHostRequest request)
        {
            var result = new ListSessionsResult { Sessions = _registry.ListSessions() };
            return Ok(request.Id, JsonSerializer.SerializeToElement(result, AgentHostJsonContext.Default.ListSessionsResult));
        }

        private AgentHostResponse HandleReadScreen(AgentHostRequest request)
        {
            var p = DeserializeParams(request, AgentHostJsonContext.Default.ReadScreenParams);
            if (p == null)
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest, "readScreen requires params with a paneId.");
            }
            if (!_registry.TryGet(p.PaneId, out var registration))
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.SessionNotFound, $"No live session with paneId '{p.PaneId}'.");
            }

            var buffer = registration.Buffer;
            BufferSnapshot snapshot;
            bool cursorVisible;
            int rows, cols;
            buffer.Lock.EnterReadLock();
            try
            {
                snapshot = BufferSnapshot.Capture(buffer, p.IncludeAttributes);
                cursorVisible = buffer.IsCursorVisible;
                rows = buffer.Rows;
                cols = buffer.Cols;
            }
            finally
            {
                buffer.Lock.ExitReadLock();
            }

            var dto = new ScreenSnapshotDto
            {
                Lines = snapshot.Lines,
                AttributeLines = snapshot.AttributeLines,
                CursorRow = snapshot.CursorRow,
                CursorCol = snapshot.CursorCol,
                CursorVisible = cursorVisible,
                Rows = rows,
                Cols = cols,
            };
            return Ok(request.Id, JsonSerializer.SerializeToElement(dto, AgentHostJsonContext.Default.ScreenSnapshotDto));
        }

        private AgentHostResponse HandleReadScrollback(AgentHostRequest request)
        {
            var p = DeserializeParams(request, AgentHostJsonContext.Default.ReadScrollbackParams);
            if (p == null)
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest, "readScrollback requires params with paneId, startLine, and maxLines.");
            }
            if (!_registry.TryGet(p.PaneId, out var registration))
            {
                return Error(request.Id, AgentHostProtocol.ErrorCodes.SessionNotFound, $"No live session with paneId '{p.PaneId}'.");
            }

            var buffer = registration.Buffer;
            string[] lines;
            int effectiveStart;
            int total;
            buffer.Lock.EnterReadLock();
            try
            {
                total = buffer.Scrollback.Count;
                effectiveStart = Math.Clamp(p.StartLine, 0, total);
                var count = Math.Clamp(p.MaxLines, 0, AgentHostProtocol.MaxScrollbackLinesPerRequest);
                count = Math.Min(count, total - effectiveStart);

                lines = new string[count];
                for (var i = 0; i < count; i++)
                {
                    var index = effectiveStart + i;
                    lines[i] = RenderScrollbackRow(
                        buffer.Scrollback.GetRow(index),
                        buffer.Scrollback.GetExtendedTextMap(index));
                }
            }
            finally
            {
                buffer.Lock.ExitReadLock();
            }

            var result = new ReadScrollbackResult
            {
                Lines = lines,
                StartLine = effectiveStart,
                TotalLines = total,
            };
            return Ok(request.Id, JsonSerializer.SerializeToElement(result, AgentHostJsonContext.Default.ReadScrollbackResult));
        }

        /// <summary>
        /// Same text semantics as <see cref="BufferSnapshot.Capture"/>: skip wide
        /// continuations, prefer the row's extended text (emoji, multi-codepoint
        /// graphemes), NUL → space, trim right.
        /// </summary>
        private static string RenderScrollbackRow(ReadOnlySpan<TerminalCell> cells, NovaTerminal.VT.Storage.SmallMap<string>? extendedText)
        {
            var sb = new StringBuilder(cells.Length);
            for (var col = 0; col < cells.Length; col++)
            {
                ref readonly var cell = ref cells[col];
                if (cell.IsWideContinuation) continue;

                if (extendedText != null && extendedText.TryGet(col, out var ext) && ext != null)
                {
                    sb.Append(ext);
                }
                else
                {
                    sb.Append(cell.Character == '\0' ? ' ' : cell.Character);
                }
            }
            return sb.ToString().TrimEnd();
        }

        private static T? DeserializeParams<T>(AgentHostRequest request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
            where T : class
        {
            return request.Params is { } element ? element.Deserialize(typeInfo) : null;
        }

        private static AgentHostResponse Ok(long id, JsonElement result) => new()
        {
            Version = AgentHostProtocol.Version,
            Id = id,
            Result = result,
        };

        private static AgentHostResponse Error(long id, string code, string message) => new()
        {
            Version = AgentHostProtocol.Version,
            Id = id,
            Error = new AgentHostError { Code = code, Message = message },
        };
    }
}

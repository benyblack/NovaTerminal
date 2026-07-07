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
        private readonly object _gate = new();

        private CancellationTokenSource? _cts;
        private Task? _acceptLoop;
        private Socket? _unixListener;
        private string? _unixSocketPath;
        private string? _discoveryFilePath;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Stream, byte> _activeClients = new();

        public AgentHostService(
            AgentSessionRegistry registry,
            string? endpointOverride = null,
            string? discoveryDirectoryOverride = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _endpointOverride = endpointOverride;
            _discoveryDirectoryOverride = discoveryDirectoryOverride;
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

                var discoveryDir = _discoveryDirectoryOverride ?? NovaTerminal.Shell.AppPaths.RootDirectory;
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

                    var response = HandleRequestLine(line);
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

        internal AgentHostResponse HandleRequestLine(string line)
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
                    _ => Error(request.Id, AgentHostProtocol.ErrorCodes.UnknownMethod, $"Unknown method '{request.Method}'."),
                };
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

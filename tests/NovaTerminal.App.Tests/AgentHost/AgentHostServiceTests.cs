using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NovaTerminal.AgentHost;
using NovaTerminal.AgentHost.Contracts;
using NovaTerminal.Replay;
using NovaTerminal.VT;

namespace NovaTerminal.AppTests.AgentHost;

/// <summary>
/// Tests for the agent-host observe endpoint (PR3 of milestone A1,
/// docs/plans/2026-07-07-agent-host-a1-observe-design.md). Request handling
/// is covered in-process via HandleRequestLine; the transport (named pipe on
/// Windows, unix socket elsewhere) is covered by real connect/round-trip
/// tests using a private endpoint name so parallel test runs don't collide.
/// </summary>
public class AgentHostServiceTests : IDisposable
{
    private readonly string _tempDir;

    public AgentHostServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nova-agenthost-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string NewEndpointName()
    {
        return OperatingSystem.IsWindows()
            ? "novaterminal-agent-test-" + Guid.NewGuid().ToString("N")
            : Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8] + ".sock");
    }

    private AgentHostService NewService(AgentSessionRegistry registry)
        => new(registry, NewEndpointName(), _tempDir);

    private static AgentSessionRegistration Register(AgentSessionRegistry registry, TerminalBuffer buffer, Guid? paneId = null)
    {
        var registration = new AgentSessionRegistration(
            paneId ?? Guid.NewGuid(), buffer, "title", "Profile", "local", isActive: true);
        Assert.True(registry.Register(registration));
        return registration;
    }

    private static string RequestLine(string method, long id = 1, object? paramsObj = null, int version = AgentHostProtocol.Version)
    {
        var paramsJson = paramsObj switch
        {
            null => "null",
            ReadScreenParams p => JsonSerializer.Serialize(p, AgentHostJsonContext.Default.ReadScreenParams),
            ReadScrollbackParams p => JsonSerializer.Serialize(p, AgentHostJsonContext.Default.ReadScrollbackParams),
            _ => throw new InvalidOperationException("unexpected params type"),
        };
        return $"{{\"v\":{version},\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}";
    }

    /// <summary>Sync convenience over the async handler for non-long-poll requests.</summary>
    private static AgentHostResponse Handle(AgentHostService service, string line)
        => service.HandleRequestLineAsync(line, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    private static T ResultOf<T>(AgentHostResponse response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
        var value = response.Result!.Value.Deserialize(typeInfo);
        Assert.NotNull(value);
        return value!;
    }

    // ── Request handling (in-process) ────────────────────────────────────────

    [Fact]
    public void Version_mismatch_is_rejected_with_stable_error_code()
    {
        using var service = NewService(new AgentSessionRegistry());
        var response = Handle(service,RequestLine(AgentHostProtocol.Methods.ListSessions, version: 99));
        Assert.Equal(AgentHostProtocol.ErrorCodes.VersionMismatch, response.Error?.Code);
    }

    [Fact]
    public void Malformed_frame_is_rejected_not_thrown()
    {
        using var service = NewService(new AgentSessionRegistry());
        var response = Handle(service,"this is not json");
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, response.Error?.Code);
    }

    [Fact]
    public void Unknown_method_is_rejected_with_stable_error_code()
    {
        using var service = NewService(new AgentSessionRegistry());
        var response = Handle(service,RequestLine("thisMethodDoesNotExist"));
        Assert.Equal(AgentHostProtocol.ErrorCodes.UnknownMethod, response.Error?.Code);
    }

    [Fact]
    public void Missing_params_is_a_malformed_request_not_a_missing_session()
    {
        using var service = NewService(new AgentSessionRegistry());
        var readScreen = Handle(service,RequestLine(AgentHostProtocol.Methods.ReadScreen));
        var readScrollback = Handle(service,RequestLine(AgentHostProtocol.Methods.ReadScrollback));
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, readScreen.Error?.Code);
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, readScrollback.Error?.Code);
    }

    [Fact]
    public void ReadScrollback_preserves_extended_text_graphemes()
    {
        // Emoji / multi-codepoint graphemes are stored as extended text; they
        // must survive scrolling off screen (parity with readScreen).
        var registry = new AgentSessionRegistry();
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);
        parser.Process("ok \U0001F44D done\r\n");
        for (var i = 0; i < 6; i++)
        {
            parser.Process($"filler-{i}\r\n");
        }
        var registration = Register(registry, buffer);
        using var service = NewService(registry);

        var response = Handle(service,RequestLine(
            AgentHostProtocol.Methods.ReadScrollback,
            paramsObj: new ReadScrollbackParams { PaneId = registration.PaneId, StartLine = 0, MaxLines = 10 }));
        var result = ResultOf(response, AgentHostJsonContext.Default.ReadScrollbackResult);

        Assert.Contains(result.Lines, line => line.Contains("ok \U0001F44D done", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadScreen_for_unknown_pane_reports_session_not_found()
    {
        using var service = NewService(new AgentSessionRegistry());
        var response = Handle(service,RequestLine(
            AgentHostProtocol.Methods.ReadScreen,
            paramsObj: new ReadScreenParams { PaneId = Guid.NewGuid() }));
        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, response.Error?.Code);
    }

    [Fact]
    public void ReadScreen_matches_BufferSnapshot_capture_exactly()
    {
        // The A1 parity invariant: what an agent reads over the wire is the
        // deterministic snapshot, byte for byte.
        var registry = new AgentSessionRegistry();
        var buffer = new TerminalBuffer(40, 10);
        var parser = new AnsiParser(buffer);
        parser.Process("hello \x1b[1mworld\x1b[0m\r\nsecond line\r\nwide: 你好");
        var registration = Register(registry, buffer);
        using var service = NewService(registry);

        var response = Handle(service,RequestLine(
            AgentHostProtocol.Methods.ReadScreen,
            paramsObj: new ReadScreenParams { PaneId = registration.PaneId, IncludeAttributes = true }));
        var dto = ResultOf(response, AgentHostJsonContext.Default.ScreenSnapshotDto);

        var expected = BufferSnapshot.Capture(buffer, includeAttributes: true);
        Assert.Equal(expected.Lines, dto.Lines);
        Assert.Equal(expected.AttributeLines, dto.AttributeLines);
        Assert.Equal(expected.CursorRow, dto.CursorRow);
        Assert.Equal(expected.CursorCol, dto.CursorCol);
        Assert.Equal(10, dto.Rows);
        Assert.Equal(40, dto.Cols);
        Assert.Equal("hello world", dto.Lines[0]);
    }

    [Fact]
    public void ReadScrollback_returns_ranged_lines_with_totals()
    {
        var registry = new AgentSessionRegistry();
        var buffer = new TerminalBuffer(20, 4);
        var parser = new AnsiParser(buffer);
        for (var i = 0; i < 12; i++)
        {
            parser.Process($"line-{i}\r\n");
        }
        var registration = Register(registry, buffer);
        using var service = NewService(registry);

        var response = Handle(service,RequestLine(
            AgentHostProtocol.Methods.ReadScrollback,
            paramsObj: new ReadScrollbackParams { PaneId = registration.PaneId, StartLine = 1, MaxLines = 3 }));
        var result = ResultOf(response, AgentHostJsonContext.Default.ReadScrollbackResult);

        Assert.Equal(1, result.StartLine);
        Assert.Equal(3, result.Lines.Length);
        Assert.Equal(buffer.Scrollback.Count, result.TotalLines);
        Assert.Equal("line-1", result.Lines[0]);
        Assert.Equal("line-3", result.Lines[2]);
    }

    // ── Endpoint lifecycle & transport ───────────────────────────────────────

    [Fact]
    public void Disabled_service_creates_no_endpoint_and_no_discovery_file()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewService(registry);

        service.Apply(false);

        Assert.False(service.IsRunning);
        Assert.False(File.Exists(Path.Combine(_tempDir, AgentHostProtocol.DiscoveryFileName)));
    }

    [Fact]
    public void Stop_retires_the_discovery_descriptor()
    {
        using var service = NewService(new AgentSessionRegistry());
        service.Apply(true);
        var discoveryPath = Path.Combine(_tempDir, AgentHostProtocol.DiscoveryFileName);
        Assert.True(File.Exists(discoveryPath));
        Assert.True(new FileInfo(discoveryPath).Length > 0);

        service.Apply(false);

        // The descriptor is truncated under an exclusive handle rather than
        // deleted (delete-after-close would race a concurrent takeover). An
        // empty file is a stale descriptor: no live endpoint is advertised.
        Assert.False(service.IsRunning);
        Assert.True(!File.Exists(discoveryPath) || new FileInfo(discoveryPath).Length == 0);
    }

    [Fact]
    public void Stop_leaves_a_foreign_live_descriptor_untouched()
    {
        using var service = NewService(new AgentSessionRegistry());
        service.Apply(true);
        var discoveryPath = Path.Combine(_tempDir, AgentHostProtocol.DiscoveryFileName);

        // Simulate another instance taking over the endpoint between our start
        // and our stop: the descriptor on disk is no longer ours.
        var foreign = new EndpointDescriptor
        {
            Version = AgentHostProtocol.Version,
            Endpoint = "someone-elses-endpoint",
            Pid = Environment.ProcessId + 1,
        };
        File.WriteAllText(discoveryPath, JsonSerializer.Serialize(foreign, AgentHostJsonContext.Default.EndpointDescriptor));

        service.Apply(false);

        var survivor = JsonSerializer.Deserialize(
            File.ReadAllText(discoveryPath), AgentHostJsonContext.Default.EndpointDescriptor);
        Assert.NotNull(survivor);
        Assert.Equal("someone-elses-endpoint", survivor!.Endpoint);
    }

    [Fact]
    public async Task ListSessions_round_trips_over_the_real_transport()
    {
        var registry = new AgentSessionRegistry();
        var buffer = new TerminalBuffer(80, 24);
        var registration = Register(registry, buffer);
        using var service = NewService(registry);
        service.Start();
        Assert.True(service.IsRunning);

        // Discovery file advertises the endpoint, exactly as the MCP client will read it.
        var descriptor = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(_tempDir, AgentHostProtocol.DiscoveryFileName)),
            AgentHostJsonContext.Default.EndpointDescriptor);
        Assert.NotNull(descriptor);
        Assert.Equal(AgentHostProtocol.Version, descriptor!.Version);
        Assert.Equal(Environment.ProcessId, descriptor.Pid);

        await using var stream = await ConnectAsync(descriptor.Endpoint);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync(RequestLine(AgentHostProtocol.Methods.ListSessions, id: 42));
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(line);

        var response = JsonSerializer.Deserialize(line!, AgentHostJsonContext.Default.AgentHostResponse);
        Assert.NotNull(response);
        Assert.Equal(42, response!.Id);
        var sessions = ResultOf(response, AgentHostJsonContext.Default.ListSessionsResult).Sessions;
        var session = Assert.Single(sessions);
        Assert.Equal(registration.PaneId, session.PaneId);
        Assert.Equal(24, session.Rows);
        Assert.Equal(80, session.Cols);
    }

    [Fact]
    public async Task Disabling_the_service_closes_already_connected_clients()
    {
        // Security invariant: turning Agent Access off revokes access for
        // existing connections, not just future ones.
        var registry = new AgentSessionRegistry();
        Register(registry, new TerminalBuffer(80, 24));
        using var service = NewService(registry);
        service.Start();

        var descriptor = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(_tempDir, AgentHostProtocol.DiscoveryFileName)),
            AgentHostJsonContext.Default.EndpointDescriptor)!;
        await using var stream = await ConnectAsync(descriptor.Endpoint);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
        // Not await-using: disposal flushes, and flushing into the deliberately
        // broken pipe after Stop() would throw during teardown.
        var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        try
        {
            // Prove the connection works, so the assertion below is meaningful.
            await writer.WriteLineAsync(RequestLine(AgentHostProtocol.Methods.ListSessions));
            Assert.NotNull(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));

            service.Stop();

            // The server must have force-closed the stream: the next read observes
            // end-of-stream or a broken pipe/socket, never fresh data.
            try
            {
                var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Null(line);
            }
            catch (IOException)
            {
                // Also acceptable: the transport surfaces the closed connection as an error.
            }
        }
        finally
        {
            try { await writer.DisposeAsync(); } catch (IOException) { /* broken pipe is the expected end state */ }
        }
    }

    private static async Task<Stream> ConnectAsync(string endpoint)
    {
        if (OperatingSystem.IsWindows())
        {
            var client = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(10_000);
            return client;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint));
        return new NetworkStream(socket, ownsSocket: true);
    }
}

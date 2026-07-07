using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using NovaTerminal.AgentHost;
using NovaTerminal.AgentHost.Contracts;
using NovaTerminal.Replay;
using NovaTerminal.VT;

namespace NovaTerminal.AppTests.AgentHost;

/// <summary>
/// Tests for the A4 <c>exportReplay</c> protocol surface and its flight-recorder
/// lifecycle wiring (docs/plans/2026-07-07-agent-host-a4-replay-design.md, PR2).
/// Two independent gates: the observe endpoint must be running (ring exists) and
/// <see cref="AgentHostService.ReplayExportEnabled"/> must be on. Exports must
/// never contain input events.
/// </summary>
public class AgentHostReplayProtocolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _exportDir;

    public AgentHostReplayProtocolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nova-agentreplay-tests-" + Guid.NewGuid().ToString("N"));
        _exportDir = Path.Combine(_tempDir, "agent-exports");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AgentHostService NewService(AgentSessionRegistry registry)
    {
        var endpoint = OperatingSystem.IsWindows()
            ? "novaterminal-agent-test-" + Guid.NewGuid().ToString("N")
            : Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8] + ".sock");
        return new AgentHostService(registry, endpoint, _tempDir, _exportDir);
    }

    private static AgentSessionRegistration Register(AgentSessionRegistry registry, Guid? paneId = null)
    {
        var registration = new AgentSessionRegistration(
            paneId ?? Guid.NewGuid(), new TerminalBuffer(80, 24), "title", "Profile", "local", isActive: true);
        Assert.True(registry.Register(registration));
        return registration;
    }

    private static string ExportRequestLine(Guid paneId, long id = 1)
    {
        var paramsJson = JsonSerializer.Serialize(
            new ExportReplayParams { PaneId = paneId }, AgentHostJsonContext.Default.ExportReplayParams);
        return $"{{\"v\":{AgentHostProtocol.Version},\"id\":{id},\"method\":\"{AgentHostProtocol.Methods.ExportReplay}\",\"params\":{paramsJson}}}";
    }

    private static AgentHostResponse Handle(AgentHostService service, string line)
        => service.HandleRequestLineAsync(line, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    // ── Stub session with a real ring ────────────────────────────────────────

    private sealed class FlightStubSession : NovaTerminal.Pty.ITerminalSession
    {
        private FlightRecordingBuffer? _ring;

        public long? LastEnabledMaxBytes { get; private set; }

        public void FeedOutput(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            _ring?.RecordChunk(bytes, bytes.Length);
        }

        public void FeedResize(int cols, int rows) => _ring?.RecordResize(cols, rows);

        public bool IsFlightRecording => _ring != null;

        public void EnableFlightRecording(long maxTotalBytes)
        {
            if (_ring != null) return;
            LastEnabledMaxBytes = maxTotalBytes;
            _ring = new FlightRecordingBuffer(maxTotalBytes, 80, 24, clock: static () => 0);
        }

        public void DisableFlightRecording() => _ring = null;

        public bool TryExportFlightRecording(string filePath, out FlightExportInfo info)
        {
            var ring = _ring;
            if (ring == null)
            {
                info = default;
                return false;
            }
            info = ring.ExportTo(filePath, "stub-shell");
            return true;
        }

        // Inert ITerminalSession surface
        public Guid Id { get; } = Guid.NewGuid();
        public string ShellCommand => "stub-shell";
        public string? ShellArguments => null;
        public bool IsProcessRunning => true;
        public bool HasActiveChildProcesses => false;
        public int? ExitCode => null;
        public bool IsRecording => false;
        public event Action<string>? OnOutputReceived { add { } remove { } }
        public event Action<int>? OnExit { add { } remove { } }
        public void SendInput(string input) { }
        public void Resize(int cols, int rows) { }
        public void StartRecording(string filePath) { }
        public void StopRecording() { }
        public void Dispose() { }
    }

    // ── Lifecycle: the ring exists exactly while the endpoint runs ──────────

    [Fact]
    public void Start_enables_flight_recording_on_already_registered_sessions_and_stop_disables_it()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        var session = new FlightStubSession();
        registration.SetLifecycle(session);
        Assert.False(session.IsFlightRecording); // off-is-off before Start

        using var service = NewService(registry);
        service.Start();
        Assert.True(session.IsFlightRecording);
        Assert.Equal(AgentHostProtocol.FlightRecorderMaxBytesPerSession, session.LastEnabledMaxBytes);

        service.Stop();
        Assert.False(session.IsFlightRecording);
    }

    [Fact]
    public void Sessions_registered_while_running_get_flight_recording()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewService(registry);
        service.Start();

        var registration = Register(registry);
        var session = new FlightStubSession();
        registration.SetLifecycle(session);

        Assert.True(session.IsFlightRecording);
    }

    [Fact]
    public void Swapping_or_clearing_the_published_session_disables_the_old_ring()
    {
        // A session this registration no longer owns must not keep retaining
        // output until its eventual disposal (observe-lifecycle invariant).
        var registration = new AgentSessionRegistration(
            Guid.NewGuid(), new TerminalBuffer(80, 24), "t", "P", "local", isActive: true);
        registration.EnableFlightRecording(1024);

        var first = new FlightStubSession();
        registration.SetLifecycle(first);
        Assert.True(first.IsFlightRecording);

        var second = new FlightStubSession();
        registration.SetLifecycle(second);
        Assert.False(first.IsFlightRecording);  // swapped out → ring dropped
        Assert.True(second.IsFlightRecording);

        registration.SetLifecycle(null);
        Assert.False(second.IsFlightRecording); // cleared → ring dropped
    }

    [Fact]
    public void Repeated_exports_for_the_same_pane_produce_distinct_files()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        var session = new FlightStubSession();
        registration.SetLifecycle(session);
        registration.EnableFlightRecording(AgentHostProtocol.FlightRecorderMaxBytesPerSession);
        session.FeedOutput("some output");

        using var service = NewService(registry);
        service.ReplayExportEnabled = true;

        // Two exports within the same second (the timestamp component has
        // one-second resolution) must not overwrite each other.
        var first = Handle(service, ExportRequestLine(registration.PaneId, id: 1));
        var second = Handle(service, ExportRequestLine(registration.PaneId, id: 2));

        Assert.Null(first.Error);
        Assert.Null(second.Error);
        var firstResult = first.Result!.Value.Deserialize(AgentHostJsonContext.Default.ExportReplayResult)!;
        var secondResult = second.Result!.Value.Deserialize(AgentHostJsonContext.Default.ExportReplayResult)!;
        Assert.NotEqual(firstResult.FilePath, secondResult.FilePath);
        Assert.True(File.Exists(firstResult.FilePath));
        Assert.True(File.Exists(secondResult.FilePath));
    }

    [Fact]
    public void Session_published_after_enable_inherits_the_pending_recording_state()
    {
        // Registration happens before the pane spawns its PTY session; a session
        // published later (or swapped on reconnect) must inherit the endpoint's
        // decision without any further service involvement.
        var registration = new AgentSessionRegistration(
            Guid.NewGuid(), new TerminalBuffer(80, 24), "t", "P", "local", isActive: true);
        registration.EnableFlightRecording(1024);

        var late = new FlightStubSession();
        registration.SetLifecycle(late);
        Assert.True(late.IsFlightRecording);

        var swapped = new FlightStubSession();
        registration.SetLifecycle(swapped);
        Assert.True(swapped.IsFlightRecording);

        registration.DisableFlightRecording();
        Assert.False(swapped.IsFlightRecording);

        var afterDisable = new FlightStubSession();
        registration.SetLifecycle(afterDisable);
        Assert.False(afterDisable.IsFlightRecording);
    }

    // ── exportReplay handler ─────────────────────────────────────────────────

    [Fact]
    public void Export_with_both_gates_on_writes_a_v2_file_with_no_input_events()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        var session = new FlightStubSession();
        registration.SetLifecycle(session);
        registration.EnableFlightRecording(AgentHostProtocol.FlightRecorderMaxBytesPerSession);

        session.FeedOutput("hello from the session\r\n");
        session.FeedResize(100, 30);
        session.FeedOutput("after resize");

        using var service = NewService(registry);
        service.ReplayExportEnabled = true;

        var response = Handle(service, ExportRequestLine(registration.PaneId));

        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
        var result = response.Result!.Value.Deserialize(AgentHostJsonContext.Default.ExportReplayResult);
        Assert.NotNull(result);
        Assert.Equal(3, result!.EventCount);
        Assert.False(result.TruncatedAtStart);
        Assert.True(File.Exists(result.FilePath));
        Assert.StartsWith(Path.GetFullPath(_exportDir), Path.GetFullPath(result.FilePath), StringComparison.Ordinal);
        Assert.StartsWith("nova_rec_", Path.GetFileName(result.FilePath), StringComparison.Ordinal);
        Assert.EndsWith(".rec", result.FilePath, StringComparison.Ordinal);

        string[] lines = File.ReadAllLines(result.FilePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        var header = JsonSerializer.Deserialize(lines[0], ReplayJsonContext.Default.ReplayHeader);
        Assert.NotNull(header);
        Assert.Equal("novarec", header!.Type);
        Assert.Equal(2, header.Version);

        // Privacy invariant: output + resize only, never input.
        var eventTypes = lines.Skip(1)
            .Select(l => JsonSerializer.Deserialize(l, ReplayJsonContext.Default.ReplayEvent)!.Type)
            .ToArray();
        Assert.Equal(3, eventTypes.Length);
        Assert.All(eventTypes, t => Assert.True(t == "data" || t == "resize", $"unexpected event type '{t}'"));
        Assert.DoesNotContain("input", eventTypes);
    }

    [Fact]
    public void Export_without_the_export_setting_fails_with_exportDisabled_and_writes_nothing()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        var session = new FlightStubSession();
        registration.SetLifecycle(session);
        registration.EnableFlightRecording(AgentHostProtocol.FlightRecorderMaxBytesPerSession);
        session.FeedOutput("secret output");

        using var service = NewService(registry);
        service.ReplayExportEnabled = false; // observe on, export sub-gate off

        var response = Handle(service, ExportRequestLine(registration.PaneId));

        Assert.Equal(AgentHostProtocol.ErrorCodes.ExportDisabled, response.Error?.Code);
        Assert.False(Directory.Exists(_exportDir) && Directory.EnumerateFiles(_exportDir).Any());
    }

    [Fact]
    public void Export_for_unknown_pane_reports_session_not_found()
    {
        using var service = NewService(new AgentSessionRegistry());
        service.ReplayExportEnabled = true;

        var response = Handle(service, ExportRequestLine(Guid.NewGuid()));

        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, response.Error?.Code);
    }

    [Fact]
    public void Export_without_params_is_a_malformed_request()
    {
        using var service = NewService(new AgentSessionRegistry());
        service.ReplayExportEnabled = true;

        var line = $"{{\"v\":{AgentHostProtocol.Version},\"id\":7,\"method\":\"{AgentHostProtocol.Methods.ExportReplay}\",\"params\":null}}";
        var response = Handle(service, line);

        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, response.Error?.Code);
    }

    [Fact]
    public void Export_for_session_without_a_published_pty_reports_exportUnavailable()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry); // no SetLifecycle: pane hasn't spawned yet

        using var service = NewService(registry);
        service.ReplayExportEnabled = true;

        var response = Handle(service, ExportRequestLine(registration.PaneId));

        Assert.Equal(AgentHostProtocol.ErrorCodes.ExportUnavailable, response.Error?.Code);
    }
}

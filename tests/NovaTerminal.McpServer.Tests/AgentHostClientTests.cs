using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using NovaTerminal.AgentHost.Contracts;
using NovaTerminal.McpServer;
using NovaTerminal.McpServer.Tools;

namespace NovaTerminal.McpServer.Tests;

/// <summary>
/// Client-side tests for the agent-host observe channel (milestone A1, PR4).
/// The endpoint here is a minimal in-test fake speaking the contracts frame
/// protocol — the real server lives in the app and is tested there; these
/// tests pin the client's discovery, unavailability, and round-trip behavior.
/// </summary>
public class AgentHostClientTests : IDisposable
{

    private readonly string _tempDir;

    public AgentHostClientTests()
    {
        // Short name on purpose: the unix-domain socket built under this dir must keep the
        // full path under macOS's ~104-char sun_path limit (a long temp dir overflowed it,
        // failing the live-endpoint tests only on the macOS release runner).
        _tempDir = Path.Combine(Path.GetTempPath(), "nvac-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string DiscoveryPath => Path.Combine(_tempDir, AgentHostProtocol.DiscoveryFileName);

    private void WriteDescriptor(string endpoint, int? pid = null)
    {
        var descriptor = new EndpointDescriptor
        {
            Version = AgentHostProtocol.Version,
            Endpoint = endpoint,
            Pid = pid ?? Environment.ProcessId,
        };
        File.WriteAllText(DiscoveryPath, JsonSerializer.Serialize(descriptor, AgentHostJsonContext.Default.EndpointDescriptor));
    }

    [Fact]
    public async Task Missing_discovery_file_reports_unavailable_with_guidance()
    {
        var client = new AgentHostClient(DiscoveryPath);
        var outcome = await client.CallAsync(AgentHostProtocol.Methods.ListSessions, null, TestContext.Current.CancellationToken);

        Assert.False(outcome.Available);
        Assert.Equal(AgentHostClient.UnavailableMessage, outcome.UnavailableReason);
    }

    [Fact]
    public async Task Truncated_descriptor_is_treated_as_retired()
    {
        // The app truncates (never deletes) the descriptor on stop.
        File.WriteAllText(DiscoveryPath, string.Empty);
        var client = new AgentHostClient(DiscoveryPath);

        var outcome = await client.CallAsync(AgentHostProtocol.Methods.ListSessions, null, TestContext.Current.CancellationToken);

        Assert.False(outcome.Available);
    }

    [Fact]
    public async Task Dead_pid_descriptor_is_treated_as_stale()
    {
        // Pid 4_000_000 is above the Windows/Linux practical pid ranges used in
        // CI; GetProcessById throws → stale.
        WriteDescriptor("nonexistent-endpoint", pid: 4_000_000);
        var client = new AgentHostClient(DiscoveryPath);

        var outcome = await client.CallAsync(AgentHostProtocol.Methods.ListSessions, null, TestContext.Current.CancellationToken);

        Assert.False(outcome.Available);
    }

    [Fact]
    public async Task Round_trips_a_list_sessions_call_against_a_live_endpoint()
    {
        var endpoint = OperatingSystem.IsWindows()
            ? "novaterminal-agent-client-test-" + Guid.NewGuid().ToString("N")
            : Path.Combine(_tempDir, "t.sock");

        var sessions = new ListSessionsResult
        {
            Sessions = new[]
            {
                new SessionInfo
                {
                    PaneId = Guid.NewGuid(),
                    Title = "vim",
                    ProfileName = "Bash",
                    Kind = "local",
                    Rows = 24,
                    Cols = 80,
                    IsActive = true,
                },
            },
        };

        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = RunFakeEndpointOnceAsync(endpoint, sessions, serverCts.Token);
        WriteDescriptor(endpoint);

        var client = new AgentHostClient(DiscoveryPath);
        var outcome = await client.CallAsync(AgentHostProtocol.Methods.ListSessions, null, TestContext.Current.CancellationToken);

        Assert.True(outcome.Available, outcome.UnavailableReason);
        Assert.Null(outcome.Response!.Error);
        var roundTripped = outcome.Response.Result!.Value.Deserialize(AgentHostJsonContext.Default.ListSessionsResult);
        Assert.Equal("vim", Assert.Single(roundTripped!.Sessions).Title);

        // TestContext token per xUnit1051, so a cancelled test run stops waiting promptly
        // instead of holding the full 10s ceiling.
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_reporting_unavailable()
    {
        WriteDescriptor("some-endpoint-nobody-listens-on");
        var client = new AgentHostClient(DiscoveryPath);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CallAsync(AgentHostProtocol.Methods.ListSessions, null, cts.Token));
    }

    [Fact]
    public async Task Malformed_server_response_is_a_protocol_error_not_unavailable()
    {
        var endpoint = OperatingSystem.IsWindows()
            ? "novaterminal-agent-client-test-" + Guid.NewGuid().ToString("N")
            : Path.Combine(_tempDir, "m.sock");

        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = RunRawReplyEndpointOnceAsync(endpoint, "this is not a frame", serverCts.Token);
        WriteDescriptor(endpoint);

        var client = new AgentHostClient(DiscoveryPath);
        var outcome = await client.CallAsync(AgentHostProtocol.Methods.ListSessions, null, TestContext.Current.CancellationToken);

        Assert.False(outcome.Available);
        Assert.Equal(AgentHostClient.ProtocolErrorMessage, outcome.UnavailableReason);
        // TestContext token per xUnit1051, so a cancelled test run stops waiting promptly
        // instead of holding the full 10s ceiling.
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    /// <summary>Accepts one connection, reads one line, replies with a raw string, then exits.</summary>
    private static async Task RunRawReplyEndpointOnceAsync(string endpoint, string rawReply, CancellationToken token)
    {
        await using var stream = await AcceptOneAsync(endpoint, token);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        _ = await reader.ReadLineAsync(token);
        await writer.WriteLineAsync(rawReply.AsMemory(), token);
    }

    private static async Task<Stream> AcceptOneAsync(string endpoint, CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeServerStream(endpoint, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync(token);
            return pipe;
        }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(endpoint));
        listener.Listen(1);
        var socket = await listener.AcceptAsync(token);
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>Serves exactly one connection and one request, then exits.</summary>
    private static async Task RunFakeEndpointOnceAsync(string endpoint, ListSessionsResult reply, CancellationToken token)
    {
        Stream stream;
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeServerStream(endpoint, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync(token);
            stream = pipe;
        }
        else
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(endpoint));
            listener.Listen(1);
            var socket = await listener.AcceptAsync(token);
            stream = new NetworkStream(socket, ownsSocket: true);
        }

        await using (stream)
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            var line = await reader.ReadLineAsync(token);
            var request = JsonSerializer.Deserialize(line!, AgentHostJsonContext.Default.AgentHostRequest)!;
            var response = new AgentHostResponse
            {
                Version = AgentHostProtocol.Version,
                Id = request.Id,
                Result = JsonSerializer.SerializeToElement(reply, AgentHostJsonContext.Default.ListSessionsResult),
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, AgentHostJsonContext.Default.AgentHostResponse).AsMemory(), token);
        }
    }
}

/// <summary>Formatter tests: the shapes agents actually read.</summary>
public class SessionToolsFormattingTests
{
    // Hoisted out of the object initialisers below to satisfy CA1861: a constant array
    // argument is re-allocated on every call. This project is now built with
    // TreatWarningsAsErrors (#108), so these analyzers are enforced rather than advisory.
    private static readonly string[] HelloWorldLines = ["hello", "world"];
    private static readonly string[] AbLines = ["a", "b"];

    [Fact]
    public async Task ReadScrollback_rejects_non_positive_maxLines_before_any_ipc()
    {
        // maxLines <= 0 would produce an empty page whose paging hint repeats
        // the same startLine — an agent following the hint would loop forever.
        // The guard runs before the IPC call: a client with no discovery file
        // would otherwise return the unavailable message instead.
        var client = new AgentHostClient(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nothing.json"));

        var zero = await SessionTools.ReadScrollback(client, Guid.NewGuid().ToString(), startLine: 0, maxLines: 0, TestContext.Current.CancellationToken);
        var negative = await SessionTools.ReadScrollback(client, Guid.NewGuid().ToString(), startLine: 0, maxLines: -5, TestContext.Current.CancellationToken);
        var negativeStart = await SessionTools.ReadScrollback(client, Guid.NewGuid().ToString(), startLine: -1, maxLines: 10, TestContext.Current.CancellationToken);

        Assert.Equal("Error: maxLines must be greater than 0.", zero);
        Assert.Equal("Error: maxLines must be greater than 0.", negative);
        Assert.StartsWith("Error: startLine must be 0 or greater", negativeStart, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSessionList_renders_a_row_per_session_and_handles_null_tab()
    {
        var paneId = Guid.NewGuid();
        var text = SessionTools.FormatSessionList(new[]
        {
            new SessionInfo
            {
                PaneId = paneId, Title = "htop", ProfileName = "Zsh", Kind = "ssh",
                Rows = 40, Cols = 120, IsActive = false, TabId = null,
            },
        });

        Assert.Contains(paneId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("| htop | Zsh | ssh | 120x40 | no | - |", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatScreen_numbers_lines_and_reports_cursor()
    {
        var text = SessionTools.FormatScreen(new ScreenSnapshotDto
        {
            Lines = HelloWorldLines,
            CursorRow = 1,
            CursorCol = 5,
            CursorVisible = true,
            Rows = 24,
            Cols = 80,
        });

        Assert.Contains("cursor at row 1, col 5", text, StringComparison.Ordinal);
        Assert.Contains("  0| hello", text, StringComparison.Ordinal);
        Assert.Contains("  1| world", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSessionList_includes_the_status_column()
    {
        var text = SessionTools.FormatSessionList(new[]
        {
            new SessionInfo
            {
                PaneId = Guid.NewGuid(), Title = "build", ProfileName = "Bash", Kind = "local",
                Rows = 24, Cols = 80, IsActive = true,
                Status = AgentHostProtocol.StatusKinds.Running,
                Confidence = AgentHostProtocol.StatusConfidences.Precise,
            },
        });

        Assert.Contains("| running (precise) |", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatStatus_renders_command_stall_and_thresholds()
    {
        var text = SessionTools.FormatStatus(new SessionStatusDto
        {
            PaneId = Guid.NewGuid(),
            Status = AgentHostProtocol.StatusKinds.Running,
            Confidence = AgentHostProtocol.StatusConfidences.Precise,
            CurrentCommand = "cargo build",
            StatusSinceMs = 1_800_000_000_000,
            LastOutputAtMs = 1_800_000_030_000,
            IsStalled = true,
            StallThresholdSeconds = 30,
            IdleThresholdSeconds = 60,
        });

        Assert.Contains("running (precise confidence)", text, StringComparison.Ordinal);
        Assert.Contains("command: cargo build", text, StringComparison.Ordinal);
        Assert.Contains("STALLED", text, StringComparison.Ordinal);
        Assert.Contains("idle after 60s", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatEvents_teaches_the_cursor_and_reports_eviction_gaps()
    {
        var paneId = Guid.NewGuid();
        var result = new WaitForEventsResult
        {
            Events = new[]
            {
                new AgentEventDto
                {
                    Seq = 12, TimestampMs = 1_800_000_000_000, PaneId = paneId,
                    Type = AgentHostProtocol.EventTypes.CommandFinished,
                    Status = AgentHostProtocol.StatusKinds.AwaitingInput,
                    ExitCode = 0, DurationMs = 4200,
                },
            },
            NextSeq = 12,
            OldestSeq = 10,
        };

        // Cursor 3 predates oldestSeq 10 → events 4–9 were evicted.
        var text = SessionTools.FormatEvents(result, sinceSeq: 3);
        Assert.Contains("sinceSeq=12", text, StringComparison.Ordinal);
        Assert.Contains("Warning: events 4–9 were evicted", text, StringComparison.Ordinal);
        Assert.Contains("commandFinished", text, StringComparison.Ordinal);
        Assert.Contains("exit 0, 4200 ms", text, StringComparison.Ordinal);

        // A current cursor produces no warning.
        var clean = SessionTools.FormatEvents(result, sinceSeq: 11);
        Assert.DoesNotContain("Warning", clean, StringComparison.Ordinal);

        // Empty result teaches the retry cursor.
        var empty = SessionTools.FormatEvents(new WaitForEventsResult { Events = Array.Empty<AgentEventDto>(), NextSeq = 12, OldestSeq = 10 }, sinceSeq: 12);
        Assert.Contains("No events within the wait window. Call again with sinceSeq=12.", empty, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatCapture_reports_path_geometry_and_what_the_image_excludes()
    {
        var text = SessionTools.FormatCapture(new CaptureScreenResult
        {
            FilePath = @"C:\rec\agent-exports\nova_screen_20260731_120000_abc123.png",
            Width = 640,
            Height = 384,
            Cols = 80,
            Rows = 24,
            ByteCount = 12_345,
            Downscaled = false,
        });

        Assert.Contains("nova_screen_20260731_120000_abc123.png", text, StringComparison.Ordinal);
        Assert.Contains("640x384 px for a 80x24 grid", text, StringComparison.Ordinal);
        Assert.Contains("no window chrome", text, StringComparison.Ordinal);
        Assert.DoesNotContain("downscaled", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("omitted", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatCapture_flags_downscaling_and_a_dropped_inline_image()
    {
        var text = SessionTools.FormatCapture(new CaptureScreenResult
        {
            FilePath = "/tmp/shot.png",
            Width = 160,
            Height = 96,
            Cols = 80,
            Rows = 24,
            ByteCount = 4_000_000,
            Downscaled = true,
            InlineOmitted = true,
        });

        Assert.Contains("downscaled from the pane's native size", text, StringComparison.Ordinal);
        Assert.Contains("inline image was omitted", text, StringComparison.Ordinal);
        Assert.Contains("smaller maxWidth", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureScreen_rejects_a_negative_maxWidth_before_any_ipc()
    {
        var client = new AgentHostClient(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nothing.json"));

        var blocks = await SessionTools.CaptureScreen(
            client, Guid.NewGuid().ToString(), inline: false, maxWidth: -1, TestContext.Current.CancellationToken);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(blocks)).Text;
        Assert.StartsWith("Error: maxWidth must be 0", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureScreen_rejects_a_malformed_pane_id_before_any_ipc()
    {
        var client = new AgentHostClient(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nothing.json"));

        var blocks = await SessionTools.CaptureScreen(
            client, "not-a-guid", inline: false, maxWidth: 0, TestContext.Current.CancellationToken);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(blocks)).Text;
        Assert.Contains("is not a valid pane id", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatExport_reports_path_window_and_replay_hint()
    {
        var text = SessionTools.FormatExport(new ExportReplayResult
        {
            FilePath = @"C:\rec\agent-exports\nova_rec_20260707_120000_abc123.rec",
            EventCount = 42,
            FirstEventMs = 10_000,
            LastEventMs = 25_500,
            TruncatedAtStart = false,
        });

        Assert.Contains("nova_rec_20260707_120000_abc123.rec", text, StringComparison.Ordinal);
        Assert.Contains("42 event(s) covering 15500 ms", text, StringComparison.Ordinal);
        Assert.Contains("input is never recorded", text, StringComparison.Ordinal);
        Assert.Contains("--replay", text, StringComparison.Ordinal);
        Assert.DoesNotContain("truncated", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatExport_flags_truncation_and_empty_windows()
    {
        var truncated = SessionTools.FormatExport(new ExportReplayResult
        {
            FilePath = "/tmp/x.rec",
            EventCount = 5,
            FirstEventMs = 0,
            LastEventMs = 9,
            TruncatedAtStart = true,
        });
        Assert.Contains("suffix of the session", truncated, StringComparison.Ordinal);

        var empty = SessionTools.FormatExport(new ExportReplayResult
        {
            FilePath = "/tmp/y.rec",
            EventCount = 0,
            FirstEventMs = 0,
            LastEventMs = 0,
            TruncatedAtStart = false,
        });
        Assert.Contains("no events", empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExportReplay_rejects_a_non_guid_pane_before_any_ipc()
    {
        var client = new AgentHostClient(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nothing.json"));
        var text = await SessionTools.ExportReplay(client, "not-a-guid", TestContext.Current.CancellationToken);
        Assert.StartsWith("Error: 'not-a-guid' is not a valid pane id", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendInput_rejects_a_non_guid_pane_before_any_ipc()
    {
        var client = new AgentHostClient(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nothing.json"));
        var text = await SessionTools.SendInput(client, "not-a-guid", "ls\r", cancellationToken: TestContext.Current.CancellationToken);
        Assert.StartsWith("Error: 'not-a-guid' is not a valid pane id", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendInput_when_endpoint_unavailable_surfaces_guidance_verbatim()
    {
        // No live endpoint (discovery file absent): the acting call must return
        // the unavailable guidance, not throw.
        var client = new AgentHostClient(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nothing.json"));
        var text = await SessionTools.SendInput(client, Guid.NewGuid().ToString(), "ls\r", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(AgentHostClient.UnavailableMessage, text);
    }

    [Fact]
    public async Task WaitForEvents_rejects_a_negative_cursor_before_any_ipc()
    {
        var client = new AgentHostClient(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nothing.json"));
        var text = await SessionTools.WaitForEvents(client, sinceSeq: -1, timeoutMs: 100, TestContext.Current.CancellationToken);
        Assert.StartsWith("Error: sinceSeq must be 0 or greater", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatScrollback_reports_range_and_paging_hint()
    {
        var text = SessionTools.FormatScrollback(new ReadScrollbackResult
        {
            Lines = AbLines,
            StartLine = 10,
            TotalLines = 100,
        });

        Assert.Contains("lines 10–11 of 100", text, StringComparison.Ordinal);
        Assert.Contains("startLine=12", text, StringComparison.Ordinal);
        Assert.Contains("   10| a", text, StringComparison.Ordinal);
    }
}

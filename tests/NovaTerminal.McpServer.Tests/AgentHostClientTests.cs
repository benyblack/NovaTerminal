using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
        _tempDir = Path.Combine(Path.GetTempPath(), "nova-agentclient-tests-" + Guid.NewGuid().ToString("N"));
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

        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
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
            Lines = new[] { "hello", "world" },
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
    public void FormatScrollback_reports_range_and_paging_hint()
    {
        var text = SessionTools.FormatScrollback(new ReadScrollbackResult
        {
            Lines = new[] { "a", "b" },
            StartLine = 10,
            TotalLines = 100,
        });

        Assert.Contains("lines 10–11 of 100", text, StringComparison.Ordinal);
        Assert.Contains("startLine=12", text, StringComparison.Ordinal);
        Assert.Contains("   10| a", text, StringComparison.Ordinal);
    }
}

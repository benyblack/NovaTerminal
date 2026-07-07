using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NovaTerminal.AgentHost;
using NovaTerminal.AgentHost.Contracts;
using NovaTerminal.VT;

namespace NovaTerminal.AppTests.AgentHost;

/// <summary>
/// In-process protocol tests for the A2 additions: getSessionStatus,
/// waitForEvents (cursor + long-poll), and status in listSessions.
/// </summary>
public class AgentHostStatusProtocolTests : IDisposable
{
    private readonly string _tempDir;

    public AgentHostStatusProtocolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nova-agentstatus-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AgentHostService NewRunningService(AgentSessionRegistry registry)
    {
        var endpoint = OperatingSystem.IsWindows()
            ? "novaterminal-agent-status-test-" + Guid.NewGuid().ToString("N")
            : Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8] + ".sock");
        var service = new AgentHostService(registry, endpoint, _tempDir);
        service.Start();
        Assert.True(service.IsRunning);
        return service;
    }

    private static AgentSessionRegistration Register(AgentSessionRegistry registry, bool hasChildren = false)
    {
        var registration = new AgentSessionRegistration(
            Guid.NewGuid(), new TerminalBuffer(80, 24), "title", "Profile", "local", isActive: true,
            nowProvider: null, hasActiveChildProcessesProvider: () => hasChildren);
        Assert.True(registry.Register(registration));
        return registration;
    }

    private static async Task<AgentHostResponse> HandleAsync(AgentHostService service, string method, string paramsJson = "null", long id = 1)
        => await service.HandleRequestLineAsync(
            $"{{\"v\":{AgentHostProtocol.Version},\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}",
            TestContext.Current.CancellationToken);

    private static T ResultOf<T>(AgentHostResponse response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
        var value = response.Result!.Value.Deserialize(typeInfo);
        Assert.NotNull(value);
        return value!;
    }

    [Fact]
    public async Task GetSessionStatus_reports_the_machine_snapshot()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry);
        var registration = Register(registry);
        registration.StatusMachine.NotifyCommandAccepted("make -j");
        registration.StatusMachine.NotifyCommandStarted();

        var response = await HandleAsync(service, AgentHostProtocol.Methods.GetSessionStatus,
            $"{{\"paneId\":\"{registration.PaneId}\"}}");
        var dto = ResultOf(response, AgentHostJsonContext.Default.SessionStatusDto);

        Assert.Equal(AgentHostProtocol.StatusKinds.Running, dto.Status);
        Assert.Equal(AgentHostProtocol.StatusConfidences.Precise, dto.Confidence);
        Assert.Equal("make -j", dto.CurrentCommand);
        Assert.False(dto.IsStalled);
        Assert.Equal(AgentSessionStatusMachine.StallThresholdSeconds, dto.StallThresholdSeconds);
    }

    [Fact]
    public async Task GetSessionStatus_maps_missing_params_and_unknown_pane_to_distinct_errors()
    {
        using var service = NewRunningService(new AgentSessionRegistry());

        var malformed = await HandleAsync(service, AgentHostProtocol.Methods.GetSessionStatus);
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, malformed.Error?.Code);

        var notFound = await HandleAsync(service, AgentHostProtocol.Methods.GetSessionStatus,
            $"{{\"paneId\":\"{Guid.NewGuid()}\"}}");
        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, notFound.Error?.Code);
    }

    [Fact]
    public async Task ListSessions_includes_status_and_confidence()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry);
        Register(registry);

        var response = await HandleAsync(service, AgentHostProtocol.Methods.ListSessions);
        var session = Assert.Single(ResultOf(response, AgentHostJsonContext.Default.ListSessionsResult).Sessions);

        Assert.Equal(AgentHostProtocol.StatusKinds.AwaitingInput, session.Status);
        Assert.Equal(AgentHostProtocol.StatusConfidences.Heuristic, session.Confidence);
    }

    [Fact]
    public async Task Registering_a_session_while_running_emits_sessionOpened()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry);
        var registration = Register(registry);

        var response = await HandleAsync(service, AgentHostProtocol.Methods.WaitForEvents,
            "{\"sinceSeq\":0,\"timeoutMs\":5000}");
        var result = ResultOf(response, AgentHostJsonContext.Default.WaitForEventsResult);

        var opened = Assert.Single(result.Events, e => e.Type == AgentHostProtocol.EventTypes.SessionOpened);
        Assert.Equal(registration.PaneId, opened.PaneId);
    }

    [Fact]
    public async Task WaitForEvents_long_poll_wakes_on_a_status_event()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry);
        var registration = Register(registry);

        // Drain the sessionOpened event to get a current cursor.
        var drained = ResultOf(
            await HandleAsync(service, AgentHostProtocol.Methods.WaitForEvents, "{\"sinceSeq\":0,\"timeoutMs\":1000}"),
            AgentHostJsonContext.Default.WaitForEventsResult);

        var wait = HandleAsync(service, AgentHostProtocol.Methods.WaitForEvents,
            $"{{\"sinceSeq\":{drained.NextSeq},\"timeoutMs\":20000}}");
        Assert.False(wait.IsCompleted);

        registration.StatusMachine.NotifyCommandStarted(); // → statusChanged(running)

        var result = ResultOf(await wait.WaitAsync(TimeSpan.FromSeconds(10)), AgentHostJsonContext.Default.WaitForEventsResult);
        var evt = Assert.Single(result.Events, e => e.Type == AgentHostProtocol.EventTypes.StatusChanged);
        Assert.Equal(AgentHostProtocol.StatusKinds.Running, evt.Status);
        Assert.Equal(registration.PaneId, evt.PaneId);
    }

    [Fact]
    public async Task Unregistering_emits_sessionClosed_and_stops_forwarding()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry);
        var registration = Register(registry);

        Assert.True(registry.Unregister(registration.PaneId));
        registration.StatusMachine.NotifyCommandStarted(); // must NOT be forwarded

        var result = ResultOf(
            await HandleAsync(service, AgentHostProtocol.Methods.WaitForEvents, "{\"sinceSeq\":0,\"timeoutMs\":1000}"),
            AgentHostJsonContext.Default.WaitForEventsResult);

        Assert.Contains(result.Events, e => e.Type == AgentHostProtocol.EventTypes.SessionClosed);
        Assert.DoesNotContain(result.Events, e => e.Type == AgentHostProtocol.EventTypes.StatusChanged);
    }

    [Fact]
    public async Task WaitForEvents_times_out_with_an_empty_result()
    {
        using var service = NewRunningService(new AgentSessionRegistry());

        var result = ResultOf(
            await HandleAsync(service, AgentHostProtocol.Methods.WaitForEvents, "{\"sinceSeq\":0,\"timeoutMs\":150}"),
            AgentHostJsonContext.Default.WaitForEventsResult);

        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task WaitForEvents_rejects_missing_params()
    {
        using var service = NewRunningService(new AgentSessionRegistry());
        var response = await HandleAsync(service, AgentHostProtocol.Methods.WaitForEvents);
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, response.Error?.Code);
    }
}

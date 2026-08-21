using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.AgentHost;
using NovaTerminal.AgentHost.Contracts;
using NovaTerminal.VT;

namespace NovaTerminal.AppTests.AgentHost;

/// <summary>
/// The endpoint pushes attention signals from the real handlers: reads mark
/// Watched, successful writes mark Wrote, denied writes mark nothing, and
/// act-reachability is republished on demand.
/// </summary>
public class AgentHostAttentionProtocolTests : IDisposable
{
    private readonly string _tempDir;

    public AgentHostAttentionProtocolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nova-agentattention-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private sealed class InputStubSession : NovaTerminal.Pty.ITerminalSession
    {
        private readonly bool _running;
        public InputStubSession(bool running = true) => _running = running;

        public readonly System.Collections.Generic.List<string> Inputs = new();

        public void SendInput(string input) => Inputs.Add(input);
        public bool IsProcessRunning => _running;

        public Guid Id { get; } = Guid.NewGuid();
        public string ShellCommand => "stub";
        public string? ShellArguments => null;
        public bool HasActiveChildProcesses => false;
        public int? ExitCode => null;
        public bool IsRecording => false;
        public event Action<string>? OnOutputReceived { add { } remove { } }
        public event Action<int>? OnExit { add { } remove { } }
        public void Resize(int cols, int rows) { }
        public void StartRecording(string filePath) { }
        public void StopRecording() { }
        public bool IsFlightRecording => false;
        public void EnableFlightRecording(long maxTotalBytes) { }
        public void DisableFlightRecording() { }
        public bool TryExportFlightRecording(string filePath, out NovaTerminal.Replay.FlightExportInfo info) { info = default; return false; }
        public void Dispose() { }
    }

    private AgentHostService NewRunningService(AgentSessionRegistry registry, bool act)
    {
        var endpoint = OperatingSystem.IsWindows()
            ? "novaterminal-agent-attention-test-" + Guid.NewGuid().ToString("N")
            : Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8] + ".sock");
        var service = new AgentHostService(registry, endpoint, _tempDir);
        service.ActEnabled = act;
        service.Start();
        Assert.True(service.IsRunning);
        return service;
    }

    private static AgentSessionRegistration Register(AgentSessionRegistry registry, string kind = "local", Guid? profileId = null)
    {
        var registration = new AgentSessionRegistration(
            Guid.NewGuid(), new TerminalBuffer(80, 24), "title", "Profile", kind,
            isActive: true, profileId: profileId);
        Assert.True(registry.Register(registration));
        return registration;
    }

    /// <summary>Registers a pane with a live, input-accepting session behind it (for sendInput tests).</summary>
    private static AgentSessionRegistration RegisterWithSession(
        AgentSessionRegistry registry, InputStubSession session, string kind = "local", Guid? profileId = null)
    {
        var registration = Register(registry, kind, profileId);
        registration.SetLifecycle(session);
        return registration;
    }

    private static async Task<AgentHostResponse> HandleAsync(
        AgentHostService service, string method, string paramsJson = "null", long id = 1)
        => await service.HandleRequestLineAsync(
            $"{{\"v\":{AgentHostProtocol.Version},\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}",
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Read_screen_marks_the_pane_watched()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: false);
        var registration = Register(registry);

        var response = await HandleAsync(service, "readScreen", $"{{\"paneId\":\"{registration.PaneId}\"}}");

        Assert.Null(response.Error);
        Assert.Equal(AgentAttentionTier.Watched, registration.AttentionMachine.Snapshot().Tier);
    }

    [Fact]
    public async Task Read_scrollback_marks_the_pane_watched()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: false);
        var registration = Register(registry);

        await HandleAsync(service, "readScrollback", $"{{\"paneId\":\"{registration.PaneId}\",\"startLine\":0,\"maxLines\":10}}");

        Assert.Equal(AgentAttentionTier.Watched, registration.AttentionMachine.Snapshot().Tier);
    }

    [Fact]
    public async Task Get_session_status_marks_the_pane_watched()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: false);
        var registration = Register(registry);

        var response = await HandleAsync(service, "getSessionStatus", $"{{\"paneId\":\"{registration.PaneId}\"}}");

        Assert.Null(response.Error);
        Assert.Equal(AgentAttentionTier.Watched, registration.AttentionMachine.Snapshot().Tier);
    }

    [Fact]
    public async Task Send_input_marks_the_pane_wrote_when_allowed()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: true);
        service.SetActionExecutor(new StubExecutor());
        var registration = RegisterWithSession(registry, new InputStubSession());

        var response = await HandleAsync(
            service, "sendInput", $"{{\"paneId\":\"{registration.PaneId}\",\"text\":\"ls\"}}");

        Assert.Null(response.Error);
        var snapshot = registration.AttentionMachine.Snapshot();
        Assert.Equal(AgentAttentionTier.Wrote, snapshot.Tier);
        Assert.Equal("sendInput", snapshot.LastWriteMethod);
    }

    [Fact]
    public async Task A_denied_send_input_marks_nothing()
    {
        // Act is off: the journal records the denial, but the pane indicator must
        // not claim something happened to this pane, because nothing did.
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: false);
        var registration = RegisterWithSession(registry, new InputStubSession());

        var response = await HandleAsync(
            service, "sendInput", $"{{\"paneId\":\"{registration.PaneId}\",\"text\":\"ls\"}}");

        Assert.NotNull(response.Error);
        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
    }

    [Fact]
    public async Task A_send_input_that_fails_at_the_session_marks_nothing()
    {
        // Act is on and the pane is known, but the underlying session rejects the
        // write (process exited) — still nothing actually happened to the pane.
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: true);
        var registration = RegisterWithSession(registry, new InputStubSession(running: false));

        var response = await HandleAsync(
            service, "sendInput", $"{{\"paneId\":\"{registration.PaneId}\",\"text\":\"ls\"}}");

        Assert.NotNull(response.Error);
        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
    }

    [Fact]
    public async Task Close_session_marks_the_pane_wrote_when_closed()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: true);
        var registration = Register(registry);
        // Simulate the real UI executor: closing the pane also unregisters it
        // from the registry, before this handler gets a chance to look again.
        var exec = new StubExecutor { OnClose = id => registry.Unregister(id) };
        service.SetActionExecutor(exec);

        var response = await HandleAsync(service, "closeSession", $"{{\"paneId\":\"{registration.PaneId}\"}}");

        Assert.Null(response.Error);
        Assert.Equal(AgentAttentionTier.Wrote, registration.AttentionMachine.Snapshot().Tier);
        Assert.Equal("closeSession", registration.AttentionMachine.Snapshot().LastWriteMethod);
    }

    [Fact]
    public async Task Close_session_that_fails_marks_nothing()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: true);
        var registration = Register(registry);
        service.SetActionExecutor(new StubExecutor { OnClose = _ => false });

        var response = await HandleAsync(service, "closeSession", $"{{\"paneId\":\"{registration.PaneId}\"}}");

        Assert.NotNull(response.Error);
        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
    }

    [Fact]
    public async Task Spawn_session_marks_the_newly_created_pane_wrote()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: true);
        var spawned = Register(registry); // stands in for the pane the executor "creates"
        var exec = new StubExecutor
        {
            OnSpawn = _ => (new AgentSpawnResult(spawned.PaneId, null, spawned.ProfileName, spawned.Kind), null),
        };
        service.SetActionExecutor(exec);

        var response = await HandleAsync(service, "spawnSession", "{}");

        Assert.Null(response.Error);
        Assert.Equal(AgentAttentionTier.Wrote, spawned.AttentionMachine.Snapshot().Tier);
        Assert.Equal("spawnSession", spawned.AttentionMachine.Snapshot().LastWriteMethod);
    }

    [Fact]
    public void Refresh_actability_marks_a_local_pane_actable_when_act_is_on()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: true);
        var registration = Register(registry);

        service.RefreshActability();

        Assert.True(registration.IsAgentActable);
    }

    [Fact]
    public void Refresh_actability_marks_nothing_when_act_is_off()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: false);
        var registration = Register(registry);

        service.RefreshActability();

        Assert.False(registration.IsAgentActable);
    }

    [Fact]
    public void An_ssh_pane_is_not_actable_without_an_allowlist_probe()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: true);
        var registration = Register(registry, kind: "ssh", profileId: Guid.NewGuid());

        service.RefreshActability();

        Assert.False(registration.IsAgentActable);
    }

    [Fact]
    public void An_allowlisted_ssh_pane_is_actable()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: true);
        var profileId = Guid.NewGuid();
        var registration = Register(registry, kind: "ssh", profileId: profileId);
        service.SetSshProfileAllowlist(id => id == profileId);

        service.RefreshActability();

        Assert.True(registration.IsAgentActable);
    }

    [Fact]
    public void Setting_act_enabled_republishes_actability_immediately()
    {
        // No sweep tick, no RefreshActability call — flipping the toggle alone
        // must be enough, so the pane chrome cannot lag a permission change.
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: false);
        var registration = Register(registry);

        service.ActEnabled = true;

        Assert.True(registration.IsAgentActable);
    }

    [Fact]
    public async Task The_in_flight_poll_count_returns_to_zero()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: false);
        Assert.Equal(0, service.InFlightPollCount);

        await HandleAsync(service, "waitForEvents", "{\"sinceSeq\":0,\"timeoutMs\":300}");

        Assert.Equal(0, service.InFlightPollCount);
    }

    [Fact]
    public async Task Observe_activity_fires_when_a_poll_parks_and_returns()
    {
        // The zero -> non-zero -> zero transition is what the window indicator
        // subscribes to; observing the mid-flight count from the awaiting thread
        // would be racy, so assert on the event instead.
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: false);
        int transitions = 0;
        service.ObserveActivityChanged += () => Interlocked.Increment(ref transitions);

        await HandleAsync(service, "waitForEvents", "{\"sinceSeq\":0,\"timeoutMs\":300}");

        Assert.Equal(2, Volatile.Read(ref transitions));
    }
}

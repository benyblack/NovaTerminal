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

    // InputStubSession now lives in InputStubSession.cs, shared with the act tests.

    private AgentHostService NewRunningService(AgentSessionRegistry registry, bool act)
    {
        var service = NewStoppedService(registry);
        service.ActEnabled = act;
        service.Start();
        Assert.True(service.IsRunning);
        return service;
    }

    /// <summary>
    /// A service that never binds an endpoint — i.e. observe off. Actability
    /// includes the observe term, so this is the state that must publish no
    /// bars no matter what the act toggle says.
    /// </summary>
    private AgentHostService NewStoppedService(AgentSessionRegistry registry)
    {
        var endpoint = OperatingSystem.IsWindows()
            ? "novaterminal-agent-attention-test-" + Guid.NewGuid().ToString("N")
            : Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8] + ".sock");
        return new AgentHostService(registry, endpoint, _tempDir);
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
    public void Act_without_a_running_endpoint_marks_nothing()
    {
        // Actable is observe && act && (local || allowlisted). The two settings
        // checkboxes are independent, so observe-off/act-on is reachable, and
        // without the observe term every local pane grew a 22 px "agent access"
        // bar - reflowing its PTY once - claiming an agent could type into it
        // while nothing was listening at all.
        var registry = new AgentSessionRegistry();
        using var service = NewStoppedService(registry);
        service.ActEnabled = true;
        Assert.False(service.IsRunning);
        var registration = Register(registry);

        service.RefreshActability();

        Assert.False(registration.IsAgentActable);
    }

    [Fact]
    public void Stopping_the_endpoint_clears_actability()
    {
        // Apply(false) has to republish, not just stop: the 1 s sweep is the
        // only other caller of RefreshActability and it dies with the endpoint,
        // so without this the bars would persist forever.
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: true);
        var registration = Register(registry);
        service.RefreshActability();
        Assert.True(registration.IsAgentActable);

        service.Apply(enabled: false);

        Assert.False(service.IsRunning);
        Assert.False(registration.IsAgentActable);
    }

    [Fact]
    public void Starting_the_endpoint_publishes_actability()
    {
        // The other edge of the same call: observe turned on with act already
        // on must surface the bars without waiting for a sweep tick.
        var registry = new AgentSessionRegistry();
        using var service = NewStoppedService(registry);
        service.ActEnabled = true;
        var registration = Register(registry);
        Assert.False(registration.IsAgentActable);

        service.Apply(enabled: true);

        Assert.True(service.IsRunning);
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
    public void A_newly_registered_pane_is_actable_before_Register_returns()
    {
        // The reflow bug: OnSessionRegistered used to do nothing about
        // actability, and AgentSessionRegistration._isAgentActable defaults to
        // false. So a pane opened while act was on was laid out with no status
        // bar, and the 1 s sweep flipped it a moment later — the 22 px bar
        // appeared, the terminal row shrank, and the PTY reflowed underneath
        // whatever full-screen TUI had just started. The design allows one
        // reflow, at permission-toggle time only.
        //
        // Synchronously, not "eventually": the pane reads IsAgentActable
        // straight off the registration on the next line of SetupCommon, so a
        // value that only lands on a later tick is the same bug.
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: true);

        var registration = new AgentSessionRegistration(
            Guid.NewGuid(), new TerminalBuffer(80, 24), "title", "Profile", "local",
            isActive: true, profileId: null);
        Assert.True(registry.Register(registration));

        Assert.True(registration.IsAgentActable);
    }

    [Fact]
    public void A_newly_registered_pane_is_not_actable_when_act_is_off()
    {
        // The other half — registering must publish the *correct* answer, not
        // just any answer. A pane created with act off stays bar-less.
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: false);

        var registration = new AgentSessionRegistration(
            Guid.NewGuid(), new TerminalBuffer(80, 24), "title", "Profile", "local",
            isActive: true, profileId: null);
        Assert.True(registry.Register(registration));

        Assert.False(registration.IsAgentActable);
    }

    [Fact]
    public void A_newly_registered_ssh_pane_off_the_allowlist_is_not_actable()
    {
        // Registering must run the full actability rule, allowlist included —
        // an SSH pane whose profile is not allowlisted must not flash a bar on
        // creation just because the global act toggle is on.
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: true);
        var allowed = Guid.NewGuid();
        service.SetSshProfileAllowlist(id => id == allowed);

        var denied = new AgentSessionRegistration(
            Guid.NewGuid(), new TerminalBuffer(80, 24), "title", "Profile", "ssh",
            isActive: true, profileId: Guid.NewGuid());
        Assert.True(registry.Register(denied));
        Assert.False(denied.IsAgentActable);

        var permitted = new AgentSessionRegistration(
            Guid.NewGuid(), new TerminalBuffer(80, 24), "title", "Profile", "ssh",
            isActive: true, profileId: allowed);
        Assert.True(registry.Register(permitted));
        Assert.True(permitted.IsAgentActable);
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

    [Fact]
    public async Task A_throwing_observe_subscriber_does_not_leak_the_poll_count_or_fail_the_response()
    {
        // Regression pin for the finding: the increment and its 0->1 invoke used
        // to sit outside the try/finally that decrements. A subscriber throwing
        // on that edge left _inFlightPolls stuck non-zero forever and turned the
        // waitForEvents call into an Internal error. No subscriber exists on this
        // path in production yet (Task 7 adds the first one), which is exactly
        // why this must be pinned before one shows up.
        var registry = new AgentSessionRegistry();
        using var service = NewRunningService(registry, act: false);
        service.ObserveActivityChanged += () => throw new InvalidOperationException("boom");

        var response = await HandleAsync(service, "waitForEvents", "{\"sinceSeq\":0,\"timeoutMs\":300}");

        Assert.Null(response.Error);
        Assert.Equal(0, service.InFlightPollCount);
    }

    [Fact]
    public async Task A_throwing_attention_subscriber_does_not_fail_or_unjournal_a_successful_write()
    {
        // Regression pin: AgentAttentionMachine.DrainPendingEvents deliberately
        // rethrows subscriber exceptions out of NoteWrote/NoteRead. The input
        // has already gone to the PTY by the time sendInput calls NoteWrote, so
        // an unguarded throw there must not convert a real success into an
        // Internal error, and must not skip the Journaled(...) record of the
        // attempt that genuinely happened.
        var registry = new AgentSessionRegistry();
        var journal = new AgentActivityJournal();
        var endpoint = OperatingSystem.IsWindows()
            ? "novaterminal-agent-attention-test-" + Guid.NewGuid().ToString("N")
            : Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8] + ".sock");
        using var service = new AgentHostService(registry, endpoint, _tempDir, journal: journal);
        service.ActEnabled = true;
        service.Start();
        Assert.True(service.IsRunning);
        var registration = RegisterWithSession(registry, new InputStubSession());
        registration.AttentionMachine.Changed += _ => throw new InvalidOperationException("boom");

        var response = await HandleAsync(
            service, "sendInput", $"{{\"paneId\":\"{registration.PaneId}\",\"text\":\"ls\"}}");

        Assert.Null(response.Error);
        Assert.Contains(
            journal.Snapshot(),
            e => e.Method == AgentHostProtocol.Methods.SendInput && e.PaneId == registration.PaneId && e.Outcome == "ok");
    }
}

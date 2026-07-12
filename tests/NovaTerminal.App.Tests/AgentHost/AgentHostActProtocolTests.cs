using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using NovaTerminal.AgentHost;
using NovaTerminal.AgentHost.Contracts;

namespace NovaTerminal.AppTests.AgentHost;

/// <summary>
/// Tests for the A3 act surface — <c>sendInput</c> gating and journaling
/// (docs/plans/2026-07-12-agent-host-a3-act-design.md, PR1). Acting requires
/// the separate act opt-in on top of observe; SSH targets additionally require
/// per-profile allowlisting; every attempt (allowed or denied) is journaled.
/// </summary>
public class AgentHostActProtocolTests
{
    private sealed class InputStubSession : NovaTerminal.Pty.ITerminalSession
    {
        private readonly bool _running;
        public InputStubSession(bool running = true) => _running = running;

        public readonly List<string> Inputs = new();

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

    private static AgentSessionRegistration Register(
        AgentSessionRegistry registry, string kind, InputStubSession session, Guid? profileId = null)
    {
        var reg = new AgentSessionRegistration(
            Guid.NewGuid(), new NovaTerminal.VT.TerminalBuffer(80, 24),
            "title", "Profile", kind, isActive: true, profileId: profileId);
        reg.SetLifecycle(session);
        registry.Register(reg);
        return reg;
    }

    private static AgentHostService NewService(AgentSessionRegistry registry, AgentActivityJournal journal)
    {
        var endpoint = OperatingSystem.IsWindows()
            ? "novaterminal-agent-test-" + Guid.NewGuid().ToString("N")
            : System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N")[..8] + ".sock");
        return new AgentHostService(registry, endpoint, System.IO.Path.GetTempPath(), null, journal);
    }

    private static string SendInputLine(Guid paneId, string text, long id = 1)
    {
        var json = JsonSerializer.Serialize(
            new SendInputParams { PaneId = paneId, Text = text }, AgentHostJsonContext.Default.SendInputParams);
        return $"{{\"v\":{AgentHostProtocol.Version},\"id\":{id},\"method\":\"{AgentHostProtocol.Methods.SendInput}\",\"params\":{json}}}";
    }

    private static AgentHostResponse Handle(AgentHostService service, string line)
        => service.HandleRequestLineAsync(line, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    // ── Hard-fail acceptance (DIRECTION) ─────────────────────────────────────

    [Fact]
    public void SendInput_is_rejected_with_actDisabled_when_only_observe_is_enabled()
    {
        var registry = new AgentSessionRegistry();
        var session = new InputStubSession();
        var reg = Register(registry, "local", session);
        var journal = new AgentActivityJournal();
        using var service = NewService(registry, journal);
        service.ActEnabled = false; // observe only

        var response = Handle(service, SendInputLine(reg.PaneId, "ls\r"));

        Assert.Equal(AgentHostProtocol.ErrorCodes.ActDisabled, response.Error?.Code);
        Assert.Empty(session.Inputs); // nothing reached the session
        var entry = Assert.Single(journal.Snapshot());
        Assert.Equal(AgentHostProtocol.ErrorCodes.ActDisabled, entry.Outcome);
    }

    [Fact]
    public void SendInput_to_non_allowlisted_ssh_session_is_profileNotAllowed()
    {
        var registry = new AgentSessionRegistry();
        var session = new InputStubSession();
        var profileId = Guid.NewGuid();
        var reg = Register(registry, "ssh", session, profileId);
        var journal = new AgentActivityJournal();
        using var service = NewService(registry, journal);
        service.ActEnabled = true;
        service.SetSshProfileAllowlist(_ => false); // not allowlisted

        var response = Handle(service, SendInputLine(reg.PaneId, "whoami\r"));

        Assert.Equal(AgentHostProtocol.ErrorCodes.ProfileNotAllowed, response.Error?.Code);
        Assert.Empty(session.Inputs);
    }

    [Fact]
    public void Stop_clears_the_ssh_allowlist_probe_so_it_cannot_pin_the_window()
    {
        // The static service singleton outlives MainWindow; the probe closes over
        // it. Stop must release the delegate (fail-closed afterwards) so a closed
        // window can be collected.
        var registry = new AgentSessionRegistry();
        var session = new InputStubSession();
        var reg = Register(registry, "ssh", session, Guid.NewGuid());
        using var service = NewService(registry, new AgentActivityJournal());
        service.ActEnabled = true;
        service.SetSshProfileAllowlist(_ => true);

        service.Stop(); // window closing

        var response = Handle(service, SendInputLine(reg.PaneId, "x\r"));
        Assert.Equal(AgentHostProtocol.ErrorCodes.ProfileNotAllowed, response.Error?.Code);
    }

    [Fact]
    public void SendInput_to_ssh_session_with_no_allowlist_probe_fails_closed()
    {
        var registry = new AgentSessionRegistry();
        var session = new InputStubSession();
        var reg = Register(registry, "ssh", session, Guid.NewGuid());
        var journal = new AgentActivityJournal();
        using var service = NewService(registry, journal);
        service.ActEnabled = true;
        // No SetSshProfileAllowlist call → probe is null → deny.

        var response = Handle(service, SendInputLine(reg.PaneId, "x\r"));

        Assert.Equal(AgentHostProtocol.ErrorCodes.ProfileNotAllowed, response.Error?.Code);
    }

    // ── Success paths ────────────────────────────────────────────────────────

    [Fact]
    public void SendInput_to_local_session_with_act_enabled_delivers_bytes_faithfully()
    {
        var registry = new AgentSessionRegistry();
        var session = new InputStubSession();
        var reg = Register(registry, "local", session);
        var journal = new AgentActivityJournal();
        using var service = NewService(registry, journal);
        service.ActEnabled = true;

        // Control characters must survive verbatim: Ctrl-C then "exit\r".
        string payload = "exit\r";
        var response = Handle(service, SendInputLine(reg.PaneId, payload));

        Assert.Null(response.Error);
        var result = response.Result!.Value.Deserialize(AgentHostJsonContext.Default.SendInputResult);
        Assert.Equal(Encoding.UTF8.GetByteCount(payload), result!.BytesSent);
        Assert.Equal(payload, Assert.Single(session.Inputs)); // byte-faithful at the API boundary
        Assert.Equal("ok", Assert.Single(journal.Snapshot()).Outcome);
    }

    [Fact]
    public void SendInput_to_allowlisted_ssh_session_succeeds()
    {
        var registry = new AgentSessionRegistry();
        var session = new InputStubSession();
        var profileId = Guid.NewGuid();
        var reg = Register(registry, "ssh", session, profileId);
        using var service = NewService(registry, new AgentActivityJournal());
        service.ActEnabled = true;
        service.SetSshProfileAllowlist(id => id == profileId);

        var response = Handle(service, SendInputLine(reg.PaneId, "uptime\r"));

        Assert.Null(response.Error);
        Assert.Equal("uptime\r", Assert.Single(session.Inputs));
    }

    // ── Protocol errors ──────────────────────────────────────────────────────

    [Fact]
    public void SendInput_for_unknown_pane_is_sessionNotFound()
    {
        using var service = NewService(new AgentSessionRegistry(), new AgentActivityJournal());
        service.ActEnabled = true;

        var response = Handle(service, SendInputLine(Guid.NewGuid(), "x"));

        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, response.Error?.Code);
    }

    [Fact]
    public void SendInput_to_exited_session_is_sessionNotRunning()
    {
        var registry = new AgentSessionRegistry();
        var session = new InputStubSession(running: false);
        var reg = Register(registry, "local", session);
        using var service = NewService(registry, new AgentActivityJournal());
        service.ActEnabled = true;

        var response = Handle(service, SendInputLine(reg.PaneId, "x"));

        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotRunning, response.Error?.Code);
        Assert.Empty(session.Inputs);
    }

    [Fact]
    public void SendInput_over_the_size_cap_is_malformedRequest()
    {
        var registry = new AgentSessionRegistry();
        var session = new InputStubSession();
        var reg = Register(registry, "local", session);
        using var service = NewService(registry, new AgentActivityJournal());
        service.ActEnabled = true;

        string huge = new string('a', AgentHostProtocol.MaxSendInputBytes + 1);
        var response = Handle(service, SendInputLine(reg.PaneId, huge));

        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, response.Error?.Code);
        Assert.Empty(session.Inputs);
    }

    [Fact]
    public void SendInput_without_params_is_malformedRequest_and_is_journaled()
    {
        var journal = new AgentActivityJournal();
        using var service = NewService(new AgentSessionRegistry(), journal);
        service.ActEnabled = true;

        var line = $"{{\"v\":{AgentHostProtocol.Version},\"id\":1,\"method\":\"{AgentHostProtocol.Methods.SendInput}\",\"params\":null}}";
        var response = Handle(service, line);

        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, response.Error?.Code);
        // Malformed acting attempts are externally reachable — they must be visible.
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, Assert.Single(journal.Snapshot()).Outcome);
    }

    [Fact]
    public void SendInput_with_missing_required_field_is_malformedRequest_and_is_journaled()
    {
        var journal = new AgentActivityJournal();
        using var service = NewService(new AgentSessionRegistry(), journal);
        service.ActEnabled = true;

        // params present but missing the required "text" field → deserialization throws.
        var line = $"{{\"v\":{AgentHostProtocol.Version},\"id\":1,\"method\":\"{AgentHostProtocol.Methods.SendInput}\",\"params\":{{\"paneId\":\"{Guid.NewGuid()}\"}}}}";
        var response = Handle(service, line);

        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, response.Error?.Code);
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, Assert.Single(journal.Snapshot()).Outcome);
    }

    [Fact]
    public void SendInput_succeeds_even_if_a_journal_subscriber_throws()
    {
        // A throwing UI subscriber must not reverse a delivered sendInput —
        // otherwise a retry double-submits the input.
        var registry = new AgentSessionRegistry();
        var session = new InputStubSession();
        var reg = Register(registry, "local", session);
        var journal = new AgentActivityJournal();
        journal.EntryAdded += _ => throw new InvalidOperationException("boom");
        using var service = NewService(registry, journal);
        service.ActEnabled = true;

        var response = Handle(service, SendInputLine(reg.PaneId, "ls\r"));

        Assert.Null(response.Error);
        Assert.Equal("ls\r", Assert.Single(session.Inputs));
    }

    // ── spawnSession / closeSession ──────────────────────────────────────────

    private sealed class StubExecutor : IAgentActionExecutor
    {
        public Func<string?, (AgentSpawnResult?, AgentSpawnError?)>? OnSpawn;
        public Func<Guid, bool>? OnClose;
        public string? LastSpawnProfile;
        public Guid? LastClosePane;

        public Task<(AgentSpawnResult? Result, AgentSpawnError? Error)> SpawnAsync(string? profileName)
        {
            LastSpawnProfile = profileName;
            var r = OnSpawn?.Invoke(profileName) ?? (null, AgentSpawnError.SpawnFailed);
            return Task.FromResult(r);
        }

        public Task<bool> ClosePaneAsync(Guid paneId)
        {
            LastClosePane = paneId;
            return Task.FromResult(OnClose?.Invoke(paneId) ?? false);
        }
    }

    private static string SpawnLine(string? profile, long id = 1)
    {
        var json = JsonSerializer.Serialize(
            new SpawnSessionParams { Profile = profile }, AgentHostJsonContext.Default.SpawnSessionParams);
        return $"{{\"v\":{AgentHostProtocol.Version},\"id\":{id},\"method\":\"{AgentHostProtocol.Methods.SpawnSession}\",\"params\":{json}}}";
    }

    private static string CloseLine(Guid paneId, long id = 1)
    {
        var json = JsonSerializer.Serialize(
            new CloseSessionParams { PaneId = paneId }, AgentHostJsonContext.Default.CloseSessionParams);
        return $"{{\"v\":{AgentHostProtocol.Version},\"id\":{id},\"method\":\"{AgentHostProtocol.Methods.CloseSession}\",\"params\":{json}}}";
    }

    [Fact]
    public void SpawnSession_is_actDisabled_when_only_observe_is_enabled()
    {
        var journal = new AgentActivityJournal();
        using var service = NewService(new AgentSessionRegistry(), journal);
        service.SetActionExecutor(new StubExecutor());
        // act disabled

        var response = Handle(service, SpawnLine("Bash"));

        Assert.Equal(AgentHostProtocol.ErrorCodes.ActDisabled, response.Error?.Code);
        Assert.Equal(AgentHostProtocol.ErrorCodes.ActDisabled, Assert.Single(journal.Snapshot()).Outcome);
    }

    [Fact]
    public void SpawnSession_without_executor_is_actUnavailable()
    {
        using var service = NewService(new AgentSessionRegistry(), new AgentActivityJournal());
        service.ActEnabled = true;
        // no executor published

        var response = Handle(service, SpawnLine("Bash"));

        Assert.Equal(AgentHostProtocol.ErrorCodes.ActUnavailable, response.Error?.Code);
    }

    [Fact]
    public void SpawnSession_maps_executor_errors_to_stable_codes()
    {
        using var service = NewService(new AgentSessionRegistry(), new AgentActivityJournal());
        service.ActEnabled = true;

        var notFound = new StubExecutor { OnSpawn = _ => (null, AgentSpawnError.ProfileNotFound) };
        service.SetActionExecutor(notFound);
        Assert.Equal(AgentHostProtocol.ErrorCodes.ProfileNotFound, Handle(service, SpawnLine("nope")).Error?.Code);

        var notAllowed = new StubExecutor { OnSpawn = _ => (null, AgentSpawnError.ProfileNotAllowed) };
        service.SetActionExecutor(notAllowed);
        Assert.Equal(AgentHostProtocol.ErrorCodes.ProfileNotAllowed, Handle(service, SpawnLine("prod-ssh")).Error?.Code);
    }

    [Fact]
    public void SpawnSession_success_returns_pane_identity_and_journals()
    {
        using var service = NewService(new AgentSessionRegistry(), new AgentActivityJournal());
        service.ActEnabled = true;
        var paneId = Guid.NewGuid();
        var tabId = Guid.NewGuid();
        var exec = new StubExecutor { OnSpawn = _ => (new AgentSpawnResult(paneId, tabId, "Bash", "local"), null) };
        service.SetActionExecutor(exec);

        var response = Handle(service, SpawnLine(null));

        Assert.Null(response.Error);
        var result = response.Result!.Value.Deserialize(AgentHostJsonContext.Default.SpawnSessionResult);
        Assert.Equal(paneId, result!.PaneId);
        Assert.Equal(tabId, result.TabId);
        Assert.Equal("Bash", result.ProfileName);
        Assert.Equal("local", result.Kind);
        Assert.Null(exec.LastSpawnProfile); // null profile passed through as default
    }

    [Fact]
    public void CloseSession_is_actDisabled_when_only_observe_is_enabled()
    {
        var registry = new AgentSessionRegistry();
        var reg = Register(registry, "local", new InputStubSession());
        var journal = new AgentActivityJournal();
        using var service = NewService(registry, journal);
        service.SetActionExecutor(new StubExecutor());

        var response = Handle(service, CloseLine(reg.PaneId));

        Assert.Equal(AgentHostProtocol.ErrorCodes.ActDisabled, response.Error?.Code);
    }

    [Fact]
    public void CloseSession_for_unknown_pane_is_sessionNotFound()
    {
        using var service = NewService(new AgentSessionRegistry(), new AgentActivityJournal());
        service.ActEnabled = true;
        service.SetActionExecutor(new StubExecutor { OnClose = _ => true });

        var response = Handle(service, CloseLine(Guid.NewGuid()));

        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, response.Error?.Code);
    }

    [Fact]
    public void CloseSession_closes_a_live_registered_pane_and_journals()
    {
        var registry = new AgentSessionRegistry();
        var reg = Register(registry, "local", new InputStubSession());
        var journal = new AgentActivityJournal();
        using var service = NewService(registry, journal);
        service.ActEnabled = true;
        var exec = new StubExecutor { OnClose = _ => true };
        service.SetActionExecutor(exec);

        var response = Handle(service, CloseLine(reg.PaneId));

        Assert.Null(response.Error);
        var result = response.Result!.Value.Deserialize(AgentHostJsonContext.Default.CloseSessionResult);
        Assert.True(result!.Closed);
        Assert.Equal(reg.PaneId, exec.LastClosePane);
        Assert.Equal("ok", Assert.Single(journal.Snapshot()).Outcome);
    }

    [Fact]
    public void CloseSession_when_executor_reports_no_pane_is_sessionNotFound()
    {
        var registry = new AgentSessionRegistry();
        var reg = Register(registry, "local", new InputStubSession());
        using var service = NewService(registry, new AgentActivityJournal());
        service.ActEnabled = true;
        service.SetActionExecutor(new StubExecutor { OnClose = _ => false }); // registry knows it, UI raced it away

        var response = Handle(service, CloseLine(reg.PaneId));

        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, response.Error?.Code);
    }

    [Fact]
    public void Stop_clears_the_action_executor()
    {
        var registry = new AgentSessionRegistry();
        var reg = Register(registry, "local", new InputStubSession());
        using var service = NewService(registry, new AgentActivityJournal());
        service.ActEnabled = true;
        service.SetActionExecutor(new StubExecutor { OnClose = _ => true });

        service.Stop(); // window closing → executor released (no window pinning)

        Assert.Equal(AgentHostProtocol.ErrorCodes.ActUnavailable, Handle(service, CloseLine(reg.PaneId)).Error?.Code);
    }

    // ── Journal ──────────────────────────────────────────────────────────────

    [Fact]
    public void Every_attempt_allowed_or_denied_is_journaled_once()
    {
        var registry = new AgentSessionRegistry();
        var session = new InputStubSession();
        var reg = Register(registry, "local", session);
        var journal = new AgentActivityJournal();
        using var service = NewService(registry, journal);

        service.ActEnabled = false;
        Handle(service, SendInputLine(reg.PaneId, "a"));   // denied: actDisabled
        service.ActEnabled = true;
        Handle(service, SendInputLine(reg.PaneId, "b"));   // ok
        Handle(service, SendInputLine(Guid.NewGuid(), "c")); // denied: sessionNotFound

        var entries = journal.Snapshot(); // newest first
        Assert.Equal(3, entries.Count);
        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, entries[0].Outcome);
        Assert.Equal("ok", entries[1].Outcome);
        Assert.Equal(AgentHostProtocol.ErrorCodes.ActDisabled, entries[2].Outcome);
        Assert.All(entries, e => Assert.Equal(AgentHostProtocol.Methods.SendInput, e.Method));
    }
}

using System;
using NovaTerminal.AgentHost;
using NovaTerminal.VT;

namespace NovaTerminal.AppTests.AgentHost;

/// <summary>
/// The registration owns a pane's attention machine and its published
/// act-reachability. No endpoint, no UI.
/// </summary>
public class AgentAttentionRegistrationTests
{
    // Same shape as AgentAttentionMachineTests.FakeClock: the registration
    // forwards its nowProvider straight to the AgentAttentionMachine it
    // constructs, so sharing one clock lets a test drive both.
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => Now += by;
        public Func<DateTimeOffset> Provider => () => Now;
    }

    private static AgentSessionRegistration MakeRegistration()
        => new(
            paneId: Guid.NewGuid(),
            buffer: new TerminalBuffer(80, 24),
            title: "pane",
            profileName: "Terminal",
            kind: "local",
            isActive: false);

    [Fact]
    public void Registration_exposes_an_attention_machine_starting_idle()
    {
        var registration = MakeRegistration();
        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
    }

    [Fact]
    public void Act_reachability_defaults_to_false()
    {
        Assert.False(MakeRegistration().IsAgentActable);
    }

    [Fact]
    public void Becoming_the_active_pane_forwards_focus_to_the_machine()
    {
        var clock = new FakeClock();
        var registration = new AgentSessionRegistration(
            paneId: Guid.NewGuid(),
            buffer: new TerminalBuffer(80, 24),
            title: "pane",
            profileName: "Terminal",
            kind: "local",
            isActive: false,
            nowProvider: clock.Provider);

        registration.AttentionMachine.NoteWrote("sendInput");

        // Not focused: the write stays lit no matter how much time passes.
        // This half proves the clock is actually wired into the machine.
        clock.Advance(TimeSpan.FromSeconds(AgentAttentionMachine.WriteFloorSeconds));
        registration.AttentionMachine.Tick();
        Assert.Equal(AgentAttentionTier.Wrote, registration.AttentionMachine.Snapshot().Tier);

        // The pane becomes active; the snapshot push must forward that focus
        // change to the machine, which retires the write now that the floor
        // has elapsed. This is the assertion that fails if UpdateSnapshot
        // stops calling AttentionMachine.NoteFocusChanged.
        registration.UpdateSnapshot("pane", "Terminal", "local", isActive: true, profileId: null);
        registration.AttentionMachine.Tick();

        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
    }
}

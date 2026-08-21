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
        var registration = MakeRegistration();
        registration.AttentionMachine.NoteWrote("sendInput");

        // Not focused: the write stays lit no matter how much time passes.
        registration.AttentionMachine.Tick();
        Assert.Equal(AgentAttentionTier.Wrote, registration.AttentionMachine.Snapshot().Tier);

        // The pane becomes active; the snapshot push must forward that.
        registration.UpdateSnapshot("pane", "Terminal", "local", isActive: true, profileId: null);

        Assert.True(registration.IsActive);
    }
}

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

    // ---- focus is not selection -----------------------------------------
    //
    // IsActivePane means "the selected pane inside the app". The app can be
    // minimized or behind another window with that pane still selected, so
    // selection alone is not evidence the user saw anything.

    private static AgentSessionRegistration PaneWithClock(FakeClock clock, bool isActive)
        => new(
            paneId: Guid.NewGuid(),
            buffer: new TerminalBuffer(80, 24),
            title: "pane",
            profileName: "Terminal",
            kind: "local",
            isActive: isActive,
            nowProvider: clock.Provider);

    [Fact]
    public void A_write_survives_its_floor_while_the_window_is_not_front()
    {
        // The bug this pins, and it is the whole point of the sticky tier: the
        // user alt-tabs away or minimizes. Nothing clears IsActivePane — there
        // is no Deactivated handler that would — so the pane stayed "focused"
        // forever. An agent then typed into it, and the periodic Tick ten
        // seconds later saw focused + floor-elapsed and retired the mark. The
        // pane segment and the tab glyph both cleared with nobody looking: the
        // one signal built to survive until seen, vanishing in exactly the
        // scenario it exists for.
        var clock = new FakeClock();
        var registration = PaneWithClock(clock, isActive: true);

        registration.NoteWindowVisibilityChanged(false);
        registration.AttentionMachine.NoteWrote("sendInput");

        clock.Advance(TimeSpan.FromSeconds(AgentAttentionMachine.WriteFloorSeconds * 10));
        registration.AttentionMachine.Tick();

        Assert.Equal(AgentAttentionTier.Wrote, registration.AttentionMachine.Snapshot().Tier);
    }

    [Fact]
    public void Coming_back_to_the_window_retires_a_write_that_is_past_its_floor()
    {
        // The other half, and the one that keeps the fix from being "the mark
        // never clears": once the user is actually looking at the pane again,
        // a write already past its floor is acknowledged promptly. Note the
        // window push alone does it — no tick and no pane-level change is
        // needed, because the machine re-evaluates acknowledgement on every
        // signal.
        var clock = new FakeClock();
        var registration = PaneWithClock(clock, isActive: true);

        registration.NoteWindowVisibilityChanged(false);
        registration.AttentionMachine.NoteWrote("sendInput");
        clock.Advance(TimeSpan.FromSeconds(AgentAttentionMachine.WriteFloorSeconds * 10));
        registration.AttentionMachine.Tick();
        Assert.Equal(AgentAttentionTier.Wrote, registration.AttentionMachine.Snapshot().Tier);

        registration.NoteWindowVisibilityChanged(true);

        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
    }

    [Fact]
    public void A_snapshot_push_cannot_refocus_a_pane_whose_window_is_away()
    {
        // The trap in the fix. UpdateSnapshot runs on any pane-level change —
        // a title change from a shell OSC, a profile edit — and it re-pushes
        // focus every time. If it pushed the raw isActive it would undo the
        // window's "not visible" the moment the shell repainted its title, and
        // the next tick would retire the write anyway. Focus has to be the AND
        // of both halves at every push site, not just at the window one.
        var clock = new FakeClock();
        var registration = PaneWithClock(clock, isActive: true);

        registration.NoteWindowVisibilityChanged(false);
        registration.AttentionMachine.NoteWrote("sendInput");

        registration.UpdateSnapshot("new title", "Terminal", "local", isActive: true, profileId: null);
        clock.Advance(TimeSpan.FromSeconds(AgentAttentionMachine.WriteFloorSeconds * 10));
        registration.AttentionMachine.Tick();

        Assert.Equal(AgentAttentionTier.Wrote, registration.AttentionMachine.Snapshot().Tier);
    }

    [Fact]
    public void A_front_window_does_not_make_an_unselected_pane_focused()
    {
        // The AND has to cut both ways: the window being front says nothing
        // about a pane the user has not selected. A write into a background
        // pane of a foreground window stays lit until that pane is selected.
        var clock = new FakeClock();
        var registration = PaneWithClock(clock, isActive: false);

        registration.NoteWindowVisibilityChanged(true);
        registration.AttentionMachine.NoteWrote("sendInput");

        clock.Advance(TimeSpan.FromSeconds(AgentAttentionMachine.WriteFloorSeconds * 10));
        registration.AttentionMachine.Tick();

        Assert.Equal(AgentAttentionTier.Wrote, registration.AttentionMachine.Snapshot().Tier);
    }
}

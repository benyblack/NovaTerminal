using System;
using System.Collections.Generic;
using NovaTerminal.AgentHost;

namespace NovaTerminal.AppTests.AgentHost;

/// <summary>
/// Deterministic tests for the per-pane agent attention tiers
/// (docs/superpowers/specs/2026-08-21-agent-access-pane-indicator-design.md).
/// A fake clock drives every threshold; no UI, no timers, no PTY. Mirrors the
/// shape of <see cref="AgentSessionStatusMachineTests"/>.
/// </summary>
public class AgentAttentionMachineTests
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => Now += by;
        public Func<DateTimeOffset> Provider => () => Now;
    }

    private static (AgentAttentionMachine Machine, FakeClock Clock, List<AgentAttentionSnapshot> Changes) Make()
    {
        var clock = new FakeClock();
        var machine = new AgentAttentionMachine(clock.Provider);
        var changes = new List<AgentAttentionSnapshot>();
        machine.Changed += changes.Add;
        return (machine, clock, changes);
    }

    [Fact]
    public void Fresh_machine_is_idle()
    {
        var (machine, _, _) = Make();
        Assert.Equal(AgentAttentionTier.Idle, machine.Snapshot().Tier);
    }

    [Fact]
    public void A_read_lights_watched_and_decays_after_three_seconds()
    {
        var (machine, clock, _) = Make();

        machine.NoteRead();
        Assert.Equal(AgentAttentionTier.Watched, machine.Snapshot().Tier);

        clock.Advance(TimeSpan.FromSeconds(2));
        machine.Tick();
        Assert.Equal(AgentAttentionTier.Watched, machine.Snapshot().Tier);

        clock.Advance(TimeSpan.FromSeconds(1));
        machine.Tick();
        Assert.Equal(AgentAttentionTier.Idle, machine.Snapshot().Tier);
    }

    [Fact]
    public void A_write_outranks_a_concurrent_read()
    {
        var (machine, _, _) = Make();

        machine.NoteRead();
        machine.NoteWrote("sendInput");

        var snapshot = machine.Snapshot();
        Assert.Equal(AgentAttentionTier.Wrote, snapshot.Tier);
        Assert.Equal("sendInput", snapshot.LastWriteMethod);
    }

    [Fact]
    public void A_write_does_not_decay_on_its_own()
    {
        var (machine, clock, _) = Make();

        machine.NoteWrote("sendInput");
        clock.Advance(TimeSpan.FromMinutes(5));
        machine.Tick();

        Assert.Equal(AgentAttentionTier.Wrote, machine.Snapshot().Tier);
    }

    [Fact]
    public void Focus_clears_a_write_once_the_floor_has_elapsed()
    {
        var (machine, clock, _) = Make();

        machine.NoteWrote("sendInput");
        clock.Advance(TimeSpan.FromSeconds(10));
        machine.NoteFocusChanged(true);

        Assert.Equal(AgentAttentionTier.Idle, machine.Snapshot().Tier);
    }

    [Fact]
    public void Focus_before_the_floor_does_not_clear_a_write()
    {
        var (machine, clock, _) = Make();

        machine.NoteWrote("sendInput");
        clock.Advance(TimeSpan.FromSeconds(9));
        machine.NoteFocusChanged(true);

        Assert.Equal(AgentAttentionTier.Wrote, machine.Snapshot().Tier);
    }

    [Fact]
    public void An_already_focused_pane_clears_the_write_when_the_floor_expires()
    {
        // The case focus events cannot cover: the agent typed into the pane the
        // user was already looking at, so no focus change will ever arrive.
        var (machine, clock, _) = Make();
        machine.NoteFocusChanged(true);

        machine.NoteWrote("sendInput");
        Assert.Equal(AgentAttentionTier.Wrote, machine.Snapshot().Tier);

        clock.Advance(TimeSpan.FromSeconds(10));
        machine.Tick();
        Assert.Equal(AgentAttentionTier.Idle, machine.Snapshot().Tier);
    }

    [Fact]
    public void An_unfocused_pane_holds_the_write_past_the_floor()
    {
        var (machine, clock, _) = Make();
        machine.NoteFocusChanged(false);

        machine.NoteWrote("sendInput");
        clock.Advance(TimeSpan.FromMinutes(2));
        machine.Tick();

        Assert.Equal(AgentAttentionTier.Wrote, machine.Snapshot().Tier);
    }

    [Fact]
    public void A_second_write_re_arms_a_cleared_one()
    {
        var (machine, clock, _) = Make();

        machine.NoteWrote("sendInput");
        clock.Advance(TimeSpan.FromSeconds(10));
        machine.NoteFocusChanged(true);
        Assert.Equal(AgentAttentionTier.Idle, machine.Snapshot().Tier);

        machine.NoteWrote("closeSession");
        var snapshot = machine.Snapshot();
        Assert.Equal(AgentAttentionTier.Wrote, snapshot.Tier);
        Assert.Equal("closeSession", snapshot.LastWriteMethod);
    }

    [Fact]
    public void Changed_fires_only_on_tier_transitions()
    {
        var (machine, _, changes) = Make();

        machine.NoteRead();          // Idle -> Watched
        machine.NoteRead();          // Watched -> Watched, no event
        machine.NoteWrote("sendInput"); // Watched -> Wrote

        Assert.Equal(2, changes.Count);
        Assert.Equal(AgentAttentionTier.Watched, changes[0].Tier);
        Assert.Equal(AgentAttentionTier.Wrote, changes[1].Tier);
    }

    [Fact]
    public void Last_write_timestamp_survives_acknowledgement()
    {
        // The pane still wants to render "agent typed - 12s ago" text after the
        // tier itself has gone quiet, so the timestamp must not be erased.
        var (machine, clock, _) = Make();
        var writeAt = clock.Now;

        machine.NoteWrote("sendInput");
        clock.Advance(TimeSpan.FromSeconds(10));
        machine.NoteFocusChanged(true);

        var snapshot = machine.Snapshot();
        Assert.Equal(AgentAttentionTier.Idle, snapshot.Tier);
        Assert.Equal(writeAt, snapshot.LastWriteUtc);
    }

    [Fact]
    public void Changed_events_are_delivered_in_generation_order_across_reentrant_calls()
    {
        // A handler that re-enters the machine must not cause events to be
        // delivered out of order. The drainer ensures exactly one thread
        // delivers events at a time, preserving global ordering even when
        // signals arrive from multiple threads (IPC, timer, UI).
        var (machine, _, changes) = Make();
        var readyToCapture = false;

        // Install a handler that re-enters the machine during event delivery
        machine.Changed += snapshot =>
        {
            if (readyToCapture && snapshot.Tier == AgentAttentionTier.Watched)
            {
                // Re-enter: while delivering the Watched event, trigger a transition to Wrote
                machine.NoteWrote("sendInput");
            }
        };

        readyToCapture = true;
        machine.NoteRead();

        // We should see exactly 2 events in order: Watched then Wrote.
        // Re-entrancy is safe because the handler runs outside the lock,
        // and the drainer's queue preserves the order.
        Assert.Equal(2, changes.Count);
        Assert.Equal(AgentAttentionTier.Watched, changes[0].Tier);
        Assert.Equal(AgentAttentionTier.Wrote, changes[1].Tier);
        Assert.Equal("sendInput", changes[1].LastWriteMethod);
    }
}

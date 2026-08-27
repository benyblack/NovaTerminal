using System;
using System.IO;
using Avalonia.Headless.XUnit;
using NovaTerminal.AgentHost;
using NovaTerminal.VT;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// The window half of the attention machines' focus signal.
///
/// Focus is what retires the sticky "agent typed" mark, and it is supposed to
/// mean "the user has plausibly seen it". A pane's IsActivePane only means
/// "selected inside the app" and stays true while the app is minimized or
/// behind another application, so MainWindow has to push its own
/// front-and-visible state and the registration has to AND the two.
///
/// The rule itself is covered without a window in
/// AgentAttentionRegistrationTests; this file covers the wiring — that the
/// window actually pushes, and that a pane registered later is seeded.
/// </summary>
public class AgentWindowVisibilityTests
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => Now += by;
        public Func<DateTimeOffset> Provider => () => Now;
    }

    [AvaloniaFact]
    public void Deactivating_the_window_stops_its_panes_writes_from_self_retiring()
    {
        // End to end through the real window: a registration in the real
        // registry, marked as the selected pane, with an injected clock so the
        // 10 s floor can actually be crossed.
        //
        // The registration is created here rather than taken from one of the
        // window's own panes because those get the production nowProvider
        // (UtcNow), and no headless test can wait out the floor.
        RunIsolated(window =>
        {
            var clock = new FakeClock();
            var registration = new AgentSessionRegistration(
                paneId: Guid.NewGuid(),
                buffer: new TerminalBuffer(80, 24),
                title: "pane",
                profileName: "Terminal",
                kind: "local",
                isActive: true,
                nowProvider: clock.Provider);

            Assert.True(AgentSessionRegistry.Instance.Register(registration));
            try
            {
                // Front window, selected pane: the write is acknowledged once
                // it clears the floor. This half is the control — it proves the
                // clock and the tick actually reach the machine, so the other
                // half is not passing for the wrong reason.
                window.SetAgentWindowActivatedForTesting(true);
                registration.AttentionMachine.NoteWrote("sendInput");
                clock.Advance(TimeSpan.FromSeconds(AgentAttentionMachine.WriteFloorSeconds * 2));
                registration.AttentionMachine.Tick();
                Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);

                // The user alt-tabs away. The pane is still selected — nothing
                // in the app clears that — so without the window push the tick
                // below would retire the mark with nobody looking.
                window.SetAgentWindowActivatedForTesting(false);
                registration.AttentionMachine.NoteWrote("sendInput");
                clock.Advance(TimeSpan.FromSeconds(AgentAttentionMachine.WriteFloorSeconds * 2));
                registration.AttentionMachine.Tick();
                Assert.Equal(AgentAttentionTier.Wrote, registration.AttentionMachine.Snapshot().Tier);

                // ...and it is still there when they come back, which is when
                // it is finally allowed to clear.
                window.SetAgentWindowActivatedForTesting(true);
                Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
            }
            finally
            {
                AgentSessionRegistry.Instance.Unregister(registration.PaneId);
            }
        });
    }

    [AvaloniaFact]
    public void Minimizing_the_window_stops_its_panes_writes_from_self_retiring()
    {
        // Minimizing is the second way the window stops being visible while
        // the selected pane stays selected, and it reaches the code by a
        // different route: the WindowState property notification rather than
        // the Deactivated event. On Windows minimizing usually deactivates too,
        // but that is a platform courtesy rather than a guarantee, and unlike
        // activation this route is drivable headlessly — so it is the one place
        // a test can exercise the real production wiring end to end instead of
        // the SetAgentWindowActivatedForTesting seam.
        RunIsolated(window =>
        {
            var clock = new FakeClock();
            var registration = new AgentSessionRegistration(
                paneId: Guid.NewGuid(),
                buffer: new TerminalBuffer(80, 24),
                title: "pane",
                profileName: "Terminal",
                kind: "local",
                isActive: true,
                nowProvider: clock.Provider);

            window.SetAgentWindowActivatedForTesting(true);
            Assert.True(AgentSessionRegistry.Instance.Register(registration));
            try
            {
                window.WindowState = Avalonia.Controls.WindowState.Minimized;

                registration.AttentionMachine.NoteWrote("sendInput");
                clock.Advance(TimeSpan.FromSeconds(AgentAttentionMachine.WriteFloorSeconds * 2));
                registration.AttentionMachine.Tick();
                Assert.Equal(AgentAttentionTier.Wrote, registration.AttentionMachine.Snapshot().Tier);

                // Restoring the window is the user coming back to look at it,
                // so the mark is allowed to clear.
                window.WindowState = Avalonia.Controls.WindowState.Normal;
                Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
            }
            finally
            {
                AgentSessionRegistry.Instance.Unregister(registration.PaneId);
            }
        });
    }

    [AvaloniaFact]
    public void A_pane_that_registers_while_the_window_is_away_is_seeded_not_assumed_visible()
    {
        // Registrations default to "window visible" so that the hundreds of
        // tests which build one directly keep their old behaviour. That default
        // must never survive into a live window: session restore registers
        // panes before the window is ever activated, and an agent can split a
        // pane while the user is in another application. MainWindow seeds the
        // window half from its SessionRegistered handler.
        RunIsolated(window =>
        {
            window.SetAgentWindowActivatedForTesting(false);

            var clock = new FakeClock();
            var registration = new AgentSessionRegistration(
                paneId: Guid.NewGuid(),
                buffer: new TerminalBuffer(80, 24),
                title: "pane",
                profileName: "Terminal",
                kind: "local",
                isActive: true,
                nowProvider: clock.Provider);

            Assert.True(AgentSessionRegistry.Instance.Register(registration));
            try
            {
                registration.AttentionMachine.NoteWrote("sendInput");
                clock.Advance(TimeSpan.FromSeconds(AgentAttentionMachine.WriteFloorSeconds * 2));
                registration.AttentionMachine.Tick();

                Assert.Equal(AgentAttentionTier.Wrote, registration.AttentionMachine.Snapshot().Tier);
            }
            finally
            {
                AgentSessionRegistry.Instance.Unregister(registration.PaneId);
            }
        });
    }

    /// <summary>
    /// TestMainWindowFactory.Create() runs the real MainWindow constructor,
    /// which loads the on-disk settings.json and calls
    /// AgentHostService.Instance.Apply(...). On a machine with observe
    /// persisted as enabled that would start a real named-pipe/Unix-socket
    /// accept loop inside this shared test process. Point NOVATERM_APPDATA_ROOT
    /// at a fresh empty directory so Load() always yields defaults and Apply()
    /// takes its no-op Stop() path. Same pattern as
    /// AgentObserveIndicatorTests.RunIsolated.
    /// </summary>
    private static void RunIsolated(Action<MainWindow> body)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"novaterm_agent_window_visibility_test_{Guid.NewGuid():N}");
        string? previousRoot = Environment.GetEnvironmentVariable("NOVATERM_APPDATA_ROOT");
        Directory.CreateDirectory(tempRoot);

        try
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", tempRoot);

            var window = TestMainWindowFactory.Create();
            window.Show();
            body(window);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", previousRoot);
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }
}

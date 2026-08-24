using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// The window-level agent light. It is a permission indicator first — visible
/// exactly while observe is enabled — with two activity states layered on,
/// because it is the only surface at the right scope for them: a waitForEvents
/// long poll names no pane, and a read landing on a pane that carries no agent
/// segment of its own would otherwise be invisible everywhere.
/// </summary>
public class AgentObserveIndicatorTests
{
    [Theory]
    // Observe off: invisible, and nothing else matters.
    [InlineData(false, false, false, false, false)]
    [InlineData(false, true, true, false, false)]
    // Observe on, nothing happening: visible but quiet.
    [InlineData(true, false, false, true, false)]
    // A long poll is parked: active, because the subscription names no pane and
    // this is the only surface for it.
    [InlineData(true, true, false, true, true)]
    // A pane with no bar of its own is being read: active. This is the only
    // place that read can appear.
    [InlineData(true, false, true, true, true)]
    // Both at once: still just active.
    [InlineData(true, true, true, true, true)]
    public void Observe_indicator_state(
        bool observeRunning, bool polling, bool anyUnmarkedPaneWatched,
        bool expectedVisible, bool expectedActive)
    {
        var (visible, active) = MainWindow.ComputeObserveIndicatorState(
            observeRunning, polling, anyUnmarkedPaneWatched);

        Assert.Equal(expectedVisible, visible);
        Assert.Equal(expectedActive, active);
    }

    [Fact]
    public void A_read_of_an_unmarked_pane_lights_the_indicator_even_with_act_on()
    {
        // The bug this pins: the condition used to be "act is off", justified by
        // "with act off, no pane carries a bar". But act being *on* does not put
        // a bar on every pane — an SSH pane whose profile lacks AllowAgentAccess
        // is not actable, so it has no bar, no tab glyph under the default
        // WritesOnly rollup, and previously no window light either. Reading such
        // a pane produced no live signal anywhere, and those are precisely the
        // panes the user deliberately excluded from act.
        //
        // The act toggle is no longer an input at all, which is the fix: the
        // decision is about the *watched pane*, not the global permission.
        var (visible, active) = MainWindow.ComputeObserveIndicatorState(
            observeRunning: true, polling: false, anyUnmarkedPaneWatched: true);

        Assert.True(visible);
        Assert.True(active);
    }

    [Fact]
    public void A_read_of_a_pane_that_carries_its_own_bar_does_not_light_the_indicator()
    {
        // The other half: an actable pane already shows "agent reading" on its
        // own status bar, so lighting the window light too would double-report.
        var (visible, active) = MainWindow.ComputeObserveIndicatorState(
            observeRunning: true, polling: false, anyUnmarkedPaneWatched: false);

        Assert.True(visible);
        Assert.False(active);
    }

    [AvaloniaFact]
    public void The_indicator_control_exists_and_starts_hidden()
    {
        // Wiring only: the decision itself is covered by the theory above, and
        // this must not touch AgentHostService.Instance directly. But
        // TestMainWindowFactory.Create() runs the real MainWindow constructor,
        // which loads the real on-disk settings.json and then calls
        // AgentHostService.Instance.Apply(settings.AgentAccessObserveEnabled) -
        // transitively reaching the exact singleton this test must avoid. On a
        // machine where that setting is persisted as enabled (e.g. anyone who
        // has exercised this feature for real), Apply(true) would start a real
        // named-pipe/Unix-socket accept loop inside this shared test process.
        // Point NOVATERM_APPDATA_ROOT at a fresh, empty scratch directory for
        // the duration of the test so TerminalSettings.Load() always yields
        // defaults (observe disabled) and Apply() takes its no-op Stop() path,
        // regardless of what is persisted on the machine running the test.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"novaterm_observe_indicator_test_{Guid.NewGuid():N}");
        string? previousRoot = Environment.GetEnvironmentVariable("NOVATERM_APPDATA_ROOT");
        Directory.CreateDirectory(tempRoot);

        try
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", tempRoot);

            var window = TestMainWindowFactory.Create();
            window.Show();

            var indicator = window.FindControl<Button>("AgentObserveIndicator");

            Assert.NotNull(indicator);
            Assert.False(indicator!.IsVisible);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", previousRoot);
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }
}

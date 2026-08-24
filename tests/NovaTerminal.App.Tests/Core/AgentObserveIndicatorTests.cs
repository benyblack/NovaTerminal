using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// The window-level agent light. It is a permission indicator first — visible
/// exactly while observe is enabled — with two activity states layered on,
/// because it is the only surface at the right scope for them: a waitForEvents
/// long poll names no pane, and in observe-only mode no pane carries a status
/// bar, so reads would otherwise be invisible everywhere.
/// </summary>
public class AgentObserveIndicatorTests
{
    [Theory]
    // Observe off: invisible, and nothing else matters.
    [InlineData(false, false, false, false, false, false)]
    [InlineData(false, true, true, true, false, false)]
    // Observe on, nothing happening: visible but quiet.
    [InlineData(true, false, false, false, true, false)]
    // A long poll is parked: active regardless of the act toggle, because the
    // subscription names no pane and this is the only surface for it.
    [InlineData(true, false, true, false, true, true)]
    [InlineData(true, true, true, false, true, true)]
    // Observe-only (act off) and some pane is being read: active, because no
    // pane carries a status bar in that mode.
    [InlineData(true, false, false, true, true, true)]
    // Act on, so panes carry their own bars: a pane read does NOT drive the
    // window light, which would double-report it.
    [InlineData(true, true, false, true, true, false)]
    public void Observe_indicator_state(
        bool observeRunning, bool actEnabled, bool polling, bool anyPaneWatched,
        bool expectedVisible, bool expectedActive)
    {
        var (visible, active) = MainWindow.ComputeObserveIndicatorState(
            observeRunning, actEnabled, polling, anyPaneWatched);

        Assert.Equal(expectedVisible, visible);
        Assert.Equal(expectedActive, active);
    }

    [AvaloniaFact]
    public void The_indicator_control_exists_and_starts_hidden()
    {
        // Wiring only: the decision itself is covered by the theory above, and
        // this must not touch AgentHostService.Instance.
        var window = TestMainWindowFactory.Create();
        window.Show();

        var indicator = window.FindControl<Button>("AgentObserveIndicator");

        Assert.NotNull(indicator);
        Assert.False(indicator!.IsVisible);
    }
}

using NovaTerminal.Controls;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// A full-screen process monitor on the alternate screen. htop, btop and top are all missing on
/// the capture machine (checked directly), so this runs Assets/demo-monitor.sh - a fabricated,
/// literal-printf stand-in styled after htop - rather than falling back to `ps aux`, which would
/// leak the capture machine's real uid/gid, TTYs and PIDs, or to `less`, which shows a pager
/// rather than a monitor. See demo-monitor.sh's own header for what it draws and why nothing in
/// it is machine-derived.
/// </summary>
internal sealed class TuiHtopScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "tui-htop",
        Tier: 2,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "A full-screen fabricated process monitor, styled after htop (unavailable on the " +
                "capture machine), on the alternate screen: per-core and memory gauges above a " +
                "process table that fills the screen, and no shell prompt visible.");

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane pane = context.OpenTab(context.World.DemoProfile);
        await context.RunCommandAsync(pane, "bash scripts/demo-monitor.sh");

        context.Driver.WaitFor(
            () => pane.Buffer!.IsAltScreenActive,
            TimeSpan.FromSeconds(20),
            "demo-monitor.sh to switch to the alternate screen");

        context.Capture();

        // demo-monitor.sh parks reading one key at a time until it sees 'q', then restores the
        // primary screen itself - the same alternate-screen exit path a real curses monitor uses.
        pane.Session!.SendInput("q");

        context.Driver.WaitFor(
            () => !pane.Buffer!.IsAltScreenActive,
            TimeSpan.FromSeconds(10),
            "demo-monitor.sh to restore the primary screen after 'q'");
    }
}

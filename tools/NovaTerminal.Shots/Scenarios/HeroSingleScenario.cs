using NovaTerminal.Controls;

namespace NovaTerminal.Shots.Scenarios;

internal sealed class HeroSingleScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "hero-single",
        Tier: 1,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "A single calm pane showing the Nova banner, a short git status, and a passing " +
                "test run. No overlays open, no empty space below the prompt, colours clearly visible.");

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane pane = context.OpenTab(context.World.DemoProfile);

        await context.RunCommandAsync(pane, "clear");
        await context.RunCommandAsync(pane, "bash scripts/nova-banner.sh");
        await context.RunCommandAsync(pane, "git status --short --branch");
        await context.RunCommandAsync(pane, "bash scripts/demo-test.sh");

        context.Capture();
    }
}

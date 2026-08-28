using NovaTerminal.Controls;

namespace NovaTerminal.Shots.Scenarios;

internal sealed class HeroSingleScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "hero-single",
        Tier: 1,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        // Written as things a reader can check off the finished PNG. An Intent that a correct
        // image cannot satisfy is worse than a vague one: it is the sentence /shots judges every
        // capture against, and one that always fails teaches the review loop to ignore the gate.
        // The earlier wording asked for "no empty space below the prompt", which this scenario
        // cannot give - after `clear` the transcript is written from the top and ends on a fresh
        // prompt, so the rows beneath it are empty by construction, and the only ways to fill
        // them are to overflow the pane (scrolling the banner away, which the same sentence
        // forbids) or to pin an exact row count, which varies with window size and font metrics.
        Intent: "A single tab whose transcript runs from the first prompt to within a few rows of " +
                "the pane's bottom edge: the Nova banner in colour at the top, a short git status, " +
                "and a passing test run, ending on a fresh prompt. No overlays open.");

    public async Task RunAsync(ShotContext context)
    {
        await PlayAsync(context);
        context.Capture();
    }

    /// <summary>
    /// The command sequence alone, without capturing. Reused by ThemesGridScenario's multi-pass
    /// composer in Program, which needs this exact transcript rendered once per theme but must
    /// capture (and not record in the manifest) each pass itself rather than getting five
    /// separate PNGs out of <see cref="ShotContext.Capture"/>.
    /// </summary>
    internal static async Task PlayAsync(ShotContext context)
    {
        TerminalPane pane = context.OpenTab(context.World.DemoProfile);

        await context.RunCommandAsync(pane, "clear");
        await context.RunCommandAsync(pane, "bash scripts/nova-banner.sh");
        await context.RunCommandAsync(pane, "git status --short --branch");
        await context.RunCommandAsync(pane, "bash scripts/demo-test.sh");
    }
}

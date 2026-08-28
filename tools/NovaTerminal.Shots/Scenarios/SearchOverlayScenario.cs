using Avalonia.Controls;
using NovaTerminal.Controls;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// The pane-level search overlay open over a commit-log scrollback, mid-search: a term typed, a
/// match position reported against a larger total, and the matches themselves highlighted in the
/// buffer behind the panel.
/// </summary>
internal sealed class SearchOverlayScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "search-overlay",
        Tier: 2,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "The search panel open over scrollback with a term typed, the match counter showing " +
                "a position within a larger total, and matches highlighted in the buffer behind it.");

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane pane = context.OpenTab(context.World.DemoProfile);
        await context.RunCommandAsync(pane, "clear");
        await context.RunCommandAsync(pane, "git log --oneline -40");

        // Opens the pane-level SearchPanel (TerminalPane.ToggleSearch), not MainWindow's own
        // same-named control - see RequireIn's remarks for why the lookup below has to be scoped
        // to the pane rather than the window.
        pane.ToggleSearch();
        context.Driver.Pump(3);

        // "decoder" matches several of SeedWorkspace's scripted commit subjects (the initial
        // decoder-skeleton commit, the docs commit, the merge commit's "into feat/sixel-decoder",
        // and the golden-fixtures commit) against a ~10-commit history - a real "N of M" position
        // rather than a single isolated hit.
        context.Driver.TypeText("decoder");
        context.Driver.Pump(5);

        context.Driver.WaitFor(
            () => context.Driver.RequireIn<TextBlock>(pane, "SearchCount").Text is { Length: > 0 } text
                  && !text.StartsWith("0/", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5),
            "the search counter to report at least one match");

        context.Capture();
    }
}

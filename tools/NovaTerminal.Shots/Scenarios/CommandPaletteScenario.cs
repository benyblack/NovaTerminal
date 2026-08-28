using Avalonia.Controls;
using NovaTerminal.Controls;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// The command palette open over a populated terminal, with a query already narrowing the list —
/// the moment a user sees between typing a search term and picking a command.
/// </summary>
internal sealed class CommandPaletteScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "command-palette",
        Tier: 1,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "The command palette open over a populated terminal, a query typed in the box, and " +
                "several matching commands listed with their keyboard shortcuts on the right.");

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane pane = context.OpenTab(context.World.DemoProfile);
        await context.RunCommandAsync(pane, "clear");
        await context.RunCommandAsync(pane, "bash scripts/nova-banner.sh");

        context.Driver.InvokePrivate(context.Window, "ToggleCommandPalette");

        // "split" only ever matches two commands (Split Horizontal, Split Vertical) - real
        // filtering, but not enough rows to show what "several matching commands" in the Intent
        // means. "tab" matches over a dozen (New Tab, Close Tab, Tab: Next (MRU), Tabs: Toggle
        // Vertical Tab Sidebar, ...), all with real shortcuts, so the list actually reads as a
        // filtered palette rather than two isolated hits.
        context.Driver.TypeText("tab");
        context.Driver.Pump(5);

        context.Driver.WaitFor(
            () => context.Driver.Require<ListBox>("CommandList").ItemCount >= 5,
            TimeSpan.FromSeconds(5),
            "the palette to filter to several commands");

        context.Capture();
    }
}

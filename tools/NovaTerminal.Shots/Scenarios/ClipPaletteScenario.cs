using Avalonia.Controls;
using NovaTerminal.Controls;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// A short clip of the command palette filtering live: the demo banner already on screen, then
/// the palette opens and a query is typed one character at a time, the real-time filtering
/// narrowing the result list (and, once the query stops matching anything, emptying it) as each
/// character lands - the moment <see cref="CommandPaletteScenario"/>'s still only shows the end
/// state of.
/// </summary>
internal sealed class ClipPaletteScenario : IScenario
{
    private const int Fps = 20;

    /// <summary>
    /// Frames held on the settled "before" pane, so the clip has a beat to open on. Matches
    /// <see cref="ClipAgentScenario"/>'s own pre-roll (6 frames) rather than padding further: a
    /// long static hold before the real motion starts only dilutes the distinct-frame ratio
    /// without showing anything new - see this project's frame-diversity guidance.
    /// </summary>
    private const int PreRollHoldFrames = 6;

    /// <summary>Frames held on the final filtered (or emptied) list before recording stops.</summary>
    private const int FinalHoldFrames = 10;

    /// <summary>
    /// Matches "Split Vertical"/"Split Horizontal" through "split " (CommandRegistry.Search is a
    /// plain case-insensitive substring match over each command's Title/Category - see
    /// Shell/CommandRegistry.cs), then stops matching anything the moment "p" follows: neither
    /// title's seventh character is 'p'. That emptied list is real behaviour, not a bug in this
    /// scenario - see this class's own remarks - and is left in rather than swapped for a query
    /// that stays matched throughout, because the clip's job is to show the palette actually
    /// filtering as the user types, not to stage a query that always succeeds.
    /// </summary>
    private const string TypedQuery = "split pane";

    public ShotSpec Spec { get; } = new(
        Name: "clip-palette",
        Tier: 4,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "A short clip of the main window: the demo banner already on screen, then the " +
                "command palette opens and 'split pane' is typed one character at a time - the " +
                "result list narrows to the two Split commands as 'split' lands, then empties once " +
                "the query no longer matches any command title.");

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane pane = context.OpenTab(context.World.DemoProfile);
        await context.RunCommandAsync(pane, "bash scripts/nova-banner.sh");

        await context.RecordAsync(async () =>
        {
            // A beat on the settled "before" pane, deliberately: with no pre-roll the clip would
            // open mid-query, and a viewer scrubbing back to frame zero would see the palette
            // already open.
            for (int i = 0; i < PreRollHoldFrames; i++)
            {
                context.Recorder!.CaptureFrame();
            }

            context.Driver.InvokePrivate(context.Window, "ToggleCommandPalette");

            TextBox searchBox = context.Driver.Require<TextBox>("CommandSearchBox");
            if (!searchBox.IsVisible)
            {
                throw new InvalidOperationException(
                    "ToggleCommandPalette did not show the command search box, so this clip would " +
                    "not be showing a real palette opening.");
            }

            string typedSoFar = string.Empty;
            foreach (char c in TypedQuery)
            {
                context.Driver.TypeText(c.ToString());
                typedSoFar += c;

                // Waited for, not just pumped a fixed number of times: MainWindow re-filters the
                // list from a Text PropertyChanged handler (InitializeCommandPaletteUI), and a
                // fixed pump count that merely happens to be enough on this machine is not the
                // same guarantee as actually observing that this character's own text landed
                // before the frame is captured.
                context.Driver.WaitFor(
                    () => string.Equals(searchBox.Text, typedSoFar, StringComparison.Ordinal),
                    TimeSpan.FromSeconds(2),
                    $"the search box to actually show '{typedSoFar}'");

                context.Recorder!.CaptureFrame();
                context.Driver.Pump(1);
                context.Recorder!.CaptureFrame();
            }

            // A closing beat on the final query state before recording stops.
            for (int i = 0; i < FinalHoldFrames; i++)
            {
                context.Driver.Pump(1);
                context.Recorder!.CaptureFrame();
            }

            await Task.CompletedTask;
        }, Fps);

        context.Capture();
    }
}

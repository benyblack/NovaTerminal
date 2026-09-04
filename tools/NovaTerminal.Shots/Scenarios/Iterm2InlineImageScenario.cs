using NovaTerminal.Controls;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// An image decoded and rendered inline in the terminal via the iTerm2 image protocol.
/// </summary>
/// <remarks>
/// <para>
/// <b>Formerly deferred; both blockers are now fixed.</b> This scenario spent a long time
/// implemented but unregistered, for two separate reasons in sequence.
/// </para>
/// <para>
/// First, nothing under <c>src/</c> assigned <see cref="NovaTerminal.VT.AnsiParser.ImageDecoder"/>,
/// so the parser consumed the escape sequence correctly and then dropped the picture - a plain
/// build rendered nothing. An earlier version of this harness papered over that by injecting its
/// own decoder, which made the screenshot advertise a capability no shipped build had; that
/// injection was removed rather than kept. Production now wires a real decoder
/// (<c>TerminalPane.CreateAndWireParser</c> assigns <c>SkiaImageDecoder</c>), so the picture here
/// is decoded by the same code a user runs.
/// </para>
/// <para>
/// Second, the cursor was not returned to column 0 once an image was placed, so the shell prompt
/// after it resumed mid-row - an indent for sixel, and for the narrower iTerm2 logo an overrun
/// past the last column that wrapped the prompt mid-word. That is fixed too (#405), along with the
/// follow-on where the post-image newline swallow leaked past other output and ate a later line
/// break.
/// </para>
/// <para>
/// <see cref="InlineImageDecoding"/> asserts both properties on every run - a genuinely decoded
/// region, and text resuming at column 0 - so neither blocker can return unnoticed.
/// </para>
/// </remarks>
internal sealed class Iterm2InlineImageScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "iterm2-inline-image",
        Tier: 2,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "The Nova logo rendered inline in the terminal via the iTerm2 image protocol, " +
                "sitting between two shell prompts, sharp and correctly positioned relative to " +
                "the surrounding text.");

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane pane = context.OpenTab(context.World.DemoProfile);

        await context.RunCommandAsync(pane, "clear");
        await context.RunCommandAsync(pane, "echo 'iterm2 inline image · nova-logo.png'");
        await context.RunCommandAsync(pane, "bash scripts/imgcat.sh assets/nova-logo.png");
        await context.RunCommandAsync(pane, "echo done");

        InlineImageDecoding.AssertImageRegionDecoded(context, pane, "iterm2-inline-image");

        context.Capture();
    }
}

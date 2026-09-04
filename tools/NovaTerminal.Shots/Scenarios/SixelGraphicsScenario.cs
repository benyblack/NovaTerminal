using NovaTerminal.Controls;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// An image decoded and rendered inline in the terminal via sixel.
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
/// <para>
/// The OSC 1339 tunnel the committed asset uses is unrelated to either fix and still matters: on
/// Windows a raw <c>ESC P ... q ... ST</c> DCS sixel sent through ConPTY never reaches AnsiParser
/// at all, which docs/IMAGE_PROTOCOL_SUPPORT.md documents for this platform. The image bytes and
/// the decoder invoked on them are identical either way; only the transport envelope differs.
/// </para>
/// </remarks>
internal sealed class SixelGraphicsScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "sixel-graphics",
        Tier: 2,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "A sixel image rendered inline in the terminal, sitting between two shell prompts, " +
                "with the image sharp and correctly positioned relative to the surrounding text.");

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane pane = context.OpenTab(context.World.DemoProfile);

        await context.RunCommandAsync(pane, "clear");
        await context.RunCommandAsync(pane, "echo 'sixel decode · 480x320'");
        await context.RunCommandAsync(pane, "cat assets/plot.sixel");
        await context.RunCommandAsync(pane, "echo done");

        InlineImageDecoding.AssertImageRegionDecoded(context, pane, "sixel-graphics");

        context.Capture();
    }
}

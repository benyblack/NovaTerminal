using NovaTerminal.Controls;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// A pre-generated sixel image (see Assets/plot.sixel) decoded and drawn inline by NovaTerminal's
/// own sixel handling. See <see cref="InlineImageDecoding"/>'s remarks for why this scenario
/// wires its own <c>IImageDecoder</c> before running the command that emits the image.
/// </summary>
/// <remarks>
/// <para>
/// Assets/plot.sixel's provenance (img2sixel and gnuplot are both absent from this machine, so
/// this uses the ImageMagick 7.1.2 that is present): a 480x320 dark-themed "throughput" line
/// chart PNG was drawn with a series of ImageMagick <c>-draw</c> calls (grid lines, two coloured
/// polylines, a legend - all fabricated numbers, no real measurement), then
/// <c>magick plot.png -depth 8 -colors 64 plot-final.png</c> to shrink the palette, then
/// <c>magick plot-final.png sixel:plot.sixel</c> to produce a genuine DCS sixel stream - confirmed
/// by its own header bytes, <c>ESC P 0;0;0 q "1;1;480;320 #0;2;...</c>.
/// </para>
/// <para>
/// The committed file is that same stream re-wrapped as an OSC 1339 tunnel (<c>ESC ] 1339;
/// &lt;identical sixel body&gt; ESC \</c>) rather than left as a native DCS sequence. On this
/// machine (Windows, ConPTY) a raw <c>ESC P ... q ... ST</c> DCS sixel sent through a live PTY
/// never reached AnsiParser at all - no exception, no image, nothing in
/// <c>TerminalBuffer.Images</c> - which matches docs/IMAGE_PROTOCOL_SUPPORT.md's own documented
/// caveat for this platform ("DCS Sixel... May be filtered by host PTY; use OSC 1339 tunnel when
/// available") and AnsiParser.cs's existing <c>HandleOsc</c> tunnel support, which unwraps
/// <c>1339;...</c> and hands the exact same payload to <c>HandleSixel</c>. The image bytes and
/// the decoder invoked on them are unchanged either way; only the transport envelope differs, to
/// actually survive this platform's PTY. See the Task 13 report for the before/after evidence.
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
        InlineImageDecoding.EnableRealDecoding(pane);

        await context.RunCommandAsync(pane, "clear");
        await context.RunCommandAsync(pane, "echo 'sixel decode · 480x320'");
        await context.RunCommandAsync(pane, "cat assets/plot.sixel");
        await context.RunCommandAsync(pane, "echo done");

        InlineImageDecoding.AssertImageRegionDecoded(context, pane, "sixel-graphics");

        context.Capture();
    }
}

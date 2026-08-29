using NovaTerminal.Controls;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// The Nova logo (see Assets/nova-logo.png), emitted as an iTerm2 OSC 1337 <c>File=</c> inline
/// image by <c>scripts/imgcat.sh</c> and decoded by NovaTerminal's own handling of that sequence.
/// See <see cref="InlineImageDecoding"/>'s remarks for why this scenario wires its own
/// <c>IImageDecoder</c> before running the command that emits the image.
/// </summary>
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
        InlineImageDecoding.EnableRealDecoding(pane);

        await context.RunCommandAsync(pane, "clear");
        await context.RunCommandAsync(pane, "echo 'iterm2 inline image · nova-logo.png'");
        await context.RunCommandAsync(pane, "bash scripts/imgcat.sh assets/nova-logo.png");
        await context.RunCommandAsync(pane, "echo done");

        InlineImageDecoding.AssertImageRegionDecoded(context, pane, "iterm2-inline-image");

        context.Capture();
    }
}

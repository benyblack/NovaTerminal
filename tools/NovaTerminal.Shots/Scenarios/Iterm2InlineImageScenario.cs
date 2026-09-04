using NovaTerminal.Controls;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// DEFERRED - unregistered from <see cref="ScenarioCatalog"/>, not deleted. Do not re-register
/// without first re-reading this comment and confirming its premise no longer holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>The original blocker is gone.</b> This scenario was deferred because nothing under
/// <c>src/</c> implemented or wired <see cref="NovaTerminal.VT.IImageDecoder"/>, so
/// <c>AnsiParser.HandleITerm2Image</c> always hit its <c>if (ImageDecoder == null) return;</c>
/// guard and a plain build parsed the OSC 1337 <c>File=</c> framing and then dropped the picture.
/// That was fixed upstream: <c>TerminalPane.CreateAndWireParser</c> now assigns
/// <c>Parser.ImageDecoder = new NovaTerminal.Rendering.SkiaImageDecoder()</c>. Run today, this
/// scenario renders the logo for real and
/// <see cref="InlineImageDecoding.AssertImageRegionDecoded"/> passes on genuine pixels.
/// </para>
/// <para>
/// <b>What still blocks it:</b> the cursor is not returned to column 0 once the image is placed.
/// Here that is worse than sixel's indent - the prompt resumes at column 74 of a 116-column pane,
/// on the image's own last row, then overruns the right edge and wraps, splitting
/// "(feat/sixel-decoder)" across two lines. The Intent asks for the image "correctly positioned
/// relative to the surrounding text"; this is the opposite.
/// <see cref="InlineImageDecoding.AssertTextResumesAtColumnZero"/> fails on it rather than letting
/// a mangled image through.
/// </para>
/// <para>
/// <b>To re-enable:</b> once <c>src/</c> leaves the cursor at column 0 after an image, add
/// <c>new Iterm2InlineImageScenario()</c> back to <c>ScenarioCatalog</c>'s list. No other code
/// change is required - the asset (<c>Assets/nova-logo.png</c>) and <c>scripts/imgcat.sh</c> remain
/// valid as-is.
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

using NovaTerminal.Controls;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// DEFERRED — unregistered from <see cref="ScenarioCatalog"/>, not deleted. Do not re-register
/// without first re-reading this comment and confirming its premise no longer holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is missing:</b> no production code anywhere under <c>src/</c> implements or wires
/// <see cref="NovaTerminal.VT.IImageDecoder"/>. <c>TerminalPane.CreateAndWireParser</c>
/// (<c>src/NovaTerminal.App/TerminalPane.axaml.cs:2806</c>) constructs a bare
/// <c>new AnsiParser(Buffer)</c> and never assigns <c>Parser.ImageDecoder</c>. As a result,
/// <c>AnsiParser.HandleITerm2Image</c> (<c>src/NovaTerminal.VT/AnsiParser.cs:2720</c>) always
/// hits its <c>if (ImageDecoder == null) return;</c> guard and never calls
/// <c>TerminalBuffer.AddImage</c>. (The Kitty graphics protocol handler at
/// <c>AnsiParser.cs:2557</c> has the identical guard, for the same reason, though no scenario in
/// this catalogue exercises it.) A plain NovaTerminal build parses the OSC 1337 <c>File=</c>
/// framing and its base64 payload correctly and then silently drops the picture — nothing
/// renders.
/// </para>
/// <para>
/// This scenario previously passed only because the harness assigned its own
/// <c>IImageDecoder</c> onto the pane's parser before running — see
/// <see cref="InlineImageDecoding"/>'s remarks for why that was removed: it made the screenshot
/// demonstrate a capability no shipped build has.
/// </para>
/// <para>
/// <b>To re-enable:</b> once <c>src/</c> gets a real <c>IImageDecoder</c> implementation wired
/// into <c>TerminalPane.CreateAndWireParser</c> (a <c>src/</c> change, out of scope for the shots
/// harness), this scenario needs no code change at all — just add
/// <c>new Iterm2InlineImageScenario()</c> back to <c>ScenarioCatalog</c>'s list. The asset
/// (<c>Assets/nova-logo.png</c>) and <c>scripts/imgcat.sh</c> remain valid as-is.
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

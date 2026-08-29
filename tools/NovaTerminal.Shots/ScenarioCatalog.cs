using NovaTerminal.Shots.Scenarios;

namespace NovaTerminal.Shots;

public static class ScenarioCatalog
{
    // SixelGraphicsScenario and Iterm2InlineImageScenario are deliberately NOT registered here.
    // Both are implemented, their assets and scripts/imgcat.sh are still seeded by DemoWorld, and
    // their region-scoped verification (InlineImageDecoding.AssertImageRegionDecoded) still
    // exists — but no production code under src/ implements or wires an IImageDecoder onto
    // AnsiParser (TerminalPane.CreateAndWireParser constructs a bare `new AnsiParser(Buffer)`;
    // see AnsiParser.cs's HandleSixel/HandleITerm2Image null-decoder guards), so a plain build
    // decodes neither protocol. An earlier version of this harness worked around that by
    // injecting its own IImageDecoder before each scenario ran, which made the screenshots
    // demonstrate a capability no shipped build has — see InlineImageDecoding.cs's remarks and
    // each scenario's own header comment for the full evidence trail. Re-enable by adding
    // `new SixelGraphicsScenario()` and `new Iterm2InlineImageScenario()` back to this list once
    // src/ wires a real decoder — no other code change is required.
    private static readonly IScenario[] Scenarios =
    [
        new HeroSingleScenario(),
        new HeroSplitScenario(),
        new TabsVerticalScenario(),
        new CommandPaletteScenario(),
        new SettingsAgentAccessScenario(),
        new AgentSessionScenario(),
        new ClipAgentScenario(),
        new ThemesGridScenario(),
        new SearchOverlayScenario(),
        new TuiVimScenario(),
        new TuiMonitorScenario()
    ];

    public static IReadOnlyList<IScenario> All() => Scenarios;

    public static IScenario? Find(string name) =>
        Scenarios.FirstOrDefault(s => string.Equals(s.Spec.Name, name, StringComparison.OrdinalIgnoreCase));
}

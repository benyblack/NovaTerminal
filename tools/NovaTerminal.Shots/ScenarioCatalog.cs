using NovaTerminal.Shots.Scenarios;

namespace NovaTerminal.Shots;

public static class ScenarioCatalog
{
    // SixelGraphicsScenario and Iterm2InlineImageScenario are deliberately NOT registered here.
    // Both are implemented and both now decode: the original blocker - no production IImageDecoder
    // anywhere under src/ - was closed upstream, and TerminalPane.CreateAndWireParser now assigns
    // `Parser.ImageDecoder = new SkiaImageDecoder()`. Registered and run, each produces a real
    // decoded picture on screen and AssertImageRegionDecoded passes.
    //
    // What still blocks them is a different, narrower defect: the cursor is not returned to column 0
    // after an image is placed, so the shell's next prompt resumes partway across a row. sixel-graphics
    // indents it to column 8; iterm2-inline-image resumes at column 74 of a 116-column pane, which
    // overruns the last column and wraps "(feat/sixel-decoder)" mid-word. Both Intents require the
    // image "correctly positioned relative to the surrounding text", and neither image is publishable
    // while the text around it is mangled - so they stay out of the catalogue rather than shipping a
    // picture that advertises broken layout.
    //
    // InlineImageDecoding.AssertTextResumesAtColumnZero now fails on exactly that, so re-registering
    // them before the cursor defect is fixed produces a loud failure rather than a published image
    // nobody looked at twice. Once src/ returns the cursor to column 0, re-enable by adding
    // `new SixelGraphicsScenario()` and `new Iterm2InlineImageScenario()` back to this list - still
    // no other code change required.
    //
    // ConnectionManagerScenario and RemoteFilesScenario (Task 14) are likewise implemented but
    // deliberately NOT registered here. connection-manager's data path
    // (SshConnectionService/JsonSshProfileStore) bypasses AppPaths' NOVATERM_APPDATA_ROOT sandbox
    // entirely and was empirically confirmed to read and write a real, unsandboxed per-machine SSH
    // profile file — see ConnectionManagerScenario's header comment for the file:line evidence and
    // what a src/ fix would look like. remote-files requires a genuinely connected native SSH
    // session, which this offline harness has no way to provide — see RemoteFilesScenario's header
    // comment.
    private static readonly IScenario[] Scenarios =
    [
        new HeroSingleScenario(),
        new HeroSplitScenario(),
        new TabsVerticalScenario(),
        new CommandPaletteScenario(),
        new SettingsAgentAccessScenario(),
        new SettingsAppearanceScenario(),
        new AgentSessionScenario(),
        new ClipAgentScenario(),
        new ThemesGridScenario(),
        new SearchOverlayScenario(),
        new TuiVimScenario(),
        new TuiMonitorScenario(),
        new CommandAssistScenario(),
        new ClipPaletteScenario(),
        new ClipSplitScenario(),
        new ClipTuiScenario()
    ];

    public static IReadOnlyList<IScenario> All() => Scenarios;

    public static IScenario? Find(string name) =>
        Scenarios.FirstOrDefault(s => string.Equals(s.Spec.Name, name, StringComparison.OrdinalIgnoreCase));
}

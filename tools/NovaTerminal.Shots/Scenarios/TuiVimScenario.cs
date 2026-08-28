using NovaTerminal.Controls;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// A real vim session editing the fabricated Rust source, on the real alternate-screen path -
/// vim is genuinely present on the capture machine, so this exercises the actual editor rather
/// than a scripted stand-in.
/// </summary>
internal sealed class TuiVimScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "tui-vim",
        Tier: 2,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "A full-screen editor on the alternate screen showing the Rust source with syntax " +
                "colouring, a status line at the bottom, and no shell prompt visible.");

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane pane = context.OpenTab(context.World.DemoProfile);
        await context.RunCommandAsync(pane, "vim src/sixel-decoder.rs");

        // The property is IsAltScreenActive (TerminalBuffer.cs), not the IsAlternateScreen an
        // earlier draft of this task named. If vim is ever missing on the capture machine, this
        // is the check that turns that into a loud failure instead of a screenshot of a shell
        // prompt: `vim: command not found` writes to the *primary* screen and never flips this.
        context.Driver.WaitFor(
            () => pane.Buffer!.IsAltScreenActive,
            TimeSpan.FromSeconds(20),
            "vim to switch to the alternate screen");

        context.Capture();

        // A leading Escape byte guards against a stray keystroke having left vim in insert mode;
        // ":q!" then discards any unintended edit rather than prompting to save, which would
        // otherwise leave an unanswered "save changes?" dialog on the alternate screen.
        pane.Session!.SendInput(":q!\n");

        // Waited for explicitly, not just pumped a fixed number of times: DisposePanes (run once
        // per scenario by Program.cs) only waits for the *shell* to exit, not for vim to have
        // left the alternate screen first. An editor that failed to quit would otherwise be
        // silently torn down mid-session instead of failing this scenario by name.
        context.Driver.WaitFor(
            () => !pane.Buffer!.IsAltScreenActive,
            TimeSpan.FromSeconds(10),
            "vim to restore the primary screen after ':q!'");
    }
}

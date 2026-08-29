using NovaTerminal.Controls;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// A real vim session editing the fabricated Rust source, on the real alternate-screen path -
/// vim is genuinely present on the capture machine, so this exercises the actual editor rather
/// than a scripted stand-in.
/// </summary>
internal sealed class TuiVimScenario : IScenario
{
    // Git for Windows' system vimrc (/etc/vimrc, sourced before any user vimrc — confirmed via
    // `vim --version`'s "system vimrc file" line and by reading it directly) sets a statusline
    // whose last-modified clause is `strftime("%H:%M %d/%m/%Y", getftime(expand("%:p")))` - the
    // seeded file's own mtime, formatted as a live-looking clock. Because DemoWorld re-seeds the
    // workspace (and so rewrites src/sixel-decoder.rs) on every run, that mtime - and therefore
    // this rendered timestamp - differs run to run, exactly like an unpinned wall clock would.
    // DemoWorld.cs's HOME redirect never touches it because it lives in the *system* vimrc, not
    // a user one. Fixed the same way the git dates are pinned: at the point of invocation, not by
    // patching the machine's vimrc. `-c` (not VIMINIT) keeps the override scenario-local - it
    // runs after all of vim's own init, so nothing vimrc does can shadow it, and it touches
    // nothing outside this one process launch. The replacement statusline keeps every clause of
    // the original except the timestamp one: filename, flags, fileformat, then a right-aligned
    // line/column/percent position.
    private const string VimCommand =
        "vim -c 'set statusline=%f%h%m%r\\ [%{&ff}]%=%l,%c%V\\ %P' src/sixel-decoder.rs";

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
        await context.RunCommandAsync(pane, VimCommand);

        // The property is IsAltScreenActive (TerminalBuffer.cs), not the IsAlternateScreen an
        // earlier draft of this task named. If vim is ever missing on the capture machine, this
        // is the check that turns that into a loud failure instead of a screenshot of a shell
        // prompt: `vim: command not found` writes to the *primary* screen and never flips this.
        context.WaitForAltScreen(
            pane,
            active: true,
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
        context.WaitForAltScreen(
            pane,
            active: false,
            TimeSpan.FromSeconds(10),
            "vim to restore the primary screen after ':q!'");
    }
}

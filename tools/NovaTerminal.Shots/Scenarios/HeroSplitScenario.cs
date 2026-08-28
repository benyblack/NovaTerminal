using System.Reflection;
using Avalonia.Layout;
using NovaTerminal.Controls;
using NovaTerminal.Shell;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// The lead hero shot: one tab split into three live panes. This is the image the README leads
/// with, so every pane must be genuinely full of its own content rather than an empty split that
/// happened to catch a pane before its shell answered.
/// </summary>
internal sealed class HeroSplitScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "hero-split",
        Tier: 1,
        LogicalWidth: 1920,
        LogicalHeight: 900,
        Intent: "Three panes at once: a colourful test run on the left, a git graph top-right, and " +
                "a process monitor bottom-right. Every pane full of text, splitters clearly visible.");

    /// <summary>
    /// This scenario inherits DemoWorld's default FontSize (18) unless overridden. At the
    /// original LogicalWidth 1440 split two ways, each pane was only ~63 columns wide - well
    /// under the ~56-character prompt plus any command over ten characters, so both split panes
    /// wrapped mid-token (`bash scrip` / `ts/demo-test.sh`). FontSize 14 alone (~81 columns at
    /// 1440) still wasn't enough for this scenario's longest command - the prompt (~56 chars)
    /// plus `git log --graph --oneline --all -12` (36 chars) is ~92 characters - so
    /// LogicalWidth is widened to 1920 as well: combined with FontSize 14 that's comfortably
    /// over 100 columns per split pane, with margin for a longer command later.
    /// </summary>
    public Action<TerminalSettings>? Settings => settings => settings.FontSize = 14;

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane left = context.OpenTab(context.World.DemoProfile);
        await context.RunCommandAsync(left, "clear");
        await context.RunCommandAsync(left, "bash scripts/demo-test.sh");

        // A second command, so the left pane's content isn't just one test run's worth of
        // output - Intent requires "every pane full of text", and one 6-line suite plus its
        // summary left roughly the top third of the pane full and the rest empty.
        await context.RunCommandAsync(left, "bash scripts/nova-banner.sh");

        // Still ~29 of 55 rows short after the two commands above (measured on-disk: the test
        // run and banner together land the fresh prompt at row ~26 of 55). sixel-decoder.rs is
        // a fabricated ~88-line file (78-line asset plus SeedWorkspace's own appends) - `cat`ing
        // it unabridged would overshoot by roughly 50 rows and scroll the colourful test run
        // above (and even part of the file itself) off the top, which the Intent's "test run on
        // the left" and this task's own guidance both rule out as worse than a small gap.
        // Piping through `head` keeps the literal `cat src/sixel-decoder.rs` this was specified
        // as, while tuning the row count to land the fresh prompt within a couple of rows of the
        // pane's bottom edge instead of scrolling anything important away.
        await context.RunCommandAsync(left, "cat src/sixel-decoder.rs | head -n 22");

        TerminalPane topRight = SplitAndGetNewPane(context, Orientation.Horizontal);

        // Adds a one-line-per-commit summary ("1 file changed, N insertions(+)") on top of the
        // existing branch-and-merge history (round 1 took SeedWorkspace from 3 linear commits to
        // ~10 with a real merge). Measured against the actual captured image, not just the raw
        // PTY row count: full --stat (a `file | N ++--` line *and* a summary line per commit)
        // over all 10 commits was 32 rows all told (echo + content + fresh prompt) against ~25
        // usable, enough to scroll the command's own echo line off the top - the exact failure
        // this task warns is worse than a gap. --shortstat (summary line only, no per-file line)
        // over all 10 commits is 23 rows all told - a close, safe fit with a couple of rows to
        // spare, and (unlike trimming the commit count with -N) keeps the entire branch/merge
        // shape intact: the docs commit, the merge commit, and both palette-branch commits it
        // brought in.
        await context.RunCommandAsync(topRight, "git log --graph --oneline --all -12 --shortstat");

        TerminalPane bottomRight = SplitAndGetNewPane(context, Orientation.Vertical);

        // Not `ps aux | head -20`: real `ps` output is the real capture machine, in full - the
        // real uid/gid, the real TTY names, real PIDs, and this harness's own bash/grep/head -
        // none of which DemoWorld's PS1 or environment overrides can touch, because none of
        // that text passes through them. demo-top.sh is plain printf, so there is nothing real
        // in it to leak, and its row count is small and fixed so the table is never mid-scroll.
        await context.RunCommandAsync(bottomRight, "bash scripts/demo-top.sh");

        context.Capture();
    }

    /// <summary>
    /// Splits the current pane and returns the new one, asserting the split actually moved
    /// MainWindow's notion of the current pane.
    /// </summary>
    /// <remarks>
    /// <see cref="MainWindow.SplitPane"/> silently returns without doing anything when
    /// <c>_currentPane</c> is null (MainWindow.axaml.cs:5225) - a no-op split would leave the
    /// next command typed into whatever pane was already current, and on the lead image that
    /// would mean capturing a single-pane "hero split" with no failure anywhere. Every other
    /// scenario in this catalogue asserts the state it depends on; this one asserted nothing
    /// about the split itself.
    /// </remarks>
    private static TerminalPane SplitAndGetNewPane(ShotContext context, Orientation orientation)
    {
        TerminalPane before = CurrentPane(context);

        context.Driver.InvokePrivate(context.Window, "SplitPane", orientation);
        TerminalPane after = CurrentPane(context);

        if (ReferenceEquals(before, after))
        {
            throw new InvalidOperationException(
                $"SplitPane({orientation}) did not change MainWindow's current pane - it silently " +
                "no-ops when _currentPane is null, so this would otherwise capture a single-pane " +
                "window as a 'hero split' with no failure anywhere.");
        }

        return after;
    }

    /// <summary>
    /// Reads MainWindow's notion of "the pane the next split/command targets".
    /// </summary>
    /// <remarks>
    /// The task brief describes <c>_currentPane</c> as a private field and reaches it with
    /// <c>GetField</c>. That is wrong: MainWindow.axaml.cs:49-51 declares it as a private
    /// <b>property</b> (backed by the field <c>_currentPaneValue</c> at :48), and its setter has
    /// a side effect — flipping <c>IsActivePane</c> on the old and new pane — that a raw field
    /// read would still observe correctly, so either route would have been safe to read from.
    /// This uses <c>GetProperty</c> because that is what the member actually is; reading the
    /// backing field directly would keep working today but silently stop matching the source the
    /// moment the property gained real get-time logic instead of a plain pass-through.
    /// </remarks>
    private static TerminalPane CurrentPane(ShotContext context)
    {
        PropertyInfo property = typeof(MainWindow).GetProperty(
            "_currentPane", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindow._currentPane no longer exists.");

        return (TerminalPane)property.GetValue(context.Window)!;
    }
}

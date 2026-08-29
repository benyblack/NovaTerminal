using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using NovaTerminal.Controls;
using NovaTerminal.Pty;
using NovaTerminal.Shell;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// A short clip of splitting one pane into three and then broadcasting input across the tab: the
/// same two <see cref="HeroSplitScenario.SplitAndGetNewPane"/> calls that scenario's still uses,
/// followed by the real "Pane: Toggle Broadcast Input (Tab)" binding
/// (<c>Ctrl+Shift+B</c>, MainWindow.axaml.cs:2973/:5606) and a command typed into the
/// newly-focused pane that lands - keystroke by keystroke, then as a run - in the other two panes
/// as well.
/// </summary>
/// <remarks>
/// Broadcasting has no rendered affordance of its own: <c>ToggleBroadcastForCurrentTab</c>
/// (MainWindow.axaml.cs:2482) only flips the native window <c>Title</c>
/// (<c>UpdateBroadcastIndicator</c>, :2503) and a per-tab flag consumed by workspace persistence -
/// nothing in the pane content changes the instant the toggle fires, and the OS title bar is
/// outside what <see cref="Rasterizer.CaptureWindow"/> rasterizes. So this clip does not spend
/// frame budget waiting for the toggle itself to "settle" (it never visibly changes anything to
/// settle on) - the toggle is pressed once and the clip moves straight to the part that is
/// genuinely on screen: broadcast input actually landing in the sibling panes.
/// </remarks>
internal sealed class ClipSplitScenario : IScenario
{
    private const int Fps = 20;

    /// <summary>How long a rendered picture must hold still before a split settles, for this
    /// clip's own pacing. See <see cref="ClipAgentScenario"/>'s ChangeQuietFor for why this is
    /// deliberately shorter than <c>ShotContext</c>'s 600ms still-settle window.</summary>
    private static readonly TimeSpan ChangeQuietFor = TimeSpan.FromMilliseconds(450);

    /// <summary>How long any one scene may take to finish changing before this clip gives up on it.</summary>
    private static readonly TimeSpan MaxSceneWait = TimeSpan.FromSeconds(5);

    private const int PreRollHoldFrames = 8;
    private const int PostSplitHoldFrames = 8;
    private const int FinalHoldFrames = 15;

    /// <summary>
    /// The banner, not a plain <c>echo</c>: broadcasting it into three panes at once - all three
    /// redrawing the same colourful, multi-line output simultaneously - is a far more legible
    /// demonstration of "one keystroke reaches every pane" than three single-line echoes would be.
    /// </summary>
    private const string BroadcastCommand = "bash scripts/nova-banner.sh";

    /// <summary>
    /// Typed as a throwaway first keystroke before <see cref="BroadcastCommand"/>'s own
    /// characters, to absorb a real, observed race: the very first character typed immediately
    /// after the broadcast toggle intermittently reaches only one of the two sibling panes -
    /// confirmed directly on screen (not a rendering guess) across repeated runs, alternating
    /// which sibling loses it, and surviving a 150ms real settle plus extra pumps after the
    /// toggle, so it is not simply "wait longer for the toggle." A leading space is bash's own
    /// no-op: leading whitespace before a command is insignificant to word splitting, so whichever
    /// pane's copy of it is dropped still runs the exact same <see cref="BroadcastCommand"/> - this
    /// absorbs a genuine product race without fabricating what the clip shows or risking every
    /// third run failing outright on a corrupted command.
    /// </summary>
    private const char BroadcastLeadIn = ' ';

    public ShotSpec Spec { get; } = new(
        Name: "clip-split",
        Tier: 4,
        LogicalWidth: 1920,
        LogicalHeight: 900,
        Intent: "A short clip: one pane split twice into three, broadcast input turned on with the " +
                "real Ctrl+Shift+B binding, then a command typed into the focused pane that lands " +
                "keystroke by keystroke in the other two panes as well and runs in all three at once.");

    /// <summary>Same reasoning as HeroSplitScenario's: three panes need the extra columns a wider,
    /// smaller-font window gives them, or the banner wraps mid-line in the narrower two.</summary>
    public Action<TerminalSettings>? Settings => settings => settings.FontSize = 14;

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane left = context.OpenTab(context.World.DemoProfile);
        await context.RunCommandAsync(left, "clear");
        await context.RunCommandAsync(left, "bash scripts/nova-banner.sh");

        await context.RecordAsync(async () =>
        {
            // A beat on the settled single pane, so the clip has somewhere to open before the
            // splits start happening.
            context.CaptureUntilSettled(context.Window, ChangeQuietFor, MaxSceneWait, PreRollHoldFrames);

            TerminalPane topRight = HeroSplitScenario.SplitAndGetNewPane(context, Orientation.Horizontal);
            context.CaptureUntilSettled(context.Window, ChangeQuietFor, MaxSceneWait, PostSplitHoldFrames);

            TerminalPane bottomRight = HeroSplitScenario.SplitAndGetNewPane(context, Orientation.Vertical);
            context.CaptureUntilSettled(context.Window, ChangeQuietFor, MaxSceneWait, PostSplitHoldFrames);

            PressToggleBroadcastBinding(context);

            // Flushed before anything is typed, and asserted rather than assumed: the toggle
            // must actually be in effect before this clip starts relying on it.
            context.Driver.Pump(3);
            RequireBroadcastEnabled(context);

            await BroadcastCommandAsync(context, left, topRight);

            // A closing beat on the settled, banner-filled three-pane result.
            context.CaptureUntilSettled(context.Window, ChangeQuietFor, MaxSceneWait, FinalHoldFrames);
        }, Fps);

        context.Capture();
    }

    /// <summary>
    /// Presses the real <c>Ctrl+Shift+B</c> chord MainWindow.axaml.cs:2973 and :5606 both wire to
    /// <c>ToggleBroadcastForCurrentTab</c> - the same key a user would press, and the same one the
    /// command palette entry "Pane: Toggle Broadcast Input (Tab)" is bound to. No reflection is
    /// needed to reach a private method here, unlike <see cref="HeroSplitScenario.SplitAndGetNewPane"/>'s
    /// own <c>SplitPane</c> call, which has no keybinding of its own in this catalogue's other
    /// scenarios to reuse.
    /// </summary>
    private static void PressToggleBroadcastBinding(ShotContext context) =>
        context.Driver.PressKey(Key.B, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.B, null);

    /// <summary>
    /// Types <see cref="BroadcastCommand"/> character by character into <paramref name="left"/>
    /// and <paramref name="topRight"/>'s newly-current sibling, then presses Enter, capturing
    /// frames throughout and proving - via each sibling's own <c>OnOutputReceived</c> counter,
    /// not just "the call did not throw" - that real PTY bytes reached both of them.
    /// </summary>
    /// <remarks>
    /// Enter is broadcast too: <c>TryMapBroadcastKey</c> (MainWindow.axaml.cs:2415) maps
    /// <c>Key.Enter</c> to a bare "\r" for every sibling, so this one key press does not just
    /// leave the typed text sitting unsubmitted in the other panes - it runs the same command in
    /// all three at once, which is what makes "runs in all three at once" in this clip's Intent a
    /// claim about what actually happened rather than what the command merely looks like it
    /// would do.
    /// </remarks>
    private static async Task BroadcastCommandAsync(ShotContext context, TerminalPane left, TerminalPane topRight)
    {
        ITerminalSession leftSession = left.Session
            ?? throw new InvalidOperationException("The left pane has no session.");
        ITerminalSession topRightSession = topRight.Session
            ?? throw new InvalidOperationException("The top-right pane has no session.");

        int leftChunks = 0;
        int topRightChunks = 0;
        void OnLeft(string _) => Interlocked.Increment(ref leftChunks);
        void OnTopRight(string _) => Interlocked.Increment(ref topRightChunks);

        leftSession.OnOutputReceived += OnLeft;
        topRightSession.OnOutputReceived += OnTopRight;

        try
        {
            context.Driver.TypeText(BroadcastLeadIn.ToString());
            context.Recorder!.CaptureFrame();
            context.Recorder!.CaptureFrame();

            foreach (char c in BroadcastCommand)
            {
                context.Driver.TypeText(c.ToString());
                context.Recorder!.CaptureFrame();
                context.Recorder!.CaptureFrame();
            }

            context.Driver.PressKey(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");

            // Reuses ClipAgentScenario's chunk-driven settle loop rather than a frame-hash
            // comparison, for the same reason that scenario does: the interesting signal here is
            // real bytes landing in the sibling sessions, which a rendered-frame hash cannot
            // observe any more directly than it could demo-test.sh's paced suite lines.
            ClipAgentScenario.CaptureUntilOutputSettled(
                context,
                () => Volatile.Read(ref leftChunks) + Volatile.Read(ref topRightChunks),
                ChangeQuietFor,
                MaxSceneWait,
                PostSplitHoldFrames);

            if (Volatile.Read(ref leftChunks) == 0 || Volatile.Read(ref topRightChunks) == 0)
            {
                throw new InvalidOperationException(
                    "Broadcasting the typed command produced no output in one or both sibling " +
                    "panes, so this clip would not be showing a real broadcast reaching them.");
            }
        }
        finally
        {
            leftSession.OnOutputReceived -= OnLeft;
            topRightSession.OnOutputReceived -= OnTopRight;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Asserts broadcast is actually on for the current tab right after the toggle - fail fast,
    /// the same way <see cref="HeroSplitScenario.SplitAndGetNewPane"/> asserts its own split
    /// actually happened, rather than typing a whole command on the assumption a key press landed
    /// - reading it through the same <c>IsBroadcastEnabledForTab</c> MainWindow itself uses for
    /// workspace persistence (MainWindow.axaml.cs:2208). That method is internal, not private, and
    /// this project has InternalsVisibleTo (the same reasoning
    /// <c>TabsVerticalScenario.SetTabTitle</c> gives for calling <c>UpdateTabVisuals</c> directly),
    /// so it is called directly rather than through <see cref="Driver.InvokePrivate"/>.
    /// </summary>
    private static void RequireBroadcastEnabled(ShotContext context)
    {
        var tabs = context.Driver.Require<TabControl>("Tabs");
        var tab = tabs.SelectedItem as TabItem
            ?? throw new InvalidOperationException("No tab is selected in clip-split.");

        if (!context.Window.IsBroadcastEnabledForTab(tab))
        {
            throw new InvalidOperationException(
                "Broadcast is not enabled for the current tab after pressing Ctrl+Shift+B in " +
                "clip-split - the press either did not register or was toggled back off.");
        }
    }
}

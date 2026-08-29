using NovaTerminal.Controls;
using NovaTerminal.Pty;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// A short clip of a full-screen TUI actually redrawing: the same demo-monitor.sh fixture
/// <see cref="TuiMonitorScenario"/>'s still uses, launched on camera and captured as it redraws
/// itself a few times on the alternate screen, ending on the settled table
/// <see cref="TuiMonitorScenario"/> also photographs.
/// </summary>
/// <remarks>
/// <para>
/// demo-monitor.sh's own header explains why it exists: htop, btop and top are all missing on the
/// capture machine, so this is a fabricated, literal-printf stand-in rather than a real process
/// monitor. Its three redraws are gated behind <see cref="ClipAgentScenario.PacedEnvironmentVariable"/>
/// - the same variable name <c>ClipAgentScenario</c> sets for demo-test.sh, reused here rather than
/// invented twice - and, with it set, are spaced ~300ms apart in real time.
/// </para>
/// <para>
/// Each of the three redraws prints the byte-for-byte identical screen (same fixed PIDs, same
/// fixed percentages - see demo-monitor.sh's <c>draw_frame</c>) - proving the mechanism (clear,
/// then redraw) rather than showing changing data, the same scoping its header comment states.
/// So the visible motion this clip actually has is the launch itself - the shell prompt giving way
/// to the full-screen table - not new information appearing redraw over redraw. This scenario
/// does not manufacture fake motion to compensate: <see cref="RunAsync"/>'s own report of its
/// distinct-frame ratio names this plainly rather than padding the clip with holds that would not
/// change that number.
/// </para>
/// </remarks>
internal sealed class ClipTuiScenario : IScenario
{
    private const int Fps = 20;

    /// <summary>
    /// Frames held on the settled empty prompt before the monitor launches. Matches
    /// <see cref="ClipAgentScenario"/>'s own pre-roll rather than padding further: demo-monitor.sh's
    /// three redraws render byte-identical content (see this class's own remarks), so this clip's
    /// real signal is thin already - a long static hold before it starts would only dilute the
    /// distinct-frame ratio for no benefit.
    /// </summary>
    private const int PreRollHoldFrames = 6;

    /// <summary>
    /// Quiet window for the pre-roll's own settle check. OpenTab's prompt has already settled by
    /// the time this scenario gets control (nothing paced is running yet), so this only needs to
    /// match <c>ShotContext</c>'s own still-settle window, not demo-monitor.sh's pace gap.
    /// </summary>
    private static readonly TimeSpan PreRollChangeQuietFor = TimeSpan.FromMilliseconds(450);

    /// <summary>
    /// How long the pane's output must stay quiet before the launch-and-redraw sequence counts as
    /// finished. Deliberately well above demo-monitor.sh's own 300ms pace gap between redraws -
    /// double <see cref="ClipAgentScenario"/>'s 450ms, which is tuned against a different fixture's
    /// 180ms gap and would leave only 150ms of margin here, half what that scenario's own remarks
    /// judged necessary for a 180ms gap. See <see cref="ClipAgentScenario.CaptureUntilOutputSettled"/>'s
    /// remarks for why this is a parameter each caller tunes for its own fixture rather than a
    /// shared constant.
    /// </summary>
    private static readonly TimeSpan ChangeQuietFor = TimeSpan.FromMilliseconds(900);

    /// <summary>How long the launch-and-redraw sequence may take before this clip gives up on it.</summary>
    private static readonly TimeSpan MaxSceneWait = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Frames held once the output goes quiet, before recording stops. Kept short for the same
    /// reason <see cref="PreRollHoldFrames"/> is: this fixture has little real content to hold on.
    /// </summary>
    private const int SettleHoldFrames = 6;

    public ShotSpec Spec { get; } = new(
        Name: "clip-tui",
        Tier: 4,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "A short clip: the shell prompt gives way to a full-screen fabricated process " +
                "monitor on the alternate screen, which redraws itself a couple of times before " +
                "settling on the finished table.");

    /// <summary>
    /// Must run before <c>MainWindow</c> is constructed, not from inside <see cref="RunAsync"/> -
    /// see <see cref="IScenario.PrepareEnvironment"/>'s own remarks and
    /// <see cref="ClipAgentScenario.PrepareEnvironment"/>'s: <see cref="ShotContext.OpenTab"/>'s
    /// first call adopts the shell MainWindow already spawned during construction, and a variable
    /// set after that adoption is too late for that shell to ever see - it inherits the process
    /// environment only at its own spawn. ClipAgentScenario's own remarks describe hitting exactly
    /// this with demo-test.sh's pacing; this scenario sets the same variable the same way,
    /// deliberately, rather than risk the same failure with demo-monitor.sh.
    /// </summary>
    public Action? PrepareEnvironment => () => _previousPace = ClipAgentScenario.EnablePacing();

    private string? _previousPace;

    public async Task RunAsync(ShotContext context)
    {
        try
        {
            TerminalPane pane = context.OpenTab(context.World.DemoProfile);

            ITerminalSession session = pane.Session
                ?? throw new InvalidOperationException("The pane has no session.");

            int chunks = 0;
            void OnOutput(string _) => Interlocked.Increment(ref chunks);
            session.OnOutputReceived += OnOutput;

            try
            {
                await context.RecordAsync(async () =>
                {
                    // A beat on the settled empty prompt OpenTab already leaves behind, so the
                    // clip has somewhere to open before the launch happens.
                    context.CaptureUntilSettled(context.Window, PreRollChangeQuietFor, MaxSceneWait, PreRollHoldFrames);

                    session.SendInput("bash scripts/demo-monitor.sh\n");

                    // Chunk-count driven, not a frame-hash wait, and for the same reason
                    // ClipAgentScenario's RunAnimatedCommandAsync gives: demo-monitor.sh's three
                    // redraws render byte-identical pictures (see this class's own remarks), so a
                    // hash comparison would call the scene "settled" the instant the first one
                    // finished and never notice the other two happened.
                    ClipAgentScenario.CaptureUntilOutputSettled(
                        context,
                        () => Volatile.Read(ref chunks),
                        ChangeQuietFor,
                        MaxSceneWait,
                        SettleHoldFrames);

                    await Task.CompletedTask;
                }, Fps);
            }
            finally
            {
                session.OnOutputReceived -= OnOutput;
            }

            if (Volatile.Read(ref chunks) == 0)
            {
                throw new InvalidOperationException(
                    "Launching demo-monitor.sh produced no output at all, not even the echo of the " +
                    "typed line, so the shell is wedged or gone and this clip would not be showing " +
                    "a real launch.");
            }

            // The property is IsAltScreenActive (TerminalBuffer.cs) - see TuiVimScenario's own
            // remarks for why this, and not a rendered-frame guess, is what actually proves the
            // monitor took over the screen rather than merely printing "command not found" to the
            // primary one.
            context.WaitForAltScreen(
                pane,
                active: true,
                TimeSpan.FromSeconds(20),
                "demo-monitor.sh to switch to the alternate screen");

            // The clip's own recording already ended on this same settled table - this is the
            // still that ships alongside it, at the run's configured scale rather than the
            // recorder's 1x, exactly as ClipAgentScenario's final Capture() does.
            context.Capture();

            // demo-monitor.sh parks reading one key at a time until it sees 'q', then restores the
            // primary screen itself - the same alternate-screen exit path TuiMonitorScenario uses,
            // and not part of the recorded clip: this scenario's Intent claims only the launch and
            // redraw, not an exit transition nobody asked to see.
            pane.Session!.SendInput("q");

            context.WaitForAltScreen(
                pane,
                active: false,
                TimeSpan.FromSeconds(10),
                "demo-monitor.sh to restore the primary screen after 'q'");
        }
        finally
        {
            ClipAgentScenario.RestorePacing(_previousPace);
        }
    }
}

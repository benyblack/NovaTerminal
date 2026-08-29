using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using NovaTerminal.AgentHost;
using NovaTerminal.Controls;
using NovaTerminal.Pty;
using NovaTerminal.Shell;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// The lead clip: an MCP agent typing into a live session while the activity journal ticks.
/// </summary>
/// <remarks>
/// Drives the same in-process agent-host path <see cref="AgentSessionScenario"/> does -
/// <see cref="AgentHostService.HandleRequestLineAsync"/> - rather than reinventing it, for the
/// same reason: the journal entries a clip of a security feature shows must be genuine, not a
/// staged animation of UI state nobody produced.
/// </remarks>
internal sealed class ClipAgentScenario : IScenario
{
    /// <summary>Title given to the journal window by MainWindow.ShowAgentActivityJournalAsync.</summary>
    private const string JournalWindowTitle = "Agent activity";

    /// <summary>The pane segment's label in the Wrote tier (TerminalPane.ApplyAgentAttention).</summary>
    private const string WroteSegmentLabel = "agent typed";

    private const int Fps = 20;

    /// <summary>
    /// How long a rendered picture must hold still before a scene counts as settled, for the
    /// purposes of this clip's own pacing. Shorter than <c>ShotContext</c>'s 600ms QuietFor
    /// deliberately: that value exists to be certain a *still* is finished drawing before it is
    /// published, where being slow costs nothing; this one decides how long to keep sampling a
    /// clip scene for further motion before moving on, where being slow costs frame budget on
    /// every single scene.
    /// </summary>
    /// <remarks>
    /// Must stay above <c>demo-test.sh</c>'s paced per-line gap (180ms, only emitted when
    /// <see cref="PacedEnvironmentVariable"/> is set, which this scenario does). At the previous
    /// value of 120ms, the ~180ms gap between two suite lines read as "settled" well before the
    /// script actually finished: <see cref="CaptureUntilSettled"/> broke out after the first
    /// quiet 120ms window, burned its hold frames on that partial transcript, and the remaining
    /// suites only appeared after the journal cut away and back - a jump, not motion. Raised
    /// again from 300ms to 450ms: 300ms left only 120ms of headroom over the 180ms gap, and each
    /// gap pays a real `sleep` process spawn in Git-Bash - on a loaded machine that spawn alone
    /// was observed blowing a 600ms budget elsewhere in this harness, so 120ms of margin was not
    /// enough to trust. 450ms roughly triples that margin while staying safely under the 600ms a
    /// still needs.
    /// </remarks>
    private static readonly TimeSpan ChangeQuietFor = TimeSpan.FromMilliseconds(450);

    /// <summary>
    /// Set for the lifetime of this scenario's single pane, so <c>demo-test.sh</c> paces its
    /// suite lines instead of printing all six in one instant burst - this clip is the one place
    /// a real shell progressing on camera is worth the frame budget. Must be set before the pane
    /// opens: the shell inherits the harness process's environment at spawn time, so a change
    /// made after the shell is already running never reaches it.
    /// </summary>
    /// <remarks>
    /// Internal, not private: <c>demo-monitor.sh</c> (Task 16's <c>ClipTuiScenario</c>) gates its
    /// own per-frame <c>pace()</c> helper behind this exact same variable name, by design - see
    /// its header comment - so both clips share one name instead of inventing a second.
    /// </remarks>
    internal const string PacedEnvironmentVariable = "NOVA_SHOTS_PACE";

    /// <summary>How long any one scene may take to finish changing before this clip gives up on it.</summary>
    private static readonly TimeSpan MaxSceneWait = TimeSpan.FromSeconds(5);

    /// <summary>Frames held on the settled "before" pane, so the clip has a beat to open on.</summary>
    private const int PreRollHoldFrames = 6;

    /// <summary>Frames held on a command's finished output before moving on.</summary>
    private const int CommandHoldFrames = 8;

    /// <summary>Frames held on the open journal window before closing it.</summary>
    private const int JournalHoldFrames = 10;

    /// <summary>Frames held on the finished transcript before recording stops.</summary>
    private const int FinalHoldFrames = 10;

    /// <summary>The commands the agent runs, in order - the same three <see cref="AgentSessionScenario"/> uses.</summary>
    private static readonly string[] AgentCommands =
    [
        "git status --short --branch",
        "git log --graph --oneline -5",
        "bash scripts/demo-test.sh"
    ];

    public ShotSpec Spec { get; } = new(
        Name: "clip-agent",
        Tier: 4,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "A short clip of the main window: the demo pane already showing its banner, then " +
                "an MCP agent sends three commands one after another through the real agent host " +
                "- a git status, a commit graph, and a passing test run - while the amber 'agent " +
                "typed' segment lights in the status bar. After each command the clip cuts to the " +
                "real agent activity journal, open and on screen, whose entry count has grown by " +
                "one since the last time it was shown. Ends on a settled still of the finished " +
                "transcript.");

    /// <summary>Act on, so the clip shows what the toggle actually buys - same as agent-session.</summary>
    public Action<TerminalSettings>? Settings => settings =>
    {
        settings.AgentAccessObserveEnabled = true;
        settings.AgentAccessActEnabled = true;
    };

    /// <summary>
    /// Not just "before OpenTab" but before MainWindow is constructed at all: MainWindow spawns
    /// (or restores) its startup tab's shell during construction/Show(), and
    /// <see cref="ShotContext.OpenTab"/>'s very first call in <see cref="RunAsync"/> below
    /// *adopts* that already-running shell rather than spawning a new one (see its own remarks).
    /// A variable set from inside RunAsync - after MainWindow already exists - is set too late
    /// for that shell: the process environment it inherited at its own spawn is fixed for its
    /// lifetime. Confirmed the hard way: with the set moved here, before it was in RunAsync
    /// (still ahead of the OpenTab call, but after MainWindow's own construction had already
    /// spawned and this scenario's pane had silently adopted that unpaced shell), a diagnostic
    /// dump of every <see cref="ITerminalSession.OnOutputReceived"/> chunk showed demo-test.sh's
    /// entire six-suite output landing in one ~40ms burst - the pacing was never seen at all.
    /// </summary>
    /// <remarks>
    /// <see cref="_previousPace"/> remembers the value this displaced, so RunAsync's finally can
    /// put it back - later scenarios in the same run (Program.cs runs the whole catalogue in one
    /// process) must not inherit paced output for their own stills. See demo-test.sh's own
    /// remarks for why that matters.
    /// </remarks>
    public Action? PrepareEnvironment => () => _previousPace = EnablePacing();

    private string? _previousPace;

    /// <summary>
    /// Sets <see cref="PacedEnvironmentVariable"/> and returns whatever value it displaced, so a
    /// caller's own <c>finally</c> can put that back - shared by this scenario and
    /// <c>ClipTuiScenario</c> (Task 16) rather than each inlining the same get/set pair.
    /// </summary>
    internal static string? EnablePacing()
    {
        string? previous = Environment.GetEnvironmentVariable(PacedEnvironmentVariable);
        Environment.SetEnvironmentVariable(PacedEnvironmentVariable, "1");
        return previous;
    }

    /// <summary>Undoes <see cref="EnablePacing"/>, restoring exactly the value it displaced.</summary>
    internal static void RestorePacing(string? previous) =>
        Environment.SetEnvironmentVariable(PacedEnvironmentVariable, previous);

    public async Task RunAsync(ShotContext context)
    {
        try
        {
            TerminalPane pane = context.OpenTab(context.World.DemoProfile);

            await context.RunCommandAsync(pane, "clear");
            await context.RunCommandAsync(pane, "bash scripts/nova-banner.sh");

            context.Driver.WaitFor(
                () => AgentSessionRegistry.Instance.TryGet(pane.PaneId, out _),
                TimeSpan.FromSeconds(10),
                "the pane to register with the agent-session registry");

            int journalBefore = AgentActivityJournal.Instance.Count;

            await context.RecordAsync(async () =>
            {
                // A beat on the settled "before" pane, deliberately: with no pre-roll the clip
                // would open mid-transcript, and a viewer scrubbing back to frame zero would see
                // the same fully-typed picture the still already shows.
                context.CaptureUntilSettled(context.Window, ChangeQuietFor, MaxSceneWait, PreRollHoldFrames);

                for (int i = 0; i < AgentCommands.Length; i++)
                {
                    await RunAnimatedCommandAsync(context, pane, AgentCommands[i]);

                    // The journal is what makes this an honest clip of an agent acting, not just
                    // a clip of a terminal - so it is shown ticking on camera, once per command,
                    // rather than opened only after recording stops to be checked and thrown away.
                    ShowJournalTick(context, journalBefore + i + 1);
                }

                context.CaptureUntilSettled(context.Window, ChangeQuietFor, MaxSceneWait, FinalHoldFrames);
            }, Fps);

            if (AgentActivityJournal.Instance.Count < journalBefore + AgentCommands.Length)
            {
                throw new InvalidOperationException(
                    $"The agent journal recorded fewer than the {AgentCommands.Length} calls this " +
                    "clip just made, so the clip would be showing a staged agent rather than the " +
                    "real one. Check that act is enabled and the host is running.");
            }

            RequireLitAgentSegment(pane);

            // The clip's final frame is already a settled transcript - this is the still that
            // ships alongside it, at the run's configured scale rather than the recorder's 1x.
            context.Capture();
        }
        finally
        {
            RestorePacing(_previousPace);
        }
    }

    /// <summary>
    /// Delivers <paramref name="command"/> through the agent host and captures the pane while its
    /// output lands and draws.
    /// </summary>
    /// <remarks>
    /// <see cref="AgentHostService.HandleRequestLineAsync"/> submits the whole command in one call
    /// - the real <c>send_input</c> path genuinely does not stream keystrokes - but that only means
    /// there is no gradual *typing* to show. The demo-test.sh command's own OUTPUT still streams:
    /// with <see cref="PacedEnvironmentVariable"/> set, it prints its six suites roughly 180ms
    /// apart in real time, and this clip exists specifically to show that progression on camera.
    /// <para>
    /// This does not use <see cref="ShotContext.CaptureUntilSettled"/>, which decides "did the
    /// picture change" by re-encoding the whole frame to PNG and hashing it every poll - expensive
    /// enough per iteration that, empirically, it missed every one of the six lines and only ever
    /// captured "nothing yet" and "all six done", the same jump-cut shape as the original 120ms
    /// bug just landing on the correct frame instead of the wrong one. Real progress is instead
    /// read off <paramref name="pane"/>'s own session: <c>chunks</c> below counts
    /// <see cref="ITerminalSession.OnOutputReceived"/> events (the actual PTY bytes each print
    /// produces), and a frame is captured whenever that count moves - a cheap, direct signal of
    /// "the shell just said something", rather than an indirect and costlier "does the screen look
    /// different now" guess.
    /// </para>
    /// </remarks>
    private static async Task RunAnimatedCommandAsync(ShotContext context, TerminalPane pane, string command)
    {
        ITerminalSession session = pane.Session
            ?? throw new InvalidOperationException("The pane has no session.");

        int chunks = 0;
        void OnOutput(string _) => Interlocked.Increment(ref chunks);
        session.OnOutputReceived += OnOutput;

        try
        {
            await AgentWire.DeliverAsync(pane, command);

            CaptureUntilOutputSettled(context, () => Volatile.Read(ref chunks), ChangeQuietFor, MaxSceneWait, CommandHoldFrames);

            if (Volatile.Read(ref chunks) == 0)
            {
                throw new InvalidOperationException(
                    $"the agent-delivered '{command}' produced no output at all, not even the " +
                    "echo of the typed line, so the shell is wedged or gone and this clip would " +
                    "not be showing a real session.");
            }
        }
        finally
        {
            session.OnOutputReceived -= OnOutput;
        }
    }

    /// <summary>
    /// Captures a frame each time <paramref name="readChunkCount"/> reports new output has
    /// arrived, until it stops changing for <paramref name="changeQuietFor"/> or
    /// <paramref name="maxSceneWait"/> elapses, then holds <paramref name="holdFrames"/> more.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="ShotContext.CaptureUntilSettled"/>'s shape (poll, capture on change,
    /// break once quiet, then hold) but keyed on the session's own output-chunk counter instead
    /// of a rendered-frame hash - see <see cref="RunAnimatedCommandAsync"/>'s remarks for why.
    /// <para>
    /// Internal, not private: <c>ClipSplitScenario</c> and <c>ClipTuiScenario</c> (Task 16) both
    /// drive fixtures whose real motion is likewise only visible on the PTY byte stream, not in a
    /// frame-hash diff - demo-monitor.sh's three redraws are byte-identical, and a broadcast
    /// pane's sibling never receives a keystroke event to hash against - so both reuse this exact
    /// method rather than re-deriving the same poll/capture/settle loop.
    /// </para>
    /// <para>
    /// <paramref name="changeQuietFor"/> is a parameter, not a shared constant, deliberately: this
    /// scenario's own <see cref="ChangeQuietFor"/> (450ms) is tuned specifically against
    /// demo-test.sh's 180ms line gap (see that field's own remarks), and demo-monitor.sh's real
    /// pace gap is 300ms - a caller that reused 450ms unexamined would be trusting a margin never
    /// verified for its own fixture. Each caller passes the value it has actually checked against
    /// its own script's pacing.
    /// </para>
    /// </remarks>
    internal static void CaptureUntilOutputSettled(
        ShotContext context,
        Func<int> readChunkCount,
        TimeSpan changeQuietFor,
        TimeSpan maxSceneWait,
        int holdFrames)
    {
        FrameRecorder recorder = context.Recorder
            ?? throw new InvalidOperationException("CaptureUntilOutputSettled was called outside RecordAsync.");

        int previous = readChunkCount();
        DateTime deadline = DateTime.UtcNow + maxSceneWait;
        DateTime quietSince = DateTime.UtcNow;
        bool everChanged = false;

        while (DateTime.UtcNow < deadline)
        {
            context.Driver.Pump(1);

            int current = readChunkCount();
            if (current != previous)
            {
                previous = current;
                quietSince = DateTime.UtcNow;
                everChanged = true;
                recorder.CaptureFrame(context.Window);
            }
            else if (everChanged && DateTime.UtcNow - quietSince >= changeQuietFor)
            {
                break;
            }
        }

        for (int i = 0; i < holdFrames; i++)
        {
            context.Driver.Pump(1);
            recorder.CaptureFrame(context.Window);
        }
    }

    /// <summary>
    /// Opens the real journal window, waits for its list to actually show
    /// <paramref name="expectedMinimumEntries"/> rows, captures it on camera, then closes it.
    /// </summary>
    /// <remarks>
    /// Called once per command, so across the clip the journal is shown three times with a
    /// growing entry count each time - the honest version of "the activity journal ticks": real
    /// rows a user could open and read, not a fake counter animating upward. The wait for the
    /// list to actually reach the expected count (rather than opening once and hoping) is this
    /// clip's version of the same "did the count really grow" gate <see cref="AgentSessionScenario"/>
    /// applies to its still, just paid before the frame is captured instead of after.
    /// </remarks>
    private static void ShowJournalTick(ShotContext context, int expectedMinimumEntries)
    {
        Button indicator = context.Driver.Require<Button>("AgentObserveIndicator");

        if (!indicator.IsVisible)
        {
            throw new InvalidOperationException(
                "The title bar's agent observe light is hidden, so the journal cannot be shown " +
                "ticking in this clip. Check that observe is on.");
        }

        indicator.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        context.Driver.WaitFor(
            () => TryFindEntries(context, out int itemCount) && itemCount >= expectedMinimumEntries,
            TimeSpan.FromSeconds(10),
            $"the journal window to list at least {expectedMinimumEntries} entries");

        Window journal = FindJournalWindow(context)
            ?? throw new InvalidOperationException(
                $"Clicking the agent observe light opened no '{JournalWindowTitle}' window, so " +
                "this clip's journal tick cannot be shown.");

        try
        {
            context.CaptureUntilSettled(journal, ChangeQuietFor, MaxSceneWait, JournalHoldFrames);
        }
        finally
        {
            journal.Close();
            context.Driver.Pump(3);
        }
    }

    private static bool TryFindEntries(ShotContext context, out int itemCount)
    {
        if (FindJournalWindow(context) is { } journal && FindEntriesList(journal) is { } entries)
        {
            itemCount = entries.ItemCount;
            return true;
        }

        itemCount = 0;
        return false;
    }

    private static Window? FindJournalWindow(ShotContext context) =>
        context.Window.OwnedWindows.FirstOrDefault(
            window => string.Equals(window.Title, JournalWindowTitle, StringComparison.Ordinal));

    private static ItemsControl? FindEntriesList(Window journal) =>
        journal.GetLogicalDescendants().OfType<ItemsControl>().FirstOrDefault();

    /// <summary>
    /// Asserts the pane is wearing the amber "agent typed" segment the clip's Intent claims it
    /// ends on. Same check, same reasoning, as <see cref="AgentSessionScenario"/>'s: the write
    /// tier retires once the focused pane is 10s past the last write, and three fast commands
    /// settle well inside that, but "well inside" depends on how fast this machine's shell is.
    /// </summary>
    private static void RequireLitAgentSegment(TerminalPane pane)
    {
        TextBlock label = pane.FindControl<TextBlock>("AgentStatusText")
            ?? throw new InvalidOperationException(
                "TerminalPane has no 'AgentStatusText'. The markup changed - update the scenario.");

        if (!string.Equals(label.Text, WroteSegmentLabel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The pane's agent segment reads '{label.Text}' rather than '{WroteSegmentLabel}', " +
                "so the clip would not show the agent's writes being reported.");
        }
    }
}

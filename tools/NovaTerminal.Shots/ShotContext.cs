using Avalonia.Controls;
using NovaTerminal.Controls;
using NovaTerminal.Platform.Ssh.Launch;
using NovaTerminal.Pty;
using NovaTerminal.Shell;
using SkiaSharp;

namespace NovaTerminal.Shots;

public sealed class ShotContext
{
    /// <summary>How long the frame must hold still before a command counts as finished.</summary>
    private static readonly TimeSpan QuietFor = TimeSpan.FromMilliseconds(600);

    /// <summary>How long a command may take to finish once it has started answering.</summary>
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a command may take to show any sign of life at all.</summary>
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);

    private readonly IScenario _scenario;

    public ShotContext(MainWindow window, Driver driver, DemoWorld world, ShotRun run, IScenario scenario)
    {
        Window = window;
        Driver = driver;
        World = world;
        Run = run;
        _scenario = scenario;
    }

    public MainWindow Window { get; }

    public Driver Driver { get; }

    public DemoWorld World { get; }

    public ShotRun Run { get; }

    public FrameRecorder? Recorder { get; private set; }

    /// <summary>
    /// Runs <paramref name="body"/> while capturing frames, then encodes WebM and GIF.
    /// Frames are captured by the scenario calling <c>Recorder.CaptureFrame()</c> between
    /// steps: a timer-driven recorder would race the dispatcher and drop the frames that
    /// matter, so the scenario decides when the picture has changed.
    /// </summary>
    /// <remarks>
    /// The frame-count check and the encoding happen after the try/finally, not inside its
    /// finally, on purpose: a finally that can throw (CA2219) or <c>return</c> out of itself
    /// (CS0157) does not compile, and even set aside the compiler, a finally that throws its
    /// own exception silently replaces whatever <paramref name="body"/> failed with - the
    /// least useful moment to lose that message. Running them after means they execute only
    /// once <paramref name="body"/> has actually completed; a failed scenario propagates its
    /// own exception instead and skips encoding, which is what Program.cs's per-scenario catch
    /// expects. <see cref="Recorder"/> is still always cleared, success or failure, so a later
    /// scenario never inherits a stale recorder.
    /// </remarks>
    public async Task RecordAsync(Func<Task> body, int fps = 20)
    {
        string frameDirectory = Path.Combine(Run.OutputDirectory, "frames", _scenario.Spec.Name);

        // Cleared, not just created: ffmpeg's numbered-frame demuxer takes every contiguously
        // numbered file it finds starting at frame-00000.png, so a previous run's leftover frames
        // would silently ride along in this run's encode if this run ever produces fewer frames
        // than that one did - fewer frames this run, not none, because deleting first and letting
        // FrameRecorder recreate the directory always leaves a contiguous run starting at zero.
        if (Directory.Exists(frameDirectory))
        {
            Directory.Delete(frameDirectory, recursive: true);
        }

        var recorder = new FrameRecorder(Window, frameDirectory, 1.0);
        Recorder = recorder;

        try
        {
            await body();
        }
        finally
        {
            Recorder = null;
        }

        if (recorder.FrameCount == 0)
        {
            throw new InvalidOperationException($"'{_scenario.Spec.Name}' recorded no frames.");
        }

        if (!Encoder.IsAvailable())
        {
            Console.Error.WriteLine("[shots] ffmpeg not found; frames kept, clips skipped.");

            // Surfaced onto Run, not just logged: a console line here is easy to miss in ~two
            // minutes of capture output, and Publisher.Prune has no other way to learn that this
            // scenario's .webm/.gif were never (re)produced this run - see ShotRun.
            // RecordClipEncodingSkipped's remarks for why this cannot share failedScenarios'
            // list even though both end up refusing the same prune.
            Run.RecordClipEncodingSkipped(_scenario.Spec.Name);
            return;
        }

        string webm = Path.Combine(Run.OutputDirectory, $"{_scenario.Spec.Name}.webm");
        string gif = Path.Combine(Run.OutputDirectory, $"{_scenario.Spec.Name}.gif");

        Encoder.ToWebm(frameDirectory, webm, fps);
        Encoder.ToGif(frameDirectory, gif, fps);
    }

    /// <summary>
    /// Captures frames into <see cref="Recorder"/> only while <paramref name="window"/>'s
    /// rendered picture is actually changing, then a short, deliberate hold once it settles - so
    /// a clip's frame budget tracks real state changes instead of a fixed-size loop's worth of
    /// duplicate bitmaps.
    /// </summary>
    /// <remarks>
    /// Reuses the same frame-fingerprint comparison <see cref="WaitForQuiet"/> uses for its own
    /// "has this settled" check. This exists because a first cut of clip-agent captured a blind
    /// fixed number of frames per command regardless of whether the picture was still moving, and
    /// review found 94 of its 100 frames were byte-identical duplicates - the shape of a
    /// slideshow with long static holds wearing a video's file extension, not a clip of motion.
    /// A scene that never changes now costs exactly <paramref name="settleHoldFrames"/> frames (a
    /// deliberately short pause, long enough to be readable, short enough not to dominate the
    /// clip); a scene that keeps changing captures every distinct paint until it stops or
    /// <paramref name="timeout"/> elapses.
    /// </remarks>
    public void CaptureUntilSettled(Window window, TimeSpan quietFor, TimeSpan timeout, int settleHoldFrames)
    {
        FrameRecorder recorder = Recorder
            ?? throw new InvalidOperationException("CaptureUntilSettled was called outside RecordAsync.");

        string? previous = null;
        DateTime deadline = DateTime.UtcNow + timeout;
        DateTime quietSince = DateTime.UtcNow;
        bool everChanged = false;

        while (DateTime.UtcNow < deadline)
        {
            Driver.Pump(1);

            using SKBitmap frame = Rasterizer.CaptureWindow(window, 1.0);
            string fingerprint = Fingerprint(frame);

            if (fingerprint != previous)
            {
                previous = fingerprint;
                quietSince = DateTime.UtcNow;
                everChanged = true;
                recorder.CaptureFrame(window);
            }
            else if (everChanged && DateTime.UtcNow - quietSince >= quietFor)
            {
                break;
            }
        }

        for (int i = 0; i < settleHoldFrames; i++)
        {
            Driver.Pump(1);
            recorder.CaptureFrame(window);
        }
    }

    /// <summary>
    /// Opens a tab through MainWindow's own AddTab, so the pane is wired, registered with the
    /// agent-session registry, and themed exactly as a user-opened tab is.
    /// </summary>
    /// <remarks>
    /// The first call adopts the tab the window opened with instead of adding a second one.
    /// MainWindow always starts a tab on the default profile (MainWindow.axaml.cs, the
    /// TryRestoreStartupSession fallback), so an unconditional AddTab leaves every screenshot with
    /// one more tab than the scenario asked for - two identically labelled tabs in the hero still,
    /// which is also enough to trip the tab strip's collision disambiguator and print a "~a1b2"
    /// hint on both. Adoption is conditional on the running pane actually being this profile, so a
    /// scenario opening a different profile still gets a new tab.
    /// </remarks>
    public TerminalPane OpenTab(TerminalProfile profile)
    {
        var tabs = Window.FindControl<TabControl>("Tabs")
            ?? throw new InvalidOperationException("MainWindow has no 'Tabs' control.");

        TerminalPane? startupPane = tabs.Items.Count == 1
            ? FindPane(tabs.Items[0] as TabItem ?? throw new InvalidOperationException("The tab strip holds a non-tab item."))
            : null;

        if (startupPane?.Profile is null || startupPane.Profile.Id != profile.Id)
        {
            Driver.InvokePrivate(Window, "AddTab", profile, SshDiagnosticsLevel.None);
            startupPane = null;
        }

        var selected = tabs.SelectedItem as TabItem
            ?? throw new InvalidOperationException("AddTab did not select a tab.");

        TerminalPane pane = startupPane ?? FindPane(selected)
            ?? throw new InvalidOperationException("The new tab contains no TerminalPane.");

        Driver.WaitFor(
            () => pane.Session is not null && pane.IsProcessRunning,
            TimeSpan.FromSeconds(30),
            $"the shell in the '{profile.Name}' profile to start");

        // A running process is not yet a drawn prompt. Settling here keeps the shell's banner
        // and first prompt out of the middle of the first command's output.
        //
        // Quiet only, deliberately, with no "something changed" phase: there is no action here to
        // sample a baseline before, so the prompt may already be on screen by the time this runs
        // and a change gate would fail a perfectly good pane. The first command's own phase-one
        // wait does not baseline this, either - it watches the session's own output events, not
        // the rendered frame, so it has nothing to do with whatever this leaves on screen.
        WaitForQuiet(QuietFor, SettleTimeout, "the shell's first prompt");

        return pane;
    }

    private static TerminalPane? FindPane(Control control) => control switch
    {
        TerminalPane pane => pane,
        ContentControl content when content.Content is Control inner => FindPane(inner),
        Decorator decorator when decorator.Child is Control child => FindPane(child),
        Panel panel => panel.Children.OfType<Control>().Select(FindPane).FirstOrDefault(p => p is not null),
        _ => null
    };

    /// <summary>Sends a command and waits for the shell to answer it and the frame to go quiet.</summary>
    /// <param name="expectsResponse">
    /// False only for input the shell will not answer at all. It is very nearly always true: an
    /// interactive shell echoes the line as it is typed, so even a command that prints nothing of
    /// its own puts bytes on the PTY. Passing false skips the "did anything happen" phase and
    /// waits for quiet alone, which cannot tell a settled frame from a dead one.
    /// </param>
    public Task RunCommandAsync(TerminalPane pane, string command, bool expectsResponse = true)
    {
        ITerminalSession session = pane.Session
            ?? throw new InvalidOperationException("The pane has no session.");

        // Attached BEFORE the input is sent, so the output phase one waits for can only be this
        // command's answer.
        using var answered = new ShellAnswer(session);

        session.SendInput(command + "\n");

        if (expectsResponse)
        {
            WaitForAnswer(
                answered,
                $"the shell to answer '{command}'. It wrote nothing at all, not even the echo of " +
                "the typed line, so it is wedged or gone. A command the shell genuinely does not " +
                "answer must say so with expectsResponse: false");
        }

        // Phase two: it finished drawing.
        WaitForQuiet(QuietFor, SettleTimeout, command);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Waits for a command this scenario did not type - <paramref name="deliver"/> puts it into
    /// <paramref name="pane"/> by some other route - to be answered and then to finish drawing.
    /// </summary>
    /// <remarks>
    /// The same two phases as <see cref="RunCommandAsync"/>, and for the same reasons, minus the
    /// send. The agent-session scenario delivers its commands through AgentHostService rather
    /// than the pane's own session, so it needs the wait without the typing; a
    /// <c>RunCommandAsync(pane, string.Empty)</c> stand-in would settle correctly but would also
    /// press Enter at a prompt the agent had already submitted.
    ///
    /// The delivery is taken as a delegate rather than being left to the caller so the watch
    /// cannot be attached on the wrong side of it: phase one is only meaningful if it is listening
    /// before the input lands. Here that phase carries extra weight - it is the proof that the
    /// agent's bytes actually reached the shell, not just that the host said "ok".
    /// </remarks>
    public async Task RunDeliveredCommandAsync(TerminalPane pane, Func<Task> deliver, string what)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(deliver);

        ITerminalSession session = pane.Session
            ?? throw new InvalidOperationException("The pane has no session.");

        using var answered = new ShellAnswer(session);

        await deliver();

        WaitForAnswer(
            answered,
            $"the shell to answer {what}. It wrote nothing at all, not even the echo of the " +
            "delivered line, so the input never reached it even though the delivery reported " +
            "success");

        WaitForQuiet(QuietFor, SettleTimeout, what);
    }

    /// <summary>
    /// Phase one: the shell answered. Without it, a shell that is slow to answer - a slow PTY
    /// round trip, a script that pauses before its first byte - reads as "settled" after 600ms of
    /// the unchanged pre-command frame, and the scenario types its next command into a busy shell
    /// or captures a half-drawn transcript. That failure is silent: the blank-raster guard cannot
    /// catch it, because a half-drawn transcript is far above 1% ink. Failing here instead names
    /// the command that never answered.
    /// </summary>
    /// <remarks>
    /// It asks the session rather than the picture, and that distinction is what makes the phase
    /// usable at all. The rendered frame is not a witness of "the shell answered": `clear` sent to
    /// a pane that is already showing nothing but the prompt on row 0 ends exactly where it
    /// started, and the one frame that differs - the echoed word before the screen is wiped - is
    /// usually never rasterized, because the pane invalidates on a 16ms timer and this harness only
    /// renders between Pump() calls, while the whole round trip takes a couple of milliseconds. A
    /// frame comparison therefore failed *every* scenario whose shell happened to be fast enough,
    /// which is every scenario after the first in a warm process. Bytes on the PTY have neither
    /// problem: a wedged or dead shell writes none, which is precisely the condition this phase
    /// exists to catch, and an answer that draws nothing new still counts as an answer.
    /// </remarks>
    private void WaitForAnswer(ShellAnswer answered, string description) =>
        Driver.WaitFor(() => answered.Answered, ResponseTimeout, description);

    /// <summary>
    /// Watches one shell for any output at all, from the moment it is constructed until it is
    /// disposed.
    /// </summary>
    /// <remarks>
    /// A second subscriber on top of the pane's own, which is deliberate and safe: RustPtySession
    /// replays its startup buffer only to the *first* subscriber ever attached, and by the time a
    /// scenario runs a command that is long since the pane. So this counts nothing but output that
    /// arrives while it is listening.
    /// </remarks>
    private sealed class ShellAnswer : IDisposable
    {
        private readonly ITerminalSession _session;
        private readonly Action<string> _handler;
        private int _chunks;

        public ShellAnswer(ITerminalSession session)
        {
            _session = session;
            _handler = _ => Interlocked.Increment(ref _chunks);
            _session.OnOutputReceived += _handler;
        }

        public bool Answered => Volatile.Read(ref _chunks) > 0;

        public void Dispose() => _session.OnOutputReceived -= _handler;
    }

    /// <summary>
    /// Waits until the rendered frame stops changing. Sleeping a fixed interval would either
    /// truncate a slow command or waste time on a fast one; comparing frames measures the thing
    /// that actually matters — that the image is finished.
    ///
    /// On its own this says only "nothing has changed lately", which is equally true of a frame
    /// nothing has started happening in yet - see the phase-one wait in
    /// <see cref="RunCommandAsync"/>.
    /// </summary>
    private void WaitForQuiet(TimeSpan quietFor, TimeSpan timeout, string what)
    {
        string? previous = null;
        DateTime quietSince = DateTime.UtcNow;

        Driver.WaitFor(
            () =>
            {
                using SKBitmap frame = Rasterizer.CaptureWindow(Window, 1.0);
                string fingerprint = Fingerprint(frame);

                if (fingerprint != previous)
                {
                    previous = fingerprint;
                    quietSince = DateTime.UtcNow;
                    return false;
                }

                return DateTime.UtcNow - quietSince >= quietFor;
            },
            timeout,
            $"output of '{what}' to settle");
    }

    private static string Fingerprint(SKBitmap bitmap)
    {
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 20);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data.AsSpan()));
    }

    /// <summary>
    /// Disposes every pane in the window, and with it every PTY session and shell process.
    /// </summary>
    /// <remarks>
    /// Closing the window does not do this. MainWindow.OnClosing runs PerformAppTeardown, which
    /// saves the session, stops its timers and unhooks the agent host but never touches the panes;
    /// the only thing that disposes an ITerminalSession is DisposeControlTree, reached from the
    /// close-tab paths and from DisposeAllTabs. That is harmless in the app, where the process
    /// exits moments later, and not harmless here: this process goes on to the next scenario, so
    /// without this a twelve-scenario run ends holding a dozen live shells and a dozen
    /// RustPtySession reader loops - the leaked-session shape issue #81 traced its headless
    /// dispatcher deadlocks to.
    ///
    /// It waits for the shells to actually be gone rather than just asking for it: the session
    /// teardown DisposeControlTree starts runs on a thread-pool thread, and DemoWorld.Dispose
    /// cannot delete a workspace that is still some shell's working directory.
    /// </remarks>
    public void DisposePanes()
    {
        var tabs = Window.FindControl<TabControl>("Tabs");
        if (tabs is null)
        {
            return;
        }

        // Every pane in every tab, not just the first per tab: a split tab (hero-split, Task 10)
        // holds more than one TerminalPane, and FindPane's Panel case only returns the first
        // match. Missing the siblings does not leak anything - DisposeAllTabs still walks and
        // disposes the whole content tree below - but it weakens the wait below to "the first
        // pane per tab is gone", which is not the guarantee DemoWorld.Dispose depends on.
        ITerminalSession[] sessions = tabs.Items.OfType<TabItem>()
            .SelectMany(FindPanes)
            .Select(pane => pane.Session)
            .OfType<ITerminalSession>()
            .ToArray();

        Driver.InvokePrivate(Window, "DisposeAllTabs", tabs);

        Driver.WaitFor(
            () => sessions.All(session => !session.IsProcessRunning),
            TimeSpan.FromSeconds(30),
            "the scenario's shells to exit after their panes were disposed");
    }

    /// <summary>Every TerminalPane reachable from <paramref name="control"/>, not just the first.</summary>
    private static IEnumerable<TerminalPane> FindPanes(Control control) => control switch
    {
        TerminalPane pane => [pane],
        ContentControl content when content.Content is Control inner => FindPanes(inner),
        Decorator decorator when decorator.Child is Control child => FindPanes(child),
        Panel panel => panel.Children.OfType<Control>().SelectMany(FindPanes),
        _ => []
    };

    /// <summary>
    /// Waits for <paramref name="pane"/>'s alternate-screen state to reach <paramref name="active"/>.
    /// Shared by every scenario that drives a full-screen TUI through the enter-capture-exit life
    /// cycle (tui-vim, tui-monitor, and Task 16's clip-tui, per demo-monitor.sh's own header) so
    /// that life cycle is defined once instead of duplicated per scenario.
    /// </summary>
    /// <param name="what">
    /// Describes what is being waited for, e.g. "vim to switch to the alternate screen" — reported
    /// verbatim in the timeout message, which is what makes a stuck TUI diagnosable from the log
    /// alone.
    /// </param>
    public void WaitForAltScreen(TerminalPane pane, bool active, TimeSpan timeout, string what) =>
        Driver.WaitFor(() => pane.Buffer!.IsAltScreenActive == active, timeout, what);

    /// <summary>Captures the window and records it in the run manifest.</summary>
    public void Capture(string? suffix = null)
    {
        string name = suffix is null ? _scenario.Spec.Name : $"{_scenario.Spec.Name}-{suffix}";
        CaptureAndRecord(Window, name);
    }

    /// <summary>
    /// Captures a window other than the main one — the settings window is its own Window, so
    /// it never appears in a MainWindow frame.
    /// </summary>
    public void CaptureOther(Window window, string suffix)
    {
        string name = $"{_scenario.Spec.Name}-{suffix}";
        CaptureAndRecord(window, name);
    }

    /// <summary>
    /// Records a bitmap the scenario already assembled itself - e.g. by capturing the same window
    /// at two scroll positions and cropping/stacking the on-topic region of each with
    /// <see cref="PostProcess"/> - rather than one rasterized fresh from a live window. Shares the
    /// blank-raster guard and manifest bookkeeping with <see cref="Capture"/>/
    /// <see cref="CaptureOther"/> so a composed frame is held to the same bar as a plain one.
    /// </summary>
    public void CaptureComposed(SKBitmap bitmap, string suffix)
    {
        string name = $"{_scenario.Spec.Name}-{suffix}";
        RecordBitmap(bitmap, name);
    }

    /// <summary>Shared body of <see cref="Capture"/> and <see cref="CaptureOther"/>.</summary>
    private void CaptureAndRecord(Window window, string name)
    {
        using SKBitmap bitmap = Rasterizer.CaptureWindow(window, Run.Scale);
        RecordBitmap(bitmap, name);
    }

    private void RecordBitmap(SKBitmap bitmap, string name)
    {
        string path = Path.Combine(Run.OutputDirectory, $"{name}@{Run.Scale:0}x.png");

        double ink = Rasterizer.InkFraction(bitmap);
        if (ink < 0.01)
        {
            throw new InvalidOperationException(
                $"'{name}' rasterized to a near-uniform image ({ink:P2} ink). That is the blank-raster " +
                "failure mode, not a screenshot. Check that the window laid out and the scenario waited.");
        }

        Rasterizer.WritePng(bitmap, path);

        Run.Record(new ShotAsset(
            Name: name,
            Tier: _scenario.Spec.Tier,
            File: path,
            Width: bitmap.Width,
            Height: bitmap.Height,
            Scenario: _scenario.Spec.Name,
            Commit: Run.Commit,
            Os: Run.Os,
            TimestampUtc: DateTime.UtcNow.ToString("O")));
    }
}

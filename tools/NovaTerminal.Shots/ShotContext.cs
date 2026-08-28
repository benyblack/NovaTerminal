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
        // wait is the real gate - it baselines whatever this leaves behind, prompt or blank.
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

    /// <summary>Sends a command and waits for the pane to answer it and then go quiet.</summary>
    /// <param name="expectsResponse">
    /// False only for input the pane will not visibly react to. It is very nearly always true:
    /// an interactive shell echoes the line as it is typed, so even a command that prints nothing
    /// of its own changes the frame. Passing false skips the "did anything happen" phase and
    /// waits for quiet alone, which is the pre-existing behaviour and its pre-existing risk.
    /// </param>
    public Task RunCommandAsync(TerminalPane pane, string command, bool expectsResponse = true)
    {
        ITerminalSession session = pane.Session
            ?? throw new InvalidOperationException("The pane has no session.");

        // Sampled BEFORE the input is sent, so the change phase one waits for can only be this
        // command's. Sampling afterwards would race the echo and could baseline a frame that
        // already contains the answer.
        string beforeSending = CurrentFingerprint();

        session.SendInput(command + "\n");

        if (expectsResponse)
        {
            // Phase one: something happened. Without it, a shell that is slow to answer - a slow
            // PTY round trip, a script that pauses before its first byte - reads as "settled"
            // after 600ms of the unchanged pre-command frame, and the scenario types its next
            // command into a busy shell or captures a half-drawn transcript. That failure is
            // silent: the blank-raster guard cannot catch it, because a half-drawn transcript
            // is far above 1% ink. Failing here instead names the command that never answered.
            Driver.WaitFor(
                () => CurrentFingerprint() != beforeSending,
                ResponseTimeout,
                $"the pane to show any response to '{command}'. Nothing changed on screen at all, " +
                "not even the echo of the typed line, so the shell is wedged or gone. A command " +
                "that genuinely draws nothing must say so with expectsResponse: false");
        }

        // Phase two: it finished.
        WaitForQuiet(QuietFor, SettleTimeout, command);

        return Task.CompletedTask;
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

    private string CurrentFingerprint()
    {
        using SKBitmap frame = Rasterizer.CaptureWindow(Window, 1.0);
        return Fingerprint(frame);
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

        ITerminalSession[] sessions = tabs.Items.OfType<TabItem>()
            .Select(FindPane)
            .OfType<TerminalPane>()
            .Select(pane => pane.Session)
            .OfType<ITerminalSession>()
            .ToArray();

        Driver.InvokePrivate(Window, "DisposeAllTabs", tabs);

        Driver.WaitFor(
            () => sessions.All(session => !session.IsProcessRunning),
            TimeSpan.FromSeconds(30),
            "the scenario's shells to exit after their panes were disposed");
    }

    /// <summary>Captures the window and records it in the run manifest.</summary>
    public void Capture(string? suffix = null)
    {
        string name = suffix is null ? _scenario.Spec.Name : $"{_scenario.Spec.Name}-{suffix}";
        string path = Path.Combine(Run.OutputDirectory, $"{name}@{Run.Scale:0}x.png");

        using SKBitmap bitmap = Rasterizer.CaptureWindow(Window, Run.Scale);

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

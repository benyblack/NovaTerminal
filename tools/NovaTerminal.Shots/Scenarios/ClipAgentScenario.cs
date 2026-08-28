using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using NovaTerminal.AgentHost;
using NovaTerminal.AgentHost.Contracts;
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
    private static readonly TimeSpan ChangeQuietFor = TimeSpan.FromMilliseconds(120);

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

    private static long _requestId;

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

    public async Task RunAsync(ShotContext context)
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
            // A beat on the settled "before" pane, deliberately: with no pre-roll the clip would
            // open mid-transcript, and a viewer scrubbing back to frame zero would see the same
            // fully-typed picture the still already shows.
            context.CaptureUntilSettled(context.Window, ChangeQuietFor, MaxSceneWait, PreRollHoldFrames);

            for (int i = 0; i < AgentCommands.Length; i++)
            {
                await RunAnimatedCommandAsync(context, pane, AgentCommands[i]);

                // The journal is what makes this an honest clip of an agent acting, not just a
                // clip of a terminal - so it is shown ticking on camera, once per command, rather
                // than opened only after recording stops to be checked and thrown away.
                ShowJournalTick(context, journalBefore + i + 1);
            }

            context.CaptureUntilSettled(context.Window, ChangeQuietFor, MaxSceneWait, FinalHoldFrames);
        }, Fps);

        if (AgentActivityJournal.Instance.Count < journalBefore + AgentCommands.Length)
        {
            throw new InvalidOperationException(
                $"The agent journal recorded fewer than the {AgentCommands.Length} calls this " +
                "clip just made, so the clip would be showing a staged agent rather than the real " +
                "one. Check that act is enabled and the host is running.");
        }

        RequireLitAgentSegment(pane);

        // The clip's final frame is already a settled transcript - this is the still that ships
        // alongside it, at the run's configured scale rather than the recorder's 1x.
        context.Capture();
    }

    /// <summary>
    /// Delivers <paramref name="command"/> through the agent host and captures the pane while its
    /// output lands and draws.
    /// </summary>
    /// <remarks>
    /// <see cref="AgentHostService.HandleRequestLineAsync"/> submits the whole command in one
    /// call - the real <c>send_input</c> path genuinely does not stream keystrokes - so there is
    /// no gradual "typing" to capture within a single command; the shell renders its answer in
    /// one paint well inside one <see cref="Driver.Pump"/>. <see cref="ShotContext.CaptureUntilSettled"/>
    /// still earns its keep here: it captures that one real paint transition and nothing more,
    /// rather than the fixed-size loop of duplicate frames a blind Pump-N-times approach would
    /// have spent regardless of whether anything was still moving.
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
            await DeliverAsAgentAsync(pane, command);

            context.CaptureUntilSettled(context.Window, ChangeQuietFor, MaxSceneWait, CommandHoldFrames);

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
    /// Issues <paramref name="command"/> exactly as NovaTerminal.McpServer's <c>send_input</c>
    /// tool does, through <see cref="AgentHostService.HandleRequestLineAsync"/>.
    /// </summary>
    private static async Task DeliverAsAgentAsync(TerminalPane pane, string command)
    {
        AgentHostResponse response = await SendInputAsAgentAsync(pane.PaneId, command);

        if (response.Error is not null)
        {
            throw new InvalidOperationException(
                $"agent sendInput was rejected: {response.Error.Code} {response.Error.Message}. " +
                "Act is probably still disabled, or the pane is not registered.");
        }
    }

    private static async Task<AgentHostResponse> SendInputAsAgentAsync(Guid paneId, string command)
    {
        var request = new AgentHostRequest
        {
            Version = AgentHostProtocol.Version,
            Id = Interlocked.Increment(ref _requestId),
            Method = AgentHostProtocol.Methods.SendInput,
            Params = JsonSerializer.SerializeToElement(
                new SendInputParams { PaneId = paneId, Text = command, Submit = true },
                AgentHostJsonContext.Default.SendInputParams)
        };

        string line = JsonSerializer.Serialize(request, AgentHostJsonContext.Default.AgentHostRequest);

        return await AgentHostService.Instance.HandleRequestLineAsync(line, CancellationToken.None);
    }

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

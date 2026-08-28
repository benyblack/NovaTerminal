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

    /// <summary>A settled beat of the pane before the agent starts, so the clip has a visible "before".</summary>
    private const int PreRollFrames = 10;

    /// <summary>Frames captured while each command lands and draws. 3 commands * 30 + 10 pre-roll = 100 frames at 20fps = 5s.</summary>
    private const int FramesPerCommand = 30;

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
        Intent: "A ~5 second clip of the main window: the demo pane already showing its banner, " +
                "then an MCP agent sends three commands one after another through the real agent " +
                "host - a git status, a commit graph, and a passing test run - while the amber " +
                "'agent typed' segment lights in the status bar. Ends on a settled still of the " +
                "finished transcript.");

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
            // A beat of the settled "before" pane, deliberately: with no pre-roll the clip would
            // open mid-transcript, and a viewer scrubbing back to frame zero would see the same
            // fully-typed picture the still already shows.
            for (int i = 0; i < PreRollFrames; i++)
            {
                context.Driver.Pump(1);
                context.Recorder!.CaptureFrame();
            }

            foreach (string command in AgentCommands)
            {
                await RunAnimatedCommandAsync(context, pane, command);
            }
        }, Fps);

        if (AgentActivityJournal.Instance.Count < journalBefore + AgentCommands.Length)
        {
            throw new InvalidOperationException(
                $"The agent journal recorded fewer than the {AgentCommands.Length} calls this " +
                "clip just made, so the clip would be showing a staged agent rather than the real " +
                "one. Check that act is enabled and the host is running.");
        }

        RequireLitAgentSegment(pane);
        VerifyJournalWindowReflectsTheRun(context, journalBefore + AgentCommands.Length);

        // The clip's final frame is already a settled transcript - this is the still that ships
        // alongside it, at the run's configured scale rather than the recorder's 1x.
        context.Capture();
    }

    /// <summary>
    /// Delivers <paramref name="command"/> through the agent host and captures frames while it
    /// lands and draws, so the picture fills in across the clip instead of jumping straight to
    /// the finished transcript.
    /// </summary>
    /// <remarks>
    /// Frames are captured by calling <see cref="FrameRecorder.CaptureFrame"/> in a loop after
    /// each <see cref="Driver.Pump"/>, deliberately, rather than by a timer: a timer-driven
    /// recorder races the dispatcher thread this scenario itself runs on and drops exactly the
    /// frames that matter, where a Pump-then-capture loop only ever samples between one pumped
    /// batch of dispatcher jobs and the next.
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

            for (int i = 0; i < FramesPerCommand; i++)
            {
                context.Driver.Pump(1);
                context.Recorder!.CaptureFrame();
            }

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

    /// <summary>
    /// Opens the real journal window and checks it lists at least <paramref name="expectedMinimumEntries"/>
    /// entries, then closes it without capturing it - this clip's deliverable is the main window
    /// alone, but its honesty gate is the same kind <see cref="AgentSessionScenario"/> uses for
    /// the still: proof that what the journal shows is not just a count incremented somewhere,
    /// but a window a user could actually open and read the same rows in.
    /// </summary>
    private static void VerifyJournalWindowReflectsTheRun(ShotContext context, int expectedMinimumEntries)
    {
        Button indicator = context.Driver.Require<Button>("AgentObserveIndicator");

        if (!indicator.IsVisible)
        {
            throw new InvalidOperationException(
                "The title bar's agent observe light is hidden, so this clip's journal honesty " +
                "gate cannot be reached. Check that observe is on.");
        }

        indicator.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        context.Driver.Pump(5);

        Window journal = context.Window.OwnedWindows.FirstOrDefault(
                window => string.Equals(window.Title, JournalWindowTitle, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Clicking the agent observe light opened no '{JournalWindowTitle}' window, so " +
                "this clip's journal entries cannot be verified as real.");

        try
        {
            ItemsControl entries = journal.GetLogicalDescendants().OfType<ItemsControl>().FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "The journal window contains no ItemsControl, so it cannot be listing anything.");

            if (entries.ItemCount < expectedMinimumEntries)
            {
                throw new InvalidOperationException(
                    $"The journal window lists {entries.ItemCount} of the {expectedMinimumEntries} " +
                    "entries this clip's commands should have produced, so the clip's story would " +
                    "not be backed by a real journal.");
            }
        }
        finally
        {
            journal.Close();
            context.Driver.Pump(3);
        }
    }
}

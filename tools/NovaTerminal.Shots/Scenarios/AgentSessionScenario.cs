using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using NovaTerminal.AgentHost;
using NovaTerminal.AgentHost.Contracts;
using NovaTerminal.Controls;
using NovaTerminal.Shell;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// An agent driving a live session. The journal entries in these images are produced by the same
/// code path NovaTerminal.McpServer's send_input reaches — a staged screenshot of a security
/// surface would be exactly the wrong shortcut, because the journal's whole purpose is that it
/// records what really happened.
/// </summary>
/// <remarks>
/// Two images, not one. The journal is a separate <see cref="Window"/>
/// (MainWindow.ShowAgentActivityJournalAsync builds it and shows it with ShowDialog), so no single
/// frame can hold both the pane an agent typed into and the journal listing what it typed —
/// the same shape as SettingsWindow in the settings-agent-access scenario. Splitting is the honest
/// resolution: both halves are real and photographable, just not together.
/// </remarks>
internal sealed class AgentSessionScenario : IScenario
{
    /// <summary>Title given to the journal window by MainWindow.ShowAgentActivityJournalAsync.</summary>
    private const string JournalWindowTitle = "Agent activity";

    /// <summary>
    /// One entry per <c>sendInput</c> this scenario issues - the three the shell runs plus the one
    /// that is refused. Every assertion written against it is a *minimum* or a *delta*, never an
    /// exact count: AgentActivityJournal.Instance is a process-wide singleton, so an `all` run in
    /// which some later scenario also drives the agent host would push the total above this.
    /// </summary>
    private const int ExpectedJournalEntries = 4;

    /// <summary>
    /// The commands the agent runs in the pane, in order. Three rather than the two this scenario
    /// started with, because the journal image is a picture of an activity log, and two rows that
    /// differ only in their timestamp do not read as one.
    /// </summary>
    private static readonly string[] AgentCommands =
    [
        "git status --short --branch",
        "git log --graph --oneline -5",
        "bash scripts/demo-test.sh"
    ];

    /// <summary>
    /// The pane segment's label in the Wrote tier (TerminalPane.ApplyAgentAttention). Matching on
    /// the rendered text rather than on the attention machine's tier is deliberate: the tier is
    /// what happened, the label is what the photograph will show, and only the second one is a
    /// claim the Intent can make.
    /// </summary>
    private const string WroteSegmentLabel = "agent typed";

    private static long _requestId;

    public ShotSpec Spec { get; } = new(
        Name: "agent-session",
        Tier: 2,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        // One sentence covering both images, because this scenario captures two and the reviewer
        // has to be able to tick each claim off one of them. Deliberately does not ask for the
        // pane and the journal in the same frame: that image cannot exist (see the class remarks),
        // and an Intent a correct capture can never satisfy teaches the review loop to ignore it.
        Intent: "Two images. 'agent-session' shows the main window: an amber dot and the words " +
                "'agent typed' in the status bar at the pane's bottom-left, the agent access light " +
                "at the right end of the title bar, and a transcript ending in a git commit graph " +
                "and a passing test run. 'agent-session-journal' shows the agent activity journal, " +
                "headed 'Recent actions taken (or attempted) by AI agents', listing timestamped " +
                "'sendInput' rows newest first: several reading '[ok] Demo · pane <id>' and one " +
                "reading '[denied: sessionNotFound]', with no empty half to the window.");

    /// <summary>
    /// Act on, for this scenario only. DemoWorld.SeedSettings seeds observe-on/act-off — the
    /// honest default the settings-agent-access shot documents — and this is the other half of
    /// the story: what the toggle actually buys. The override is applied before the window is
    /// constructed, which is what makes it reach the service at all (MainWindow's constructor
    /// reads AgentAccessActEnabled into AgentHostService.Instance.ActEnabled).
    /// </summary>
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

        // Two of the three run before the refusal and one after it, so the journal reads the way a
        // real one does - a run of successes with a denial in the middle of it - rather than as a
        // block of successes with an afterthought on top.
        await SendAsAgentAsync(context, pane, AgentCommands[0]);
        await SendAsAgentAsync(context, pane, AgentCommands[1]);
        await AttemptDeniedSendAsync();
        await SendAsAgentAsync(context, pane, AgentCommands[2]);

        if (AgentActivityJournal.Instance.Count < journalBefore + ExpectedJournalEntries)
        {
            throw new InvalidOperationException(
                $"The agent journal recorded fewer than the {ExpectedJournalEntries} calls this " +
                "scenario just made, so this image would be a staged screenshot of a security " +
                "feature. Check that act is enabled and the host is running.");
        }

        RequireLitAgentSegment(pane);

        // The main window first, while it is the only window on screen and nothing is modal over
        // it: this half of the story is the pane, not the journal.
        context.Capture();

        Window journal = OpenJournal(context);
        try
        {
            FrameJournal(context, journal);
            context.CaptureOther(journal, "journal");
        }
        finally
        {
            // Also what lets the click handler's `await dialog.ShowDialog(this)` finish. In a
            // finally that now covers the assertions too: they used to run inside OpenJournal,
            // after the window had been found, so a failing one left a modal dialog open over a
            // disabled MainWindow for the rest of the process.
            journal.Close();
            context.Driver.Pump(3);
        }
    }

    /// <summary>
    /// Asserts the pane is wearing the amber "agent typed" segment the first image's Intent claims
    /// it is, before that image is taken.
    /// </summary>
    /// <remarks>
    /// The write tier is sticky but not forever: AgentAttentionMachine retires it once the pane has
    /// been focused for WriteFloorSeconds (10 s) past the write. Two agent commands settle well
    /// inside that, but "well inside" is a property of how fast this machine's shell is, and the
    /// failure it would cause - a capture of an idle grey "agent access" segment, which is a
    /// perfectly plausible-looking image of nothing having happened - is exactly the kind the
    /// blank-raster guard cannot see. So it is checked rather than assumed.
    /// </remarks>
    private static void RequireLitAgentSegment(TerminalPane pane)
    {
        TextBlock label = pane.FindControl<TextBlock>("AgentStatusText")
            ?? throw new InvalidOperationException(
                "TerminalPane has no 'AgentStatusText'. The markup changed - update the scenario.");

        if (!string.Equals(label.Text, WroteSegmentLabel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The pane's agent segment reads '{label.Text}' rather than '{WroteSegmentLabel}', so " +
                "the capture would not show the agent's writes being reported. The write tier most " +
                "likely decayed - it is retired once the focused pane is 10s past the last write.");
        }
    }

    /// <summary>
    /// Delivers <paramref name="command"/> through the agent host and waits for the pane to answer
    /// it, exactly as if a human had typed it — except that nobody did.
    /// </summary>
    private static Task SendAsAgentAsync(ShotContext context, TerminalPane pane, string command) =>
        context.RunDeliveredCommandAsync(
            pane,
            () => DeliverAsAgentAsync(pane, command),
            $"the agent-delivered '{command}'");

    /// <summary>
    /// Issues the request exactly as NovaTerminal.McpServer's <c>send_input</c> tool does: a
    /// serialized protocol frame handed to AgentHostService's line-level entry point. Everything
    /// downstream — the act gate, the pane's agent segment, and the journal entry — therefore runs
    /// for real.
    /// </summary>
    /// <remarks>
    /// The frame is serialized to a line and parsed back rather than calling a handler directly,
    /// because that round trip is the part the wire contract actually pins: a params shape that
    /// stopped matching SendInputParams' JsonPropertyName values would fail here the same way it
    /// would fail over the pipe, instead of being silently papered over by a typed call.
    /// </remarks>
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

    /// <summary>
    /// Makes one acting attempt that the host must refuse, so the journal in the image carries a
    /// denial as well as a run of successes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the shot's whole security claim in one row: the journal exists so that nothing an
    /// agent does is silent, and a list in which every row says <c>[ok]</c> illustrates only half
    /// of that. The refusal is real - the pane id below was never registered, which is what an
    /// agent addressing a session that has since been closed looks like - so AgentHostService takes
    /// its <c>sessionNotFound</c> branch and journals the attempt through the same
    /// <c>Journaled(...)</c> wrapper the successes go through. Nothing here writes to the journal.
    /// </para>
    /// <para>
    /// It deliberately does not go through <see cref="ShotContext.RunDeliveredCommandAsync"/>: a
    /// refused call never reaches a shell, so there is no output to wait for, and that wait would
    /// time out by design.
    /// </para>
    /// </remarks>
    private static async Task AttemptDeniedSendAsync()
    {
        // Never registered with AgentSessionRegistry, and a fresh value each run so it cannot
        // collide with a pane this process really has.
        var closedPane = Guid.NewGuid();

        // The text is arbitrary and goes nowhere: the pane lookup fails before anything is
        // written, and the journal row records the method and the pane, never the input. A
        // dangerous-looking command here would imply the refusal was about what was being typed,
        // which it is not.
        AgentHostResponse response = await SendInputAsAgentAsync(closedPane, "ls");

        if (response.Error is null)
        {
            throw new InvalidOperationException(
                $"sendInput to the unregistered pane '{closedPane}' succeeded. That is a hole in " +
                "the act gate, and it also means the journal has no denied row for this capture.");
        }

        if (!string.Equals(response.Error.Code, AgentHostProtocol.ErrorCodes.SessionNotFound, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"sendInput to an unregistered pane was refused with '{response.Error.Code}' " +
                $"rather than '{AgentHostProtocol.ErrorCodes.SessionNotFound}'. The journal row " +
                "would read denied for a different reason than the one this scenario is " +
                "illustrating, so the image would not match its Intent.");
        }
    }

    /// <summary>
    /// The one wire call both deliveries share: a serialized <c>sendInput</c> frame handed to
    /// AgentHostService's line-level entry point.
    /// </summary>
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
    /// Opens the journal by clicking the title bar's agent observe light — the indicator whose own
    /// tooltip reads "Agent access is enabled. Click to open the agent activity journal", and which
    /// docs/mcp/security.md describes as one of the live indicators there is no way to silence. It
    /// is in the first image, so the two captures read as one gesture apart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no command-palette entry for the journal. Of the other routes - the Agent
    /// Activity menu item, the agent_activity title-bar action, the pane's own agent segment, and
    /// MainWindow.ShowAgentActivityJournalAsync itself - the pane segment would have been the
    /// better story, since it is the thing the first image is a picture of, and docs/mcp/security.md
    /// documents it ("Clicking the segment opens the activity journal"). It does not work:
    /// its handler is gated on <c>VisualRoot is MainWindow</c>, and under Avalonia 12 a control's
    /// VisualRoot is an <c>Avalonia.Controls.TopLevelHost</c> wrapping the window rather than the
    /// window itself, so that test is always false and the click silently does nothing (verified
    /// here - the Click event fires, no exception is thrown, and no window opens). That is a
    /// product bug in TerminalPane, not a harness limitation; it is reported rather than fixed
    /// here, because fixing it belongs with tests of its own. This route is unaffected: MainWindow
    /// wires the indicator against <c>this</c>.
    /// </para>
    /// <para>
    /// The indicator's Click handler is async void and ends in <c>await dialog.ShowDialog(this)</c>,
    /// which is safe to trigger from the dispatcher thread: Avalonia's ShowDialog returns a task
    /// tracking the dialog's lifetime rather than pushing a nested dispatcher frame (Avalonia
    /// 12.0.4 - Avalonia.Controls references neither PushFrame nor DispatcherFrame, and the dialog
    /// is observably owned the instant the click returns). Awaiting that task would be the hang;
    /// nothing here does, and the caller closing the dialog is what completes it.
    /// </para>
    /// </remarks>
    private static Window OpenJournal(ShotContext context)
    {
        Button indicator = context.Driver.Require<Button>("AgentObserveIndicator");

        if (!indicator.IsVisible)
        {
            throw new InvalidOperationException(
                "The title bar's agent observe light is hidden, which means the agent host is not " +
                "running, so there is no user path to the journal and the first image is not showing " +
                "what the Intent claims. Check that observe is on.");
        }

        indicator.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        context.Driver.Pump(5);

        // Returned the moment it is found, and nothing is asserted after this point: from here on
        // the window is a live modal child of a now-disabled MainWindow, so anything that throws
        // between finding it and handing it back would leak it. The checks that used to live here
        // are in FrameJournal, which the caller runs inside the try that closes it.
        return context.Window.OwnedWindows.FirstOrDefault(
                window => string.Equals(window.Title, JournalWindowTitle, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Clicking the agent observe light opened no '{JournalWindowTitle}' window. Its " +
                "Click handler swallows exceptions, so the failure is upstream in " +
                "MainWindow.ShowAgentActivityJournalAsync.");
    }

    /// <summary>
    /// Checks the open journal is showing what the Intent claims, and trims the window down to it.
    /// </summary>
    /// <remarks>
    /// Called by <c>RunAsync</c> inside the try whose finally closes the dialog, so a failure here
    /// cannot leave a modal window over a disabled MainWindow for the rest of the process.
    /// </remarks>
    private static void FrameJournal(ShotContext context, Window journal)
    {
        // The count check in RunAsync proves the journal recorded the calls; this proves the
        // window being photographed is showing them. Only the second one is a claim about the
        // image, and the image is the deliverable.
        ItemsControl entries = journal.GetLogicalDescendants().OfType<ItemsControl>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The journal window contains no ItemsControl, so it cannot be listing anything.");

        if (entries.ItemCount < ExpectedJournalEntries)
        {
            throw new InvalidOperationException(
                $"The journal window lists {entries.ItemCount} of the {ExpectedJournalEntries} " +
                "entries this scenario produced. Capturing it would publish an image of the " +
                "journal that under-reports what the agent did.");
        }

        ShrinkToEntries(context, journal, entries);
    }

    /// <summary>
    /// Takes the empty height out of the journal window, so the capture is of a list rather than
    /// of the room below one.
    /// </summary>
    /// <remarks>
    /// Framing, not staging: MainWindow builds this dialog with <c>canResize: true</c>, so a
    /// user's window can be any height, and 720x460 is only the size it happens to open at. The
    /// list is the one part of the layout that stretches, so the empty space is exactly the
    /// difference between its scroll viewport and the entries inside it; subtracting that leaves
    /// every real element untouched. Nothing is hidden - the count above has already established
    /// that all the entries are there, and the assertion below re-checks the fit afterwards.
    /// </remarks>
    private static void ShrinkToEntries(ShotContext context, Window journal, ItemsControl entries)
    {
        ScrollViewer viewport = journal.GetLogicalDescendants().OfType<ScrollViewer>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The journal window's list is not in a ScrollViewer. The dialog's layout changed - " +
                "update the scenario.");

        // DesiredSize, not Bounds: the ItemsControl stretches to fill its scroll viewport, so its
        // Bounds are the viewport's and the difference would always be zero. DesiredSize is what it
        // asked for when the ScrollViewer measured it against an unbounded height - i.e. the height
        // the rows actually need.
        double needed = entries.DesiredSize.Height;

        // A little air under the last row, so the list does not look clipped.
        const double Breathing = 12;
        double surplus = viewport.Bounds.Height - needed - Breathing;
        if (surplus <= 0)
        {
            return;
        }

        journal.Height -= surplus;
        context.Driver.Pump(5);

        if (viewport.Bounds.Height < needed)
        {
            throw new InvalidOperationException(
                $"Trimming the journal window to {journal.Height:0} left its list scrolling " +
                $"({needed:0}px of entries in a {viewport.Bounds.Height:0}px viewport), so the " +
                "capture would cut entries off. Leave the window at its natural size instead.");
        }
    }
}

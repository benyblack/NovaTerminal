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

    /// <summary>One entry per <c>sendInput</c> below; the assertions below are written against it.</summary>
    private const int ExpectedJournalEntries = 2;

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
                "headed 'Recent actions taken (or attempted) by AI agents', listing two " +
                "'sendInput [ok] Demo' rows with timestamps and a pane id.");

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

        await SendAsAgentAsync(context, pane, "git log --graph --oneline -5");
        await SendAsAgentAsync(context, pane, "bash scripts/demo-test.sh");

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
            context.CaptureOther(journal, "journal");
        }
        finally
        {
            // Also what lets the click handler's `await dialog.ShowDialog(this)` finish.
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
        var request = new AgentHostRequest
        {
            Version = AgentHostProtocol.Version,
            Id = Interlocked.Increment(ref _requestId),
            Method = AgentHostProtocol.Methods.SendInput,
            Params = JsonSerializer.SerializeToElement(
                new SendInputParams { PaneId = pane.PaneId, Text = command, Submit = true },
                AgentHostJsonContext.Default.SendInputParams)
        };

        string line = JsonSerializer.Serialize(request, AgentHostJsonContext.Default.AgentHostRequest);

        AgentHostResponse response = await AgentHostService.Instance
            .HandleRequestLineAsync(line, CancellationToken.None);

        if (response.Error is not null)
        {
            throw new InvalidOperationException(
                $"agent sendInput was rejected: {response.Error.Code} {response.Error.Message}. " +
                "Act is probably still disabled, or the pane is not registered.");
        }
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

        Window journal = context.Window.OwnedWindows.FirstOrDefault(
                window => string.Equals(window.Title, JournalWindowTitle, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Clicking the agent observe light opened no '{JournalWindowTitle}' window. Its " +
                "Click handler swallows exceptions, so the failure is upstream in " +
                "MainWindow.ShowAgentActivityJournalAsync.");

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

        return journal;
    }
}

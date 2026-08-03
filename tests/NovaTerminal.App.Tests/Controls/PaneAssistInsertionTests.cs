using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.ViewModels;
using NovaTerminal.Controls;
using NovaTerminal.Pty;
using NovaTerminal.Replay;
using NovaTerminal.Shell;
using NovaTerminal.VT;
using Xunit;

namespace NovaTerminal.Tests.Controls;

/// <summary>
/// What <c>Ctrl+Enter</c> is allowed to send, driven through the real pane: parser, mark, grid
/// reader, controller gate, insertion planner and the session it would send to.
/// </summary>
/// <remarks>
/// <para>
/// Two failures found in the Phase 1c review live here, and they are the two ends of the same
/// method. One is about sending the <em>wrong</em> text (the echo race); the other is about
/// destroying the surface while sending <em>no</em> text (accept-before-plan).
/// </para>
/// <para>
/// Sibling coverage: <c>CommandAssistInsertionPlannerTests</c> pins the planner's own refusal rules
/// against snapshots, and <c>PaneGridTruthDesyncTests</c> pins the query the planner is fed. This
/// file is about the order the pane does things in and what it refuses to do.
/// </para>
/// </remarks>
public class PaneAssistInsertionTests
{
    private const string PromptStart = "\x1b]133;A\x07";
    private const string PromptEnd = "\x1b]133;B\x07";

    /// <summary>
    /// The echo race. Rows were ranked from <c>"git st"</c>; the user typed <c>'a'</c>, so the PTY
    /// holds <c>"git sta"</c>, and pressed <c>Ctrl+Enter</c> before the shell echoed it. A fresh
    /// read still says <c>"git st"</c> - and it is a perfectly self-consistent read: the cursor is
    /// at the end, the line is single-line, no right prompt was trimmed, so every planner guard
    /// passes. The planner would send <c>"atus"</c> and the line would become <c>git staatus</c>.
    /// No prefix check can catch this, because stale text is always a prefix of the true line; the
    /// only signal is "we have sent bytes the grid has not seen come back yet".
    /// </summary>
    [AvaloniaFact]
    public async Task WhenTypedInputHasNotBeenEchoedYet_CtrlEnterRefusesUntilItIs()
    {
        using var fixture = await Fixture.AtAnIntegratedPromptAsync("git st", history: "git status");

        // The keystroke has reached the PTY; the echo has not come back.
        fixture.Pane.NoteInputAwaitingEcho();

        fixture.PressCtrlEnter();

        Assert.Empty(fixture.Session.Sent);
        Assert.True(fixture.ViewModel.IsVisible);

        // The echo lands and the grid catches up. (The pane clears the flag after Parser.Process,
        // which is what the production output hook does.)
        fixture.Pane.NoteSessionOutputApplied();

        fixture.PressCtrlEnter();

        Assert.Equal("atus", Assert.Single(fixture.Session.Sent));
    }

    /// <summary>
    /// The baseline the test above is measured against: with the grid up to date, the same setup
    /// sends the delta. Without this, "refused" would be indistinguishable from "never worked".
    /// </summary>
    [AvaloniaFact]
    public async Task WhenTheGridIsCurrent_CtrlEnterSendsOnlyTheSuffix()
    {
        using var fixture = await Fixture.AtAnIntegratedPromptAsync("git st", history: "git status");

        fixture.PressCtrlEnter();

        Assert.Equal("atus", Assert.Single(fixture.Session.Sent));
        Assert.False(fixture.ViewModel.IsVisible);
    }

    // ------------------------------------------------- accept on Enter (V2 Phase 3a)

    /// <summary>
    /// The owner's first report, end to end through the real pane: browse to a row, press
    /// <c>Enter</c>, and the suffix is sent. Before this, <c>Enter</c> went to the shell, submitted
    /// the line and dismissed the popup on the way out - "lists history but does no action when I
    /// select".
    /// </summary>
    [AvaloniaFact]
    public async Task WhenBrowsingARow_PlainEnterInsertsInsteadOfSubmitting()
    {
        using var fixture = await Fixture.AtAnIntegratedPromptAsync("git st", history: "git status");

        // Down opens the popup over the bubble and selects a row: the browse state that owns Enter.
        Assert.True(fixture.PressDown());

        Assert.True(fixture.PressEnter());
        Assert.Equal("atus", Assert.Single(fixture.Session.Sent));
    }

    /// <summary>
    /// The typing flow, which the keyboard change must leave alone. With only the bubble up - no popup,
    /// no browse - <c>Enter</c> is not consumed, so it reaches the shell and submits the command the
    /// user typed.
    /// </summary>
    [AvaloniaFact]
    public async Task WhenOnlyTheBubbleIsUp_PlainEnterIsLeftToTheShell()
    {
        using var fixture = await Fixture.AtAnIntegratedPromptAsync("git st", history: "git status");

        Assert.False(fixture.ViewModel.IsPopupOpen);

        Assert.False(fixture.PressEnter());
        Assert.Empty(fixture.Session.Sent);
    }

    /// <summary>
    /// A refused insertion must not eat the key either. <c>Enter</c> is routed to the assist while
    /// browsing, but if the insertion cannot be planned the pane returns false so the shell still gets
    /// its Enter - a dead key would be a worse answer than the pre-Phase-3a behavior.
    /// </summary>
    [AvaloniaFact]
    public async Task WhenBrowsingButInsertionIsRefused_EnterFallsThroughToTheShell()
    {
        using var fixture = await Fixture.AtAnIntegratedPromptAsync("git st", history: "git status");
        Assert.True(fixture.PressDown());

        // The echo race: bytes are in flight, so no insertion may be planned.
        fixture.Pane.NoteInputAwaitingEcho();

        Assert.False(fixture.PressEnter());
        Assert.Empty(fixture.Session.Sent);
        Assert.True(fixture.ViewModel.IsVisible);
    }

    // -------------------------------------- degraded-session insertion (V2 Phase 3a)

    /// <summary>
    /// The narrowing of the Phase 1c "browse-only in degraded sessions" rule, and the owner's report
    /// it answers: <c>Ctrl+R</c> on a markless pane listed history and nothing could be done with it.
    /// </summary>
    /// <remarks>
    /// There is still no grid snapshot here. What changed is that "no snapshot" stopped meaning "the
    /// prefix is unknown" in the one case where the pane can prove otherwise: the markless accumulator
    /// was reset by the last Enter and has observed nothing since, so the line is empty and the whole
    /// command may be sent. See <c>TerminalPane.TryReadInsertionQuerySnapshot</c>.
    /// </remarks>
    [AvaloniaFact]
    public async Task InADegradedSessionWithAProvablyEmptyLine_CtrlEnterSendsTheWholeCommand()
    {
        using var fixture = await Fixture.DegradedAtAHistorySearchAsync(history: "git status");

        fixture.PressCtrlEnter();

        Assert.Equal("git status", Assert.Single(fixture.Session.Sent));
    }

    /// <summary>The same, through the new Enter path rather than <c>Ctrl+Enter</c>.</summary>
    [AvaloniaFact]
    public async Task InADegradedSessionWithAProvablyEmptyLine_EnterOnASelectionSendsTheWholeCommand()
    {
        using var fixture = await Fixture.DegradedAtAHistorySearchAsync(history: "git status");

        Assert.True(fixture.PressDown());

        Assert.True(fixture.PressEnter());
        Assert.Equal("git status", Assert.Single(fixture.Session.Sent));
    }

    /// <summary>
    /// The gate's first half. The user typed two characters and then opened history: the line is no
    /// longer empty, the pane cannot see what is on it, and appending a whole command to it is how
    /// <c>gigit status</c> happens. Refuse - and, as before, without tearing the list down.
    /// </summary>
    /// <remarks>
    /// The echo flag is cleared deliberately. Typing sets it, and it would refuse on its own; clearing
    /// it leaves the accumulator's "not empty" as the only reason this refuses, which is the property
    /// under test.
    /// </remarks>
    [AvaloniaFact]
    public async Task InADegradedSessionAfterTyping_CtrlEnterRefusesWithoutTearingTheListDown()
    {
        using var fixture = await Fixture.DegradedAtAHistorySearchAsync(history: "git status");

        fixture.Pane.NotifyTypedTextObserved("gi");
        fixture.Pane.NoteSessionOutputApplied();

        fixture.PressCtrlEnter();

        Assert.Empty(fixture.Session.Sent);
        Assert.True(fixture.ViewModel.IsVisible);
        Assert.True(fixture.ViewModel.HasSuggestions);
    }

    /// <summary>
    /// The gate's second half. <c>Home</c> is not an assist-owned key and is not one the accumulator can
    /// model, so it poisons: the accumulator's buffer is still empty but its answer is now "I cannot say
    /// what is on this line", and an empty buffer must not be read as an empty line.
    /// </summary>
    [AvaloniaFact]
    public async Task InADegradedSessionWithAPoisonedLine_CtrlEnterRefuses()
    {
        using var fixture = await Fixture.DegradedAtAHistorySearchAsync(history: "git status");

        Assert.False(fixture.Pane.TryHandleCommandAssistKey(Key.Home, KeyModifiers.None));

        fixture.PressCtrlEnter();

        Assert.Empty(fixture.Session.Sent);
        Assert.True(fixture.ViewModel.IsVisible);
        Assert.True(fixture.ViewModel.HasSuggestions);
    }

    /// <summary>
    /// The gate's third half: the echo race applies to the degraded path too. A keystroke the shell has
    /// not echoed means the pane's belief about the line is behind the shell's, whatever the accumulator
    /// says.
    /// </summary>
    [AvaloniaFact]
    public async Task InADegradedSessionWithUnechoedInput_CtrlEnterRefuses()
    {
        using var fixture = await Fixture.DegradedAtAHistorySearchAsync(history: "git status");

        fixture.Pane.NoteInputAwaitingEcho();

        fixture.PressCtrlEnter();

        Assert.Empty(fixture.Session.Sent);
        Assert.True(fixture.ViewModel.IsVisible);
    }

    // ---------------------------------------------------------------- mouse (V2 Phase 3a)

    /// <summary>
    /// A single click selects the row it landed on and nothing more - browsing with the mouse must not
    /// commit an edit to the command line.
    /// </summary>
    [AvaloniaFact]
    public async Task PointerSelect_SelectsTheRowWithoutSendingAnything()
    {
        using var fixture = await Fixture.AtAnIntegratedPromptAsync("git st", history: "git status");

        fixture.Pane.OnCommandAssistSuggestionPointerSelected(0);

        Assert.True(fixture.ViewModel.IsPopupOpen);
        Assert.Equal(0, fixture.ViewModel.SelectedIndex);
        Assert.Empty(fixture.Session.Sent);
    }

    /// <summary>
    /// A double click - or a click on the already-selected row - runs the same insertion path
    /// <c>Ctrl+Enter</c> does, gate for gate.
    /// </summary>
    [AvaloniaFact]
    public async Task PointerAccept_RunsTheSameInsertionPathAsCtrlEnter()
    {
        using var fixture = await Fixture.AtAnIntegratedPromptAsync("git st", history: "git status");

        fixture.Pane.OnCommandAssistSuggestionPointerAccepted(0);

        Assert.Equal("atus", Assert.Single(fixture.Session.Sent));
        Assert.False(fixture.ViewModel.IsVisible);
    }

    /// <summary>A pointer accept obeys the echo gate, exactly as the keyboard path does.</summary>
    [AvaloniaFact]
    public async Task PointerAccept_WhenInputIsUnechoed_SendsNothing()
    {
        using var fixture = await Fixture.AtAnIntegratedPromptAsync("git st", history: "git status");
        fixture.Pane.NoteInputAwaitingEcho();

        fixture.Pane.OnCommandAssistSuggestionPointerAccepted(0);

        Assert.Empty(fixture.Session.Sent);
        Assert.True(fixture.ViewModel.IsVisible);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;

        private Fixture(TerminalPane pane, RecordingSession session, string directory)
        {
            Pane = pane;
            Session = session;
            _directory = directory;
        }

        public TerminalPane Pane { get; }

        public RecordingSession Session { get; }

        public CommandAssistBarViewModel ViewModel =>
            Assert.IsType<CommandAssistBarViewModel>(Pane.CommandAssistViewModel);

        /// <summary>An instrumented prompt with <paramref name="commandLine"/> typed at it.</summary>
        public static async Task<Fixture> AtAnIntegratedPromptAsync(string commandLine, string history)
        {
            Fixture fixture = await CreateAsync(history);
            fixture.Pane.ArmShellIntegrationTracker();
            fixture.Pane.CreateAndWireParser();
            fixture.Pane.Parser!.Process(PromptStart + "$ " + PromptEnd + commandLine);

            // The shell-integration dispatcher is serialized and asynchronous; B opens the
            // lifecycle gate on the far side of it.
            await Task.Delay(50);

            // An explicit session is what widens the suggestion scope back out to history.
            fixture.Pane.ToggleCommandAssist();
            await fixture.WaitForAsync(() => fixture.ViewModel.TopSuggestionText == history);
            return fixture;
        }

        /// <summary>No marks at all, with the recency list up from <c>Ctrl+R</c>.</summary>
        public static async Task<Fixture> DegradedAtAHistorySearchAsync(string history)
        {
            Fixture fixture = await CreateAsync(history);
            fixture.Pane.CreateAndWireParser();

            fixture.Pane.OpenCommandAssistHistorySearch();
            await fixture.WaitForAsync(() => fixture.ViewModel.HasSuggestions);
            return fixture;
        }

        public void PressCtrlEnter() =>
            Pane.TryHandleCommandAssistKey(Key.Enter, KeyModifiers.Control);

        /// <summary>Returns whether Command Assist consumed the key, i.e. whether the shell saw it.</summary>
        public bool PressEnter() =>
            Pane.TryHandleCommandAssistKey(Key.Enter, KeyModifiers.None);

        public bool PressDown() =>
            Pane.TryHandleCommandAssistKey(Key.Down, KeyModifiers.None);

        public void Dispose()
        {
            Pane.Dispose();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch
            {
                // Best effort; the temp root is per-test anyway.
            }
        }

        private static async Task<Fixture> CreateAsync(string history)
        {
            // A private services graph rather than the shared TestCommandAssistServices instance:
            // these tests assert on which row is selected, so they must not race whatever other
            // pane-level tests have written into the shared history file.
            string directory = Path.Combine(
                Path.GetTempPath(),
                $"nova_assist_insertion_{Environment.ProcessId}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);

            var services = new CommandAssistServices(
                Path.Combine(directory, "history.jsonl"),
                legacyHistoryFilePath: null,
                Path.Combine(directory, "snippets.json"),
                () => directory);

            // Awaited, never blocked on: these tests run on Avalonia's headless dispatcher thread,
            // and a GetAwaiter().GetResult() here deadlocks the store's continuation against it.
            await services.HistoryStore.AppendAsync(new CommandHistoryEntry(
                Id: Guid.NewGuid().ToString("N"),
                CommandText: history,
                ExecutedAt: DateTimeOffset.UtcNow,
                ShellKind: "pwsh",
                WorkingDirectory: null,
                ProfileId: null,
                SessionId: null,
                HostId: null,
                ExitCode: 0,
                IsRemote: false,
                IsRedacted: false,
                Source: CommandCaptureSource.Heuristic,
                DurationMs: null));

            var pane = new TerminalPane();
            pane.CommandAssistServices = services;
            var settings = new TerminalSettings(); // constructed, not Load() - see #232
            settings.CommandAssistEnabled = true;
            settings.CommandAssistHistoryEnabled = true;
            pane.ApplySettings(settings);

            var session = new RecordingSession();
            typeof(TerminalPane)
                .GetProperty("Session", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                .SetValue(pane, session);

            return new Fixture(pane, session, directory);
        }

        private async Task WaitForAsync(Func<bool> predicate, int timeoutMs = 2000)
        {
            int elapsed = 0;
            while (!predicate())
            {
                if (elapsed >= timeoutMs)
                {
                    throw new TimeoutException(
                        $"Timed out. query='{ViewModel.QueryText}', top='{ViewModel.TopSuggestionText}', " +
                        $"visible={ViewModel.IsVisible}, rows={ViewModel.Suggestions.Count}.");
                }

                await Task.Delay(10);
                elapsed += 10;
            }
        }
    }

    /// <summary>A session that records what the pane sends and does nothing else.</summary>
    internal sealed class RecordingSession : ITerminalSession
    {
        private readonly List<string> _sent = new();

        public IReadOnlyList<string> Sent => _sent;

        public Guid Id { get; } = Guid.NewGuid();
        public string ShellCommand => "pwsh.exe";
        public string? ShellArguments => null;
        public bool IsProcessRunning => true;
        public bool HasActiveChildProcesses => false;
        public int? ExitCode => null;
        public bool IsRecording => false;
        public bool IsFlightRecording => false;

        public event Action<string>? OnOutputReceived { add { } remove { } }

        public event Action<int>? OnExit { add { } remove { } }

        public void SendInput(string input) => _sent.Add(input);

        public void Resize(int cols, int rows) { }

        public void StartRecording(string filePath) { }

        public void StopRecording() { }

        public void EnableFlightRecording(long maxTotalBytes) { }

        public void DisableFlightRecording() { }

        public bool TryExportFlightRecording(string filePath, out FlightExportInfo info)
        {
            info = default;
            return false;
        }

        public void AttachBuffer(TerminalBuffer buffer) { }

        public void TakeSnapshot() { }

        public void Dispose() { }
    }
}

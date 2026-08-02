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

    /// <summary>
    /// Destructive refusal. In a degraded session there is no snapshot, so the planner refuses -
    /// but the pane used to call <c>TryAcceptSelection</c> (which accepts <em>and</em> dismisses)
    /// before asking it, so the refusal arrived after the list was already gone. From the user's
    /// side that is <c>Ctrl+Enter</c> silently deleting the thing they were browsing and sending
    /// nothing: indistinguishable from a broken feature. The plan is computed from the
    /// non-mutating read first; the surface is only touched once there is text to send.
    /// </summary>
    [AvaloniaFact]
    public async Task InADegradedSession_CtrlEnterRefusesWithoutTearingTheListDown()
    {
        using var fixture = await Fixture.DegradedAtAHistorySearchAsync(history: "git status");

        Assert.True(fixture.ViewModel.IsVisible);
        Assert.True(fixture.ViewModel.HasSuggestions);

        fixture.PressCtrlEnter();

        Assert.Empty(fixture.Session.Sent);
        Assert.True(fixture.ViewModel.IsVisible);
        Assert.True(fixture.ViewModel.HasSuggestions);
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

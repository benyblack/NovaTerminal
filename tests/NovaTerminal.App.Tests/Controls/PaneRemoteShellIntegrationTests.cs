using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.Controls;
using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests.Controls;

/// <summary>
/// An SSH pane whose remote shell emits OSC 133 gets the full Command Assist treatment
/// (V2 Phase 2b): history capture tagged <see cref="CommandCaptureSource.ShellIntegration"/>,
/// a readable grid-truth query between <c>B</c> and <c>C</c>, and structured exit codes from
/// <c>D</c>.
/// </summary>
/// <remarks>
/// <para>
/// The thing being tested is the <em>arming</em>, not the mark handling. Every mechanism these
/// tests exercise already worked; what did not exist before Phase 2b was any path that attached
/// the OSC 133 translator to a session Nova had not injected a bootstrap into, so an instrumented
/// remote delivered no events at all. Each positive test below is therefore paired with a control
/// that must stay degraded, because otherwise it would pass with the arming deleted (the pane's
/// heuristic capture would quietly stand in).
/// </para>
/// <para>
/// Sibling coverage: <c>PaneMarklessCaptureTests</c> owns the un-instrumented path,
/// <c>CapturePipelineTests</c> owns what the assist assembly does with the events, and
/// <c>NovaTerminal.VT.Tests/Osc133AcceptedPayloadTests</c> owns which <c>133;C</c> payloads become
/// command text.
/// </para>
/// </remarks>
public class PaneRemoteShellIntegrationTests
{
    private const string PromptStart = "\x1b]133;A\x07";
    private const string PromptEnd = "\x1b]133;B\x07";

    // ---- arming ------------------------------------------------------------------------------

    /// <summary>
    /// The Phase 2b seam itself. An SSH profile arms the translator; a local one does not, because
    /// a local session gets its tracker from the injection path instead and arming it twice would
    /// replace a tracker that is already mid-session.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(ConnectionType.SSH, true)]
    [InlineData(ConnectionType.Local, false)]
    public async Task ArmRemoteShellIntegrationTracker_OnlyArmsSshSessions(
        ConnectionType type,
        bool expectArmed)
    {
        using var fixture = await Fixture.CreateAsync(type);
        fixture.Pane.ArmRemoteShellIntegrationTracker();
        fixture.Pane.CreateAndWireParser();

        await fixture.PromptAsync("git status");
        await fixture.AcceptAsync("git status");

        if (expectArmed)
        {
            CommandHistoryEntry entry = await fixture.WaitForSingleEntryAsync();
            Assert.Equal(CommandCaptureSource.ShellIntegration, entry.Source);
        }
        else
        {
            Assert.True(await fixture.NothingWasCapturedAsync());
        }
    }

    /// <summary>
    /// Turning shell integration off is the user's "do not participate in the OSC 133 contract"
    /// control, and it has to mean something remotely too - a remote host is the one place they
    /// cannot simply uninstall the emitter.
    /// </summary>
    [AvaloniaFact]
    public async Task ArmRemoteShellIntegrationTracker_RespectsTheShellIntegrationSetting()
    {
        using var fixture = await Fixture.CreateAsync(ConnectionType.SSH, shellIntegrationEnabled: false);
        fixture.Pane.ArmRemoteShellIntegrationTracker();
        fixture.Pane.CreateAndWireParser();

        await fixture.PromptAsync("git status");
        await fixture.AcceptAsync("git status");

        Assert.True(await fixture.NothingWasCapturedAsync());
    }

    /// <summary>
    /// Arming is unconditional and happens before any mark has arrived, so the case that has to be
    /// pinned is that it changes nothing for a host with no snippet installed. Every path into the
    /// tracker is a mark callback, so a silent remote behaves exactly as it did before Phase 2b.
    /// </summary>
    [AvaloniaFact]
    public async Task OnAnArmedButMarklessSshPane_TheHeuristicPathStillOwnsCapture()
    {
        using var fixture = await Fixture.CreateAsync(ConnectionType.SSH);
        fixture.Pane.ArmRemoteShellIntegrationTracker();
        fixture.Pane.CreateAndWireParser();

        fixture.Type("uname -a");
        fixture.PressEnter();

        CommandHistoryEntry entry = await fixture.WaitForSingleEntryAsync();
        Assert.Equal("uname -a", entry.CommandText);
        Assert.Equal(CommandCaptureSource.Heuristic, entry.Source);
        Assert.True(entry.IsRemote);
    }

    // ---- history capture over SSH ----------------------------------------------------------------

    /// <summary>
    /// The headline claim: a command run on an instrumented remote reaches history through the
    /// structured path, with the remote host recorded on it.
    /// </summary>
    [AvaloniaFact]
    public async Task OnAnInstrumentedSshPane_CommandsAreCapturedFromTheMarkPayload()
    {
        using var fixture = await Fixture.CreateAsync(ConnectionType.SSH);
        fixture.Pane.ArmRemoteShellIntegrationTracker();
        fixture.Pane.CreateAndWireParser();

        await fixture.PromptAsync("docker ps -a");
        await fixture.AcceptAsync("docker ps -a");

        CommandHistoryEntry entry = await fixture.WaitForSingleEntryAsync();
        Assert.Equal("docker ps -a", entry.CommandText);
        Assert.Equal(CommandCaptureSource.ShellIntegration, entry.Source);
        Assert.True(entry.IsRemote);
        Assert.Equal("ubuntu.example", entry.HostId);
    }

    /// <summary>
    /// Redaction runs on the remote path too. Worth its own test because the structured path builds
    /// its entry separately from the heuristic one, and a remote host is where a credential in a
    /// command line is most likely.
    /// </summary>
    [AvaloniaFact]
    public async Task OnAnInstrumentedSshPane_CapturedCommandsAreRedacted()
    {
        using var fixture = await Fixture.CreateAsync(ConnectionType.SSH);
        fixture.Pane.ArmRemoteShellIntegrationTracker();
        fixture.Pane.CreateAndWireParser();

        await fixture.PromptAsync("x");
        await fixture.AcceptAsync("mysql -u root --password hunter2");

        CommandHistoryEntry entry = await fixture.WaitForSingleEntryAsync();
        Assert.Equal("mysql -u root --password [REDACTED]", entry.CommandText);
        Assert.True(entry.IsRedacted);
    }

    /// <summary>
    /// <c>133;D</c> patches the exit code and the duration onto the entry the same command's
    /// <c>C</c> created. This is Fix mode's input over SSH.
    /// </summary>
    [AvaloniaFact]
    public async Task OnAnInstrumentedSshPane_CommandFinishedPatchesTheExitCodeAndDuration()
    {
        using var fixture = await Fixture.CreateAsync(ConnectionType.SSH);
        fixture.Pane.ArmRemoteShellIntegrationTracker();
        fixture.Pane.CreateAndWireParser();

        await fixture.PromptAsync("make");
        await fixture.AcceptAsync("make");
        await fixture.FinishAsync(exitCode: 2, durationMs: 1500);

        CommandHistoryEntry entry = await fixture.WaitForSingleEntryAsync();
        Assert.Equal(2, entry.ExitCode);
        Assert.Equal(1500, entry.DurationMs);
    }

    // ---- grid truth over SSH -----------------------------------------------------------------------

    /// <summary>
    /// The query gate follows the marks, not the session type. Between <c>B</c> and <c>C</c> the
    /// command line painted on an SSH pane's grid is the query; after <c>C</c> it is not, because
    /// the shell is running the command and what is below the mark is turning into its output.
    /// </summary>
    /// <remarks>
    /// Asked through the gated accessor rather than the raw grid seam. The mark deliberately
    /// survives <c>C</c> (it is dropped on <c>D</c>), so the seam still answers here and only the
    /// lifecycle gate makes the answer inadmissible - which is exactly the thing under test.
    /// </remarks>
    [AvaloniaFact]
    public async Task OnAnInstrumentedSshPane_TheGridIsTheQueryBetweenBAndC()
    {
        using var fixture = await Fixture.CreateAsync(ConnectionType.SSH);
        fixture.Pane.ArmRemoteShellIntegrationTracker();
        fixture.Pane.CreateAndWireParser();

        await fixture.PromptAsync("kubectl get pods");

        Assert.Equal("kubectl get pods", fixture.Pane.TryReadGatedAssistQuerySnapshotForTest()?.Text);

        await fixture.AcceptAsync("kubectl get pods");

        Assert.Null(fixture.Pane.TryReadGatedAssistQuerySnapshotForTest());
    }

    /// <summary>The control: no marks, no query - exactly as before Phase 2b.</summary>
    [AvaloniaFact]
    public async Task OnAMarklessSshPane_ThereIsNoQuery()
    {
        using var fixture = await Fixture.CreateAsync(ConnectionType.SSH);
        fixture.Pane.ArmRemoteShellIntegrationTracker();
        fixture.Pane.CreateAndWireParser();

        fixture.Echo("$ kubectl get pods");
        fixture.PressEnter();
        await Task.Delay(50);

        Assert.Null(fixture.Pane.TryReadGatedAssistQuerySnapshotForTest());
        Assert.Null(fixture.Pane.TryReadAssistQuerySnapshot());
    }

    // ---- the bare C over SSH ------------------------------------------------------------------------

    /// <summary>
    /// A third-party remote integration that emits <c>133;C</c> with no payload. The gate must still
    /// close on <c>C</c> - if it did not, the grid reader would serve the running command's output
    /// as a live command line for as long as the command took to run.
    /// </summary>
    [AvaloniaFact]
    public async Task OnAnSshPaneEmittingABareC_TheQueryGateStillCloses()
    {
        using var fixture = await Fixture.CreateAsync(ConnectionType.SSH);
        fixture.Pane.ArmRemoteShellIntegrationTracker();
        fixture.Pane.CreateAndWireParser();

        await fixture.PromptAsync("tail -f /var/log/syslog");
        Assert.NotNull(fixture.Pane.TryReadGatedAssistQuerySnapshotForTest());

        await fixture.BareAcceptAsync();

        Assert.Null(fixture.Pane.TryReadGatedAssistQuerySnapshotForTest());
    }

    /// <summary>
    /// ...and the command is still captured, once, by the Enter-time grid read, and still gets its
    /// exit code from <c>D</c>. Losing structured capture text is the whole cost of a bare-C
    /// integration; losing the history entry would not be acceptable.
    /// </summary>
    [AvaloniaFact]
    public async Task OnAnSshPaneEmittingABareC_TheCommandIsStillCapturedAndStillGetsItsExitCode()
    {
        using var fixture = await Fixture.CreateAsync(ConnectionType.SSH);
        fixture.Pane.ArmRemoteShellIntegrationTracker();
        fixture.Pane.CreateAndWireParser();

        await fixture.PromptAsync("apt update");
        fixture.PressEnter();
        await fixture.BareAcceptAsync();
        await fixture.FinishAsync(exitCode: 100, durationMs: 800);

        CommandHistoryEntry entry = await fixture.WaitForSingleEntryAsync();
        Assert.Equal("apt update", entry.CommandText);
        Assert.Equal(CommandCaptureSource.Heuristic, entry.Source);
        Assert.Equal(100, entry.ExitCode);
        Assert.Equal(800, entry.DurationMs);
    }

    /// <summary>
    /// A bare-C shell must not have the heuristic path stood down: it is the only source of history
    /// such a session has. Two commands in a row, both captured.
    /// </summary>
    [AvaloniaFact]
    public async Task OnAnSshPaneEmittingABareC_EveryCommandIsStillCaptured()
    {
        using var fixture = await Fixture.CreateAsync(ConnectionType.SSH);
        fixture.Pane.ArmRemoteShellIntegrationTracker();
        fixture.Pane.CreateAndWireParser();

        await fixture.PromptAsync("whoami");
        fixture.PressEnter();
        await fixture.BareAcceptAsync();
        await fixture.FinishAsync(exitCode: 0, durationMs: 10);

        await fixture.PromptAsync("hostname");
        fixture.PressEnter();
        await fixture.BareAcceptAsync();
        await fixture.FinishAsync(exitCode: 0, durationMs: 10);

        IReadOnlyList<CommandHistoryEntry> entries = await fixture.WaitForEntriesAsync(2);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.CommandText == "whoami");
        Assert.Contains(entries, e => e.CommandText == "hostname");
        Assert.All(entries, e => Assert.Equal(CommandCaptureSource.Heuristic, e.Source));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        private readonly IHistoryStore _historyStore;

        private Fixture(TerminalPane pane, IHistoryStore historyStore, string directory)
        {
            Pane = pane;
            _historyStore = historyStore;
            _directory = directory;
        }

        public TerminalPane Pane { get; }

        public static async Task<Fixture> CreateAsync(
            ConnectionType type,
            bool shellIntegrationEnabled = true)
        {
            // A private services graph: several of these tests assert that the store is *empty*,
            // so they cannot share a history file with anything else running.
            string directory = Path.Combine(
                Path.GetTempPath(),
                $"nova_remote_integration_{Environment.ProcessId}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);

            var services = new CommandAssistServices(
                Path.Combine(directory, "history.jsonl"),
                legacyHistoryFilePath: null,
                Path.Combine(directory, "snippets.json"),
                () => directory);

            await services.HistoryStore.GetRecentAsync(1);

            var pane = new TerminalPane();
            pane.CommandAssistServices = services;
            var settings = new TerminalSettings(); // constructed, not Load() - see #232
            settings.CommandAssistEnabled = true;
            settings.CommandAssistHistoryEnabled = true;
            settings.CommandAssistShellIntegrationEnabled = shellIntegrationEnabled;
            pane.ApplySettings(settings);
            pane.UpdateProfile(new TerminalProfile
            {
                Type = type,
                Command = type == ConnectionType.SSH ? "ssh.exe" : "pwsh.exe",
                SshHost = type == ConnectionType.SSH ? "ubuntu.example" : string.Empty,
            });

            var session = new PaneAssistInsertionTests.RecordingSession();
            typeof(TerminalPane)
                .GetProperty("Session", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                .SetValue(pane, session);

            return new Fixture(pane, services.HistoryStore, directory);
        }

        /// <summary>A remote prompt with <paramref name="commandLine"/> painted at it.</summary>
        public async Task PromptAsync(string commandLine)
        {
            Echo(PromptStart + "user@ubuntu:~$ " + PromptEnd + commandLine);
            await SettleAsync();
        }

        /// <summary>The remote shell reporting the accepted command, base64 as Nova's snippets do.</summary>
        public async Task AcceptAsync(string commandText)
        {
            string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(commandText));
            Echo($"\x1b]133;C;{encoded}\x07");
            await SettleAsync();
        }

        /// <summary>A payload-less <c>133;C</c>, as iTerm2's and VS Code's snippets emit.</summary>
        public async Task BareAcceptAsync()
        {
            Echo("\x1b]133;C\x07");
            await SettleAsync();
        }

        public async Task FinishAsync(int exitCode, long durationMs)
        {
            Echo($"\x1b]133;D;{exitCode};{durationMs}\x07");
            await SettleAsync();
        }

        public void Echo(string text) => Pane.Parser!.Process(text);

        public void Type(string text)
        {
            foreach (char c in text)
            {
                Pane.TryHandleCommandAssistKey(KeyForCharacter(c), KeyModifiers.None);
                Pane.NotifyTypedTextObserved(c.ToString());
                Echo(c.ToString());
            }
        }

        public void PressEnter()
        {
            Pane.TryHandleCommandAssistKey(Key.Enter, KeyModifiers.None);
            Pane.OnCommandAssistEnterObserved();
        }

        /// <summary>
        /// The shell-integration dispatcher is serialized and asynchronous, and the history store
        /// writes behind it.
        /// </summary>
        private static Task SettleAsync() => Task.Delay(50);

        public async Task<CommandHistoryEntry> WaitForSingleEntryAsync(int timeoutMs = 2000)
        {
            IReadOnlyList<CommandHistoryEntry> entries = await WaitForEntriesAsync(1, timeoutMs);
            return Assert.Single(entries);
        }

        public async Task<IReadOnlyList<CommandHistoryEntry>> WaitForEntriesAsync(
            int count,
            int timeoutMs = 2000)
        {
            int elapsed = 0;
            while (true)
            {
                IReadOnlyList<CommandHistoryEntry> entries = await _historyStore.GetRecentAsync(10);
                if (entries.Count >= count)
                {
                    // One more settle so a second, unwanted entry has a chance to show up and fail
                    // an Assert.Single rather than racing it.
                    await Task.Delay(50);
                    return await _historyStore.GetRecentAsync(10);
                }

                if (elapsed >= timeoutMs)
                {
                    throw new TimeoutException(
                        $"Expected {count} history entries; the store has {entries.Count}.");
                }

                await Task.Delay(10);
                elapsed += 10;
            }
        }

        public async Task<bool> NothingWasCapturedAsync()
        {
            await Task.Delay(200);
            IReadOnlyList<CommandHistoryEntry> entries = await _historyStore.GetRecentAsync(10);
            return entries.Count == 0;
        }

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

        private static Key KeyForCharacter(char c) => c switch
        {
            >= 'a' and <= 'z' => Key.A + (c - 'a'),
            >= 'A' and <= 'Z' => Key.A + (c - 'A'),
            >= '0' and <= '9' => Key.D0 + (c - '0'),
            ' ' => Key.Space,
            '-' => Key.OemMinus,
            '.' => Key.OemPeriod,
            ',' => Key.OemComma,
            '/' => Key.Oem2,
            '\\' => Key.Oem5,
            ':' or ';' => Key.Oem1,
            '\'' or '"' => Key.Oem7,
            '|' => Key.Oem5,
            _ => Key.Oem8,
        };
    }
}

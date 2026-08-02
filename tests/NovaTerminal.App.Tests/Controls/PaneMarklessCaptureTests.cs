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
/// Enter-time history capture in sessions the grid cannot serve (V2 Phase 1, task 7): `cmd.exe`,
/// shells whose integration bootstrap bailed out, and every un-instrumented SSH host.
/// </summary>
/// <remarks>
/// <para>
/// The property under test is not "the accumulator captures the line". It is the pair: it captures
/// <em>exactly</em> the typed line, or it captures <em>nothing</em>. Phase 1c deleted V1's shadow
/// buffer precisely because its third outcome — capturing something plausible and wrong — put
/// commands the user never ran into permanent history. Most of the cases below are therefore
/// "and nothing was written".
/// </para>
/// <para>
/// Sibling coverage: <c>PaneGridTruthDesyncTests</c> covers the instrumented path this one falls
/// back from, and <c>CapturePipelineTests</c> covers what the assist assembly does with the string
/// once it has one. This file is about which string the pane hands over.
/// </para>
/// </remarks>
public class PaneMarklessCaptureTests
{
    private const string PromptStart = "\x1b]133;A\x07";
    private const string PromptEnd = "\x1b]133;B\x07";

    /// <summary>
    /// The whole point of the task: a straight-through-typed line in a session with no marks reaches
    /// history, verbatim and redacted. The secret is here rather than in its own test because the
    /// interesting claim is that the accumulator feeds the <em>normal</em> capture pipeline, redaction
    /// and all, rather than a side door into the store.
    /// </summary>
    [AvaloniaFact]
    public async Task InAMarklessSession_ATypedLineIsCapturedVerbatimAndRedacted()
    {
        using var fixture = await Fixture.MarklessAsync();

        fixture.Type("mysql -u root --password hunter2");
        fixture.PressEnter();

        CommandHistoryEntry entry = await fixture.WaitForSingleEntryAsync();
        Assert.Equal("mysql -u root --password [REDACTED]", entry.CommandText);
        Assert.True(entry.IsRedacted);
        Assert.Equal(CommandCaptureSource.Heuristic, entry.Source);
    }

    /// <summary>
    /// Every key the accumulator cannot model turns it off. These are the edits that made V1 wrong:
    /// an arrow moves the insertion point so later characters do not land where the buffer thinks;
    /// `Home` does the same wholesale; `Tab` invites the shell to rewrite the word; `Delete` removes
    /// a character the buffer never sees go; `Ctrl+W` deletes a word; `Up` replaces the entire line
    /// with one the user typed no character of.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Key.Left, KeyModifiers.None)]
    [InlineData(Key.Right, KeyModifiers.None)]
    [InlineData(Key.Up, KeyModifiers.None)]
    [InlineData(Key.Down, KeyModifiers.None)]
    [InlineData(Key.Home, KeyModifiers.None)]
    [InlineData(Key.End, KeyModifiers.None)]
    [InlineData(Key.Delete, KeyModifiers.None)]
    [InlineData(Key.Tab, KeyModifiers.None)]
    [InlineData(Key.PageUp, KeyModifiers.None)]
    [InlineData(Key.PageDown, KeyModifiers.None)]
    [InlineData(Key.Insert, KeyModifiers.None)]
    [InlineData(Key.Escape, KeyModifiers.None)]
    [InlineData(Key.F7, KeyModifiers.None)]
    [InlineData(Key.W, KeyModifiers.Control)]
    [InlineData(Key.U, KeyModifiers.Control)]
    [InlineData(Key.A, KeyModifiers.Control)]
    [InlineData(Key.B, KeyModifiers.Alt)]
    public async Task InAMarklessSession_AnUnmodeledKeyStopsTheLineBeingCaptured(Key key, KeyModifiers modifiers)
    {
        using var fixture = await Fixture.MarklessAsync();

        fixture.Type("git stauts");
        fixture.PressKey(key, modifiers);
        fixture.Type("x");
        fixture.PressEnter();

        Assert.True(await fixture.NothingWasCapturedAsync());
    }

    /// <summary>Backspace is the one edit besides typing that the accumulator does model.</summary>
    [AvaloniaFact]
    public async Task InAMarklessSession_BackspaceRemovesTheLastCharacter()
    {
        using var fixture = await Fixture.MarklessAsync();

        fixture.Type("gitt");
        fixture.PressBackspace();
        fixture.PressEnter();

        CommandHistoryEntry entry = await fixture.WaitForSingleEntryAsync();
        Assert.Equal("git", entry.CommandText);
    }

    /// <summary>
    /// Paste poisons, and the poison outlives the keystrokes that follow it: the pasted characters
    /// are still on the line and the accumulator still cannot describe them.
    /// </summary>
    /// <remarks>
    /// Paste is covered twice over, by two mechanisms that are worth keeping distinct. The
    /// accumulator is <em>poisoned</em> — it did not see the characters. The session is separately
    /// marked <em>suppressed</em> (<c>AssistSessionStateMachine.IsCurrentSubmissionSuppressed</c>),
    /// which is a provenance claim that also applies to instrumented sessions, where the grid reads
    /// the pasted line perfectly well and it should still not be recorded as something the user
    /// typed. Here only the first is load-bearing; <c>CapturePipelineTests</c> owns the second.
    /// </remarks>
    [AvaloniaFact]
    public async Task InAMarklessSession_APasteStopsTheLineBeingCaptured()
    {
        using var fixture = await Fixture.MarklessAsync();

        fixture.Pane.NotifyPasteObserved("curl https://example.com");
        fixture.PressEnter();

        Assert.True(await fixture.NothingWasCapturedAsync());

        // ... and typing after the paste does not wash it out.
        fixture.Pane.NotifyPasteObserved("curl https://example.com");
        fixture.Type(" | jq");
        fixture.PressEnter();

        Assert.True(await fixture.NothingWasCapturedAsync());
    }

    /// <summary>The clipboard paste path is a different call site and poisons the same way.</summary>
    [AvaloniaFact]
    public async Task InAMarklessSession_AClipboardPasteStopsTheLineBeingCaptured()
    {
        using var fixture = await Fixture.MarklessAsync();

        fixture.Type("echo ");
        fixture.Pane.NotifyCommandAssistPaste("secret-value");
        fixture.PressEnter();

        Assert.True(await fixture.NothingWasCapturedAsync());
    }

    /// <summary>
    /// `Ctrl+C` abandons the line, so the next one starts clean. Without this the first unmodeled
    /// key would silently cost every subsequent command in the session, not just its own.
    /// </summary>
    [AvaloniaFact]
    public async Task InAMarklessSession_CtrlCClearsThePoisonForTheNextLine()
    {
        using var fixture = await Fixture.MarklessAsync();

        fixture.Type("git sta");
        fixture.PressKey(Key.Left);
        fixture.PressKey(Key.C, KeyModifiers.Control);

        fixture.Type("git status");
        fixture.PressEnter();

        CommandHistoryEntry entry = await fixture.WaitForSingleEntryAsync();
        Assert.Equal("git status", entry.CommandText);
    }

    /// <summary>
    /// Grid truth wins wherever it exists, and it does not consult the accumulator to decide. The
    /// line here is one the accumulator has given up on — the user arrowed around in it — and the
    /// capture is still exactly what is painted on screen.
    /// </summary>
    [AvaloniaFact]
    public async Task InAnIntegratedSession_TheGridWinsEvenWhenTheAccumulatorIsPoisoned()
    {
        using var fixture = await Fixture.IntegratedAsync("git status --short");

        // Everything the accumulator would have said is wrong or absent.
        fixture.Type("nonsense");
        fixture.PressKey(Key.Left);

        fixture.PressEnter();

        CommandHistoryEntry entry = await fixture.WaitForSingleEntryAsync();
        Assert.Equal("git status --short", entry.CommandText);
    }

    /// <summary>
    /// A full-screen app owns the keyboard and its keys are not line edits. The half-line pending
    /// when it started must not survive it — asserted after the TUI exits, so that what is being
    /// tested is the reset rather than <c>CapturePipeline</c>'s separate alt-screen gate.
    /// </summary>
    [AvaloniaFact]
    public async Task InAMarklessSession_AltScreenDiscardsThePendingLine()
    {
        using var fixture = await Fixture.MarklessAsync();

        fixture.Type("vim notes.txt");
        await fixture.EnterAltScreenAsync();
        await fixture.LeaveAltScreenAsync();

        fixture.PressEnter();

        Assert.True(await fixture.NothingWasCapturedAsync());
    }

    /// <summary>The control for the test above: without the TUI, the same line is captured.</summary>
    [AvaloniaFact]
    public async Task InAMarklessSession_TheSameLineWithoutAltScreenIsCaptured()
    {
        using var fixture = await Fixture.MarklessAsync();

        fixture.Type("vim notes.txt");
        fixture.PressEnter();

        CommandHistoryEntry entry = await fixture.WaitForSingleEntryAsync();
        Assert.Equal("vim notes.txt", entry.CommandText);
    }

    /// <summary>
    /// Bytes this pane's keyboard handling never produced — a broadcast from a sibling pane, the
    /// drop toast, the agent host's act surface — all land on <c>NotifyExternalInputSent</c>.
    /// </summary>
    [AvaloniaFact]
    public async Task InAMarklessSession_ExternallySentInputStopsTheLineBeingCaptured()
    {
        using var fixture = await Fixture.MarklessAsync();

        fixture.Type("echo ");
        fixture.Pane.NotifyExternalInputSent();
        fixture.PressEnter();

        Assert.True(await fixture.NothingWasCapturedAsync());
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

        /// <summary>A shell that emits no <c>OSC 133</c> at all.</summary>
        public static async Task<Fixture> MarklessAsync()
        {
            Fixture fixture = await CreateAsync();
            fixture.Pane.CreateAndWireParser();
            return fixture;
        }

        /// <summary>An instrumented prompt with <paramref name="commandLine"/> painted at it.</summary>
        public static async Task<Fixture> IntegratedAsync(string commandLine)
        {
            Fixture fixture = await CreateAsync();
            fixture.Pane.ArmShellIntegrationTracker();
            fixture.Pane.CreateAndWireParser();
            fixture.Pane.Parser!.Process(PromptStart + "$ " + PromptEnd + commandLine);

            // The shell-integration dispatcher is serialized and asynchronous; B opens the
            // lifecycle gate on the far side of it.
            await Task.Delay(50);
            return fixture;
        }

        public void Type(string text)
        {
            foreach (char c in text)
            {
                // One key press then one text-input event, in the order Avalonia raises them.
                PressKey(KeyForCharacter(c));
                Pane.NotifyTypedTextObserved(c.ToString());
            }
        }

        public void PressKey(Key key, KeyModifiers modifiers = KeyModifiers.None) =>
            Pane.TryHandleCommandAssistKey(key, modifiers);

        public void PressBackspace()
        {
            PressKey(Key.Back);
            Pane.NotifyBackspaceObserved();
        }

        public void PressEnter()
        {
            PressKey(Key.Enter);
            Pane.OnCommandAssistEnterObserved();
        }

        public async Task EnterAltScreenAsync()
        {
            Pane.Parser!.Process("\x1b[?1049h");
            await Task.Delay(50);
        }

        public async Task LeaveAltScreenAsync()
        {
            Pane.Parser!.Process("\x1b[?1049l");
            await Task.Delay(50);
        }

        /// <summary>The one entry that should have been written, waiting for the async capture.</summary>
        public async Task<CommandHistoryEntry> WaitForSingleEntryAsync(int timeoutMs = 2000)
        {
            int elapsed = 0;
            while (true)
            {
                IReadOnlyList<CommandHistoryEntry> entries = await _historyStore.GetRecentAsync(10);
                if (entries.Count > 0)
                {
                    return Assert.Single(entries);
                }

                if (elapsed >= timeoutMs)
                {
                    throw new TimeoutException("No history entry was captured.");
                }

                await Task.Delay(10);
                elapsed += 10;
            }
        }

        /// <summary>
        /// Nothing reached the store. Waits first, because the assertion is about an asynchronous
        /// write that did not happen: checking immediately would pass even if it were about to.
        /// </summary>
        public async Task<bool> NothingWasCapturedAsync()
        {
            await Task.Delay(150);
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

        private static async Task<Fixture> CreateAsync()
        {
            // A private services graph rather than the shared TestCommandAssistServices instance:
            // these tests assert that the store is *empty*, so they cannot share a history file
            // with whatever else is running.
            string directory = Path.Combine(
                Path.GetTempPath(),
                $"nova_markless_capture_{Environment.ProcessId}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);

            var services = new CommandAssistServices(
                Path.Combine(directory, "history.jsonl"),
                legacyHistoryFilePath: null,
                Path.Combine(directory, "snippets.json"),
                () => directory);

            // Force the store open before the pane runs, so the first read is not racing creation.
            await services.HistoryStore.GetRecentAsync(1);

            var pane = new TerminalPane();
            pane.CommandAssistServices = services;
            var settings = new TerminalSettings(); // constructed, not Load() - see #232
            settings.CommandAssistEnabled = true;
            settings.CommandAssistHistoryEnabled = true;
            pane.ApplySettings(settings);

            var session = new PaneAssistInsertionTests.RecordingSession();
            typeof(TerminalPane)
                .GetProperty("Session", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                .SetValue(pane, session);

            return new Fixture(pane, services.HistoryStore, directory);
        }

        /// <summary>
        /// The key press that accompanies <paramref name="c"/>. Only has to be right about the
        /// classification (printable, no chord modifier), which is all the accumulator reads.
        /// </summary>
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

using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using NovaTerminal.AgentHost;
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

        // Everything the accumulator would have said is wrong or absent. Deliberately unechoed:
        // the point is that the grid's answer is taken without the accumulator being consulted at
        // all, so what is painted must stay exactly what IntegratedAsync painted.
        fixture.Type("nonsense", echo: false);
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

    /// <summary>
    /// The security case, and the reason the echo gate exists at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TerminalView.OnTextInput</c> fires for every keystroke unconditionally — it cannot know
    /// whether the shell is echoing. So inside one markless session, `ssh host` submits and resets
    /// the accumulator, and then at the hidden `password:` prompt every character of the password
    /// is appended to a perfectly clean, unpoisoned accumulator, with no grid snapshot to outrank
    /// it. Without the gate, the password is written to `history.jsonl` verbatim:
    /// <c>SecretsFilter</c> is pattern-based, and a bare secret has no pattern.
    /// </para>
    /// <para>
    /// The distinguishing fact is on the screen. A visible markless prompt has the typed command
    /// painted on it — only the <c>OSC 133;B</c> mark is missing — and a no-echo prompt does not.
    /// </para>
    /// </remarks>
    [AvaloniaFact]
    public async Task InAMarklessSession_TextTheShellNeverEchoedIsNotCaptured()
    {
        using var fixture = await Fixture.MarklessAsync();

        // The command that opens the no-echo prompt is an ordinary, echoed, captured line.
        fixture.Type("ssh host");
        fixture.PressEnter();
        CommandHistoryEntry entry = await fixture.WaitForSingleEntryAsync();
        Assert.Equal("ssh host", entry.CommandText);

        // Then the prompt itself, and a password that never appears on the grid.
        fixture.Echo("\r\nhost's password: ");
        fixture.Type("hunter2", echo: false);
        fixture.PressEnter();

        Assert.True(await fixture.OnlyTheFirstEntryWasCapturedAsync("ssh host"));
    }

    /// <summary>
    /// Half an echo is not an echo. A shell that painted some of what was typed — a slow remote, a
    /// prompt that reprinted, a line the accumulator and the screen simply disagree about — is a
    /// case where the honest answer is "I do not know what was submitted".
    /// </summary>
    [AvaloniaFact]
    public async Task InAMarklessSession_APartiallyEchoedLineIsNotCaptured()
    {
        using var fixture = await Fixture.MarklessAsync();

        fixture.Type("git checkout ma", echo: false);
        fixture.Echo("git checkout m");
        fixture.PressEnter();

        Assert.True(await fixture.NothingWasCapturedAsync());
    }

    /// <summary>
    /// The gate compares against what is at the <em>cursor</em>, not anywhere on the row: text that
    /// happens to be on screen because it scrolled past earlier is not evidence that this line was
    /// echoed.
    /// </summary>
    [AvaloniaFact]
    public async Task InAMarklessSession_TextEchoedSomewhereElseOnTheRowIsNotCaptured()
    {
        using var fixture = await Fixture.MarklessAsync();

        fixture.Echo("whoami and then some");
        fixture.Type("whoami", echo: false);
        fixture.PressEnter();

        Assert.True(await fixture.NothingWasCapturedAsync());
    }

    /// <summary>
    /// `AltGr` on a non-US layout. Windows Avalonia reports it as `Control|Alt`, and the composed
    /// character arrives afterwards as an ordinary text-input event; treating the key press as an
    /// unowned `Ctrl` chord would poison the line, which on a German, French, Nordic, Turkish or
    /// Polish layout means losing the capture of any command containing `@`, `{`, `[`, `\`, `|` or
    /// `~` — i.e. most of them.
    /// </summary>
    [AvaloniaFact]
    public async Task InAMarklessSession_AnAltGrComposedCharacterIsCaptured()
    {
        using var fixture = await Fixture.MarklessAsync();

        fixture.Type("ssh user");

        // AltGr+Q on a German layout: the key press, then the WM_CHAR it composes.
        fixture.PressKey(Key.Q, KeyModifiers.Control | KeyModifiers.Alt);
        fixture.Pane.NotifyTypedTextObserved("@");
        fixture.Echo("@");

        fixture.Type("example.com");
        fixture.PressEnter();

        CommandHistoryEntry entry = await fixture.WaitForSingleEntryAsync();
        Assert.Equal("ssh user@example.com", entry.CommandText);
    }

    /// <summary>
    /// The other half of the `AltGr` carve-out: `Ctrl+Alt` plus a key that produces no text does
    /// reach the shell as a control byte, so it still poisons.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Key.Back)]
    [InlineData(Key.Enter)]
    [InlineData(Key.Tab)]
    [InlineData(Key.Escape)]
    public async Task InAMarklessSession_CtrlAltPlusANonTextKeyStopsTheLineBeingCaptured(Key key)
    {
        using var fixture = await Fixture.MarklessAsync();

        fixture.Type("git status");
        fixture.PressKey(key, KeyModifiers.Control | KeyModifiers.Alt);
        fixture.PressEnter();

        Assert.True(await fixture.NothingWasCapturedAsync());
    }

    /// <summary>
    /// `Ctrl+Backspace` and `Shift+Enter` are not the plain keys with a harmless modifier riding
    /// along. Under the kitty keyboard protocol's disambiguate tier `TerminalView` encodes them as
    /// CSI u and returns early, so `BackspaceObserved` / `EnterObserved` never fire and the
    /// accumulator keeps characters a kitty-aware editor has already deleted. The classifier
    /// refuses the modifier instead, at the cost of one capture.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Key.Back, KeyModifiers.Control)]
    [InlineData(Key.Back, KeyModifiers.Shift)]
    [InlineData(Key.Enter, KeyModifiers.Shift)]
    public async Task InAMarklessSession_AModifiedEnterOrBackspaceStopsTheLineBeingCaptured(
        Key key, KeyModifiers modifiers)
    {
        using var fixture = await Fixture.MarklessAsync();

        fixture.Type("git status");
        fixture.PressKey(key, modifiers);
        fixture.PressEnter();

        Assert.True(await fixture.NothingWasCapturedAsync());
    }

    /// <summary>
    /// A device reply — DA1 here, but a DSR cursor report or an answerback the same way — is text
    /// on the PTY that the keyboard never produced. If it arrives while a line editor is reading,
    /// it lands in the line exactly as a paste would, to the <em>left</em> of everything the user
    /// types afterwards — which the echo gate cannot see, because it compares the accumulated text
    /// against the same number of characters ending at the cursor and the injected bytes sit further
    /// left than that window. So the capture path refuses.
    /// </summary>
    /// <remarks>
    /// This is one half of a deliberate split, and the assertion here is the half that must not move.
    /// The other half - insertion, which asks "is the line empty" rather than "what was typed", and
    /// whose failure mode is a visible editable line rather than a permanent record - keeps working
    /// after a device reply, and is pinned by
    /// <c>PaneAssistInsertionTests.InADegradedSessionAfterTheTerminalAnsweredADeviceQuery_EnterStillSendsTheWholeCommand</c>.
    /// See <c>MarklessSubmissionAccumulator</c>'s <c>_deviceReplyObserved</c>.
    /// </remarks>
    [AvaloniaFact]
    public async Task InAMarklessSession_AParserDeviceReplyStopsTheLineBeingCaptured()
    {
        using var fixture = await Fixture.MarklessAsync();

        fixture.Type("echo hi");
        fixture.Echo("\x1b[c"); // a primary device attributes query; the parser answers it
        fixture.PressEnter();

        Assert.True(await fixture.NothingWasCapturedAsync());
    }

    /// <summary>
    /// The agent host's act surface reaches the pane through
    /// <c>AgentSessionRegistration.InputInjected</c>, which is set in <c>SetupCommon</c>. Driven
    /// through the registry rather than by calling the pane method directly, so the wiring itself
    /// is what is asserted: an agent typing for the user is text the keyboard path never saw.
    /// </summary>
    [AvaloniaFact]
    public async Task InAMarklessSession_AgentInjectedInputStopsTheLineBeingCaptured()
    {
        using var fixture = await Fixture.MarklessAsync();

        Assert.True(AgentSessionRegistry.Instance.TryGet(fixture.Pane.PaneId, out AgentSessionRegistration registration));

        fixture.Type("echo ");
        Assert.NotNull(registration.InputInjected);
        registration.InputInjected!.Invoke();
        fixture.PressEnter();

        Assert.True(await fixture.NothingWasCapturedAsync());
    }

    /// <summary>
    /// Grid truth wins even when it is empty. "The line is empty" and "I cannot read the line" are
    /// different answers, and only the second falls through to the accumulator — otherwise an
    /// instrumented session with a stale accumulator would capture the accumulator's leftovers
    /// every time the user pressed Enter at a bare prompt.
    /// </summary>
    [AvaloniaFact]
    public async Task InAnIntegratedSession_AnEmptyGridBeatsACleanAccumulator()
    {
        // The dangerous shape, built deliberately: the text is painted *before* the B mark, so the
        // grid reads the command line as empty while the echo gate — which only asks whether the
        // accumulated text is on screen at the cursor — would happily wave it through. Only the
        // "grid truth wins, including when it is empty" rule stops this capture.
        using var fixture = await Fixture.IntegratedAsync(string.Empty, prompt: "$ rm -rf /");

        fixture.Type("rm -rf /", echo: false);
        fixture.PressEnter();

        Assert.True(await fixture.NothingWasCapturedAsync());
    }

    /// <summary>
    /// <strong>The owner's "I suspect it also captures my passwords" report, in the shape that is
    /// hardest to see: an <em>instrumented</em> session where the password prompt belongs to a
    /// program the shell is running.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is easy to assume the echo gate is only load-bearing for markless panes, because that is
    /// the session the accumulator was written for. It is not. Between the <c>OSC 133;C</c> that
    /// accepts a line and the next <c>B</c>, an instrumented session has no command line to read
    /// either, so <c>TerminalPane.OnCommandAssistEnterObserved</c> falls back to the accumulator
    /// exactly as <c>cmd.exe</c> does - and the accumulator was reset by the last <c>Enter</c>, so at
    /// the remote <c>password:</c> prompt it is holding a clean, unpoisoned copy of the secret with
    /// nothing else standing in the way. Measured on a live pwsh 7 and Windows PowerShell pane during
    /// the dogfood audit: at the second <c>Enter</c> the accumulator held the marker string and the
    /// echo gate was the only thing that dropped it.
    /// </para>
    /// <para>
    /// The <c>C</c> here deliberately carries no payload. A payload-bearing <c>C</c> would also set
    /// <c>AssistSessionContext.IsStructuredCaptureActive</c> and stand the heuristic path down
    /// entirely, so the test would pass without the gate ever being consulted. This shape - the
    /// payload-less <c>C</c> that iTerm2's and VS Code's snippets emit, and that the remote snippet
    /// degrades to - leaves the heuristic path armed, which is the case worth pinning.
    /// </para>
    /// </remarks>
    [AvaloniaFact]
    public async Task InAnIntegratedSession_APasswordTypedWhileACommandIsRunningIsNotCaptured()
    {
        using var fixture = await Fixture.IntegratedAsync("ssh host");

        fixture.PressEnter();
        CommandHistoryEntry entry = await fixture.WaitForSingleEntryAsync();
        Assert.Equal("ssh host", entry.CommandText);

        // The shell says the line was accepted, which shuts the command-input window: from here the
        // grid offers nothing and the pane is back on the accumulator.
        fixture.Echo("\x1b]133;C\x07");
        await Task.Delay(50);
        Assert.Null(fixture.Pane.TryReadGatedAssistQuerySnapshotForTest());

        fixture.Echo("\r\nhost's password: ");
        fixture.Type("hunter2", echo: false);
        fixture.PressEnter();

        Assert.True(await fixture.OnlyTheFirstEntryWasCapturedAsync("ssh host"));
    }

    /// <summary>
    /// The control for the test above: the same session, the same closed window, a prompt that
    /// <em>does</em> echo. Without this, "nothing was captured" would be satisfied by a fallback path
    /// that had simply stopped working.
    /// </summary>
    [AvaloniaFact]
    public async Task InAnIntegratedSession_AnEchoedLineAfterTheWindowClosedIsStillCaptured()
    {
        using var fixture = await Fixture.IntegratedAsync("cat > notes");

        fixture.PressEnter();
        await fixture.WaitForSingleEntryAsync();

        fixture.Echo("\x1b]133;C\x07");
        await Task.Delay(50);

        fixture.Echo("\r\n");
        fixture.Type("hello there");
        fixture.PressEnter();

        Assert.True(await fixture.WaitForEntryAsync("hello there"));
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

        /// <summary>
        /// An instrumented prompt with <paramref name="commandLine"/> painted at it.
        /// <paramref name="prompt"/> is what is painted <em>before</em> the <c>B</c> mark, so a
        /// caller can place text on the row that the mark deliberately excludes.
        /// </summary>
        public static async Task<Fixture> IntegratedAsync(string commandLine, string prompt = "$ ")
        {
            Fixture fixture = await CreateAsync();
            fixture.Pane.ArmShellIntegrationTracker();
            fixture.Pane.CreateAndWireParser();
            fixture.Pane.Parser!.Process(PromptStart + prompt + PromptEnd + commandLine);

            // The shell-integration dispatcher is serialized and asynchronous; B opens the
            // lifecycle gate on the far side of it.
            await Task.Delay(50);
            return fixture;
        }

        /// <summary>
        /// Types <paramref name="text"/> and, unless <paramref name="echo"/> says otherwise, has
        /// the shell paint it back.
        /// </summary>
        /// <remarks>
        /// The echo is not decoration. Since the echo gate landed, the pane will not use the
        /// accumulator's answer unless that text is on the grid at the cursor — which is what an
        /// ordinary visible prompt produces and what a password prompt does not. Passing
        /// <c>echo: false</c> is how these tests spell "the shell did not show this".
        /// </remarks>
        public void Type(string text, bool echo = true)
        {
            foreach (char c in text)
            {
                // One key press then one text-input event, in the order Avalonia raises them.
                PressKey(KeyForCharacter(c));
                Pane.NotifyTypedTextObserved(c.ToString());
                if (echo)
                {
                    Echo(c.ToString());
                }
            }
        }

        /// <summary>Bytes arriving from the shell, painted into the grid.</summary>
        public void Echo(string text) => Pane.Parser!.Process(text);

        public void PressKey(Key key, KeyModifiers modifiers = KeyModifiers.None) =>
            Pane.TryHandleCommandAssistKey(key, modifiers);

        public void PressBackspace(bool echo = true)
        {
            PressKey(Key.Back);
            Pane.NotifyBackspaceObserved();
            if (echo)
            {
                // What every line editor sends to rub out one character.
                Echo("\b \b");
            }
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

        /// <summary>Whether <paramref name="expected"/> shows up in the store within the timeout.</summary>
        public async Task<bool> WaitForEntryAsync(string expected, int timeoutMs = 2000)
        {
            int elapsed = 0;
            while (true)
            {
                IReadOnlyList<CommandHistoryEntry> entries = await _historyStore.GetRecentAsync(10);
                if (entries.Any(entry => entry.CommandText == expected))
                {
                    return true;
                }

                if (elapsed >= timeoutMs)
                {
                    return false;
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

        /// <summary>
        /// The store holds exactly one entry and it is <paramref name="expected"/> — for the tests
        /// where an earlier command legitimately was captured and the claim is that a later one
        /// was not.
        /// </summary>
        public async Task<bool> OnlyTheFirstEntryWasCapturedAsync(string expected)
        {
            await Task.Delay(150);
            IReadOnlyList<CommandHistoryEntry> entries = await _historyStore.GetRecentAsync(10);
            return entries.Count == 1 && entries[0].CommandText == expected;
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

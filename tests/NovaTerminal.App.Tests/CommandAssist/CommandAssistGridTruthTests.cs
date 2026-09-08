using NovaTerminal.CommandAssist.Application;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;

namespace NovaTerminal.Tests.CommandAssist;

/// <summary>
/// Phase 1c: the query is read out of the terminal grid, and the shadow keystroke buffer is gone.
/// </summary>
/// <remarks>
/// <para>
/// Three things are pinned here, and they are the three the design rests on.
/// </para>
/// <para>
/// <strong>The lifecycle gate.</strong> The grid reader cannot tell "the user is typing" from
/// "this is the command's output"; only the OSC 133 stream can. So consumption is gated on the
/// window between <c>B</c> and <c>C</c>, and a grid that is perfectly readable outside that window
/// is still not a query.
/// </para>
/// <para>
/// <strong>Desync immunity.</strong> The V1 shadow buffer mirrored TextInput, Backspace, Enter and
/// Paste. Arrow keys, <c>Ctrl+U</c>, <c>Ctrl+W</c>, shell history recall and shell-side Tab
/// completion all edit the line without producing any of those four, so V1's query drifted from
/// reality and never came back. Here the shell rewrites the line behind the controller's back and
/// the next refresh simply agrees with it.
/// </para>
/// <para>
/// <strong>Degraded mode.</strong> No marks means no query - not an empty query it might act on,
/// and not a keystroke mirror kept as a fallback. Prefix-dependent features go quiet; explicit
/// history search still opens and shows recency.
/// </para>
/// </remarks>
public sealed class CommandAssistGridTruthTests
{
    // ---- lifecycle gate ------------------------------------------------------------------

    [Fact]
    public async Task BeforeAnyPromptMark_TheGridIsNotConsulted()
    {
        var harness = Harness.Create();
        harness.Grid.SetLine("git status");

        harness.Controller.NotifyInputActivity();
        await harness.SettleAsync();

        Assert.Null(harness.Controller.TryReadQuerySnapshot());
        Assert.Equal(string.Empty, harness.Controller.ViewModel.QueryText);
    }

    [Fact]
    public async Task AfterThePromptMark_TheGridIsTheQuery()
    {
        var harness = Harness.Create();
        await harness.PromptReadyAsync();
        harness.Grid.SetLine("git status");

        harness.Controller.NotifyInputActivity();
        await harness.SettleAsync();

        Assert.Equal("git status", harness.Controller.TryReadQuerySnapshot()?.Text);
        Assert.Equal("git status", harness.Controller.ViewModel.QueryText);
    }

    /// <summary>
    /// The gate's whole reason for existing. After <c>OSC 133;C</c> the command is running and the
    /// cells below the mark are its output - but the mark is deliberately kept across C (the input
    /// line is still on screen at that instant), so the reader would happily return something. Only
    /// the lifecycle gate stops it.
    /// </summary>
    [Fact]
    public async Task BetweenCommands_AReadableGridIsStillNotAQuery()
    {
        var harness = Harness.Create();
        await harness.PromptReadyAsync();
        harness.Grid.SetLine("git status");

        await harness.CommandAcceptedAsync("git status");

        // The provider is still perfectly happy to answer - nothing about the buffer changed.
        Assert.Equal("git status", harness.Grid.Read()?.Text);

        harness.Controller.NotifyInputActivity();
        await harness.SettleAsync();

        Assert.Null(harness.Controller.TryReadQuerySnapshot());
        Assert.Equal(string.Empty, harness.Controller.ViewModel.QueryText);
    }

    [Fact]
    public async Task AfterCommandFinished_TheGateIsClosed()
    {
        var harness = Harness.Create();
        await harness.PromptReadyAsync();
        harness.Grid.SetLine("on branch main");

        await harness.CommandFinishedAsync(exitCode: 0);

        Assert.Null(harness.Controller.TryReadQuerySnapshot());
    }

    /// <summary>
    /// The gate opens on the <c>B</c> itself, with none of the event's asynchronous half having run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what a lost history entry on CI came down to. The gate is one half of a pair - the
    /// other is the <c>OSC 133;B</c> mark the parser writes into the buffer synchronously - and
    /// <c>TerminalPane.OnCommandAssistEnterObserved</c> reads both at once on the UI thread. When
    /// they disagree it refuses grid truth, falls through to the markless accumulator (empty,
    /// because the shell echoed the line rather than the user typing it), and persists nothing. The
    /// command is dropped silently and permanently.
    /// </para>
    /// <para>
    /// Both halves now live on <c>TerminalBuffer</c> and are written by the parser on the thread the
    /// bytes arrive on, so no amount of dispatcher backlog can put them out of step. Here that is
    /// the terminal opening the window with no controller event emitted at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheGateOpensWithoutAnyOfTheAsyncEventPathHavingRun()
    {
        var harness = Harness.Create();

        harness.Grid.OpenCommandInputWindow();
        harness.Grid.SetLine("hostname");

        Assert.Equal("hostname", harness.Controller.TryReadQuerySnapshot()?.Text);
    }

    /// <summary>
    /// A shell-integration event arriving late cannot move the gate, in either direction.
    /// </summary>
    /// <remarks>
    /// The whole point of moving the gate onto the buffer. While the controller owned it, an event
    /// replayed off the pane's serialized dispatcher could contradict the terminal's own newer
    /// answer - reopening a window onto a running command's output, which is the one thing the gate
    /// exists to prevent. The controller is no longer a writer, so a backlogged event is now inert
    /// with respect to the gate and only the terminal's account of the byte stream counts.
    /// </remarks>
    [Fact]
    public async Task ALateShellIntegrationEvent_CannotMoveTheGate()
    {
        var harness = Harness.Create();

        harness.Grid.OpenCommandInputWindow();
        harness.Grid.CloseCommandInputWindow();
        harness.Grid.SetLine("on branch main");

        // The dispatcher finally gets to the B the terminal has long since superseded.
        await harness.Controller.HandleShellIntegrationEventAsync(
            Mark(ShellIntegrationEventType.CommandStarted));

        Assert.Null(harness.Controller.TryReadQuerySnapshot());
    }

    private static ShellIntegrationEvent Mark(ShellIntegrationEventType type) => new(
        Type: type,
        Timestamp: DateTimeOffset.UtcNow,
        CommandText: null,
        WorkingDirectory: null,
        ExitCode: null,
        Duration: null);

    /// <summary>
    /// A prompt repaint re-emits <c>B</c>, so the window reopens on evidence. This is the path back
    /// from every closer: next command, alt screen teardown, resize.
    /// </summary>
    [Fact]
    public async Task TheNextPromptReopensTheGate()
    {
        var harness = Harness.Create();
        await harness.PromptReadyAsync();
        await harness.CommandAcceptedAsync("git status");
        await harness.CommandFinishedAsync(exitCode: 0);

        await harness.PromptReadyAsync();
        harness.Grid.SetLine("ls");

        Assert.Equal("ls", harness.Controller.TryReadQuerySnapshot()?.Text);
    }

    [Fact]
    public async Task WhileTheAltScreenIsUp_TheGridIsNotConsulted()
    {
        var harness = Harness.Create();
        await harness.PromptReadyAsync();
        harness.Grid.SetLine("vim notes.txt");

        harness.Controller.HandleAltScreenChanged(true);

        Assert.Null(harness.Controller.TryReadQuerySnapshot());
    }

    /// <summary>
    /// Both halves are necessary. The gate can be open while the mark has aged out of scrollback or
    /// its coordinate generation has been reset, and then there is no truth to read either.
    /// </summary>
    [Fact]
    public async Task WhenTheMarkGoesDarkWithTheGateOpen_ThereIsNoQuery()
    {
        var harness = Harness.Create();
        await harness.PromptReadyAsync();
        harness.Grid.SetLine("git status");
        Assert.NotNull(harness.Controller.TryReadQuerySnapshot());

        harness.Grid.GoDark();

        Assert.Null(harness.Controller.TryReadQuerySnapshot());
    }

    /// <summary>
    /// <c>B</c> rides inside the prompt string, so a prompt framework that repaints mid-edit
    /// re-emits it with the window already open. That has to be a no-op: treating a second
    /// <c>B</c> as a toggle, or as evidence that a new command line started, would blank the query
    /// under the user's hands. The newest mark wins and nothing else changes.
    /// </summary>
    [Fact]
    public async Task ASecondPromptMarkInsideTheWindow_ChangesNothing()
    {
        var harness = Harness.Create();
        await harness.PromptReadyAsync();
        harness.Grid.SetLine("git st");
        Assert.Equal("git st", harness.Controller.TryReadQuerySnapshot()?.Text);

        await harness.PromptReadyAsync();

        Assert.Equal("git st", harness.Controller.TryReadQuerySnapshot()?.Text);
    }

    /// <summary>
    /// <c>Ctrl+C</c> at a prompt. Nothing ran, so the shell emits no <c>C</c> and no <c>D</c> - it
    /// prints <c>^C</c> and a fresh prompt, which re-emits <c>B</c>. The gate legitimately stays
    /// open across the whole thing; the only state that moves is which mark is newest. This is the
    /// one common interrupt path that reaches neither closer, so it is pinned rather than inferred.
    /// </summary>
    [Fact]
    public async Task CtrlCAtAPrompt_LeavesTheGateOpenAndTheNextMarkTakesOver()
    {
        var harness = Harness.Create();
        await harness.PromptReadyAsync();
        harness.Grid.SetLine("git stat");

        // The interrupt, then the shell's new prompt. No submission event of any kind.
        await harness.PromptReadyAsync();
        harness.Grid.SetLine(string.Empty);

        AssistQuerySnapshot? snapshot = harness.Controller.TryReadQuerySnapshot();
        Assert.True(snapshot.HasValue);
        Assert.Equal(string.Empty, snapshot!.Value.Text);
    }

    /// <summary>
    /// A full-screen TUI may emit its own <c>OSC 133;B</c> - a program that draws a prompt is
    /// entitled to. The gate must refuse to open, so that "never open while the alt screen is up"
    /// is a property of the flag itself and not just of every consumer remembering to check both.
    /// </summary>
    [Fact]
    public async Task WhileTheAltScreenIsUp_APromptMarkDoesNotOpenTheGate()
    {
        var harness = Harness.Create();
        harness.SetAltScreen(true);

        await harness.PromptReadyAsync();
        harness.Grid.SetLine("git status");

        Assert.Null(harness.Controller.TryReadQuerySnapshot());

        // Leaving the alt screen does not reopen it either: the shell's own prompt repaint does.
        harness.SetAltScreen(false);

        Assert.Null(harness.Controller.TryReadQuerySnapshot());
    }

    // ---- desync immunity (the V1 failure matrix) -----------------------------------------

    /// <summary>
    /// <c>Ctrl+U</c> clears the line. V1's mirror never saw it, so its query stayed at whatever had
    /// been typed and every subsequent ranking and insertion was computed against text that was no
    /// longer on screen.
    /// </summary>
    [Fact]
    public async Task AfterCtrlU_TheQueryFollowsTheEmptiedLine()
    {
        var harness = Harness.Create();
        await harness.PromptReadyAsync();

        harness.Grid.SetLine("git st");
        harness.Controller.NotifyInputActivity();
        await harness.WaitForQueryAsync("git st");

        // Ctrl+U reaches the shell, the shell erases the line, and nothing tells Command Assist.
        // The next trigger - any trigger - reads the truth.
        harness.Grid.SetLine(string.Empty);
        harness.Controller.NotifyInputActivity();
        await harness.WaitForQueryAsync(string.Empty);

        Assert.Equal(string.Empty, harness.Controller.ViewModel.QueryText);
    }

    /// <summary>
    /// Up-arrow history recall replaces the line with a command the user typed no character of.
    /// V1 ranked against the empty mirror and offered nothing; the grid ranks against the recalled
    /// command.
    /// </summary>
    [Fact]
    public async Task AfterHistoryRecall_TheQueryIsTheRecalledCommand()
    {
        var harness = Harness.Create(seed: new[] { "git status --short", "dotnet test" });
        await harness.PromptReadyAsync();
        harness.Controller.ToggleAssist();

        harness.Grid.SetLine("git status");
        harness.Controller.NotifyInputActivity();

        await harness.WaitForQueryAsync("git status");
        await harness.WaitForConditionAsync(() => harness.Controller.ViewModel.TopSuggestionText == "git status --short");

        Assert.Equal("git status --short", harness.Controller.ViewModel.TopSuggestionText);
    }

    /// <summary>
    /// Shell-side Tab completion. Command Assist deliberately does not own Tab (the shell does), so
    /// the completion arrives as a line rewrite with no key event attached - the purest form of the
    /// V1 desync.
    /// </summary>
    [Fact]
    public async Task AfterShellTabCompletion_TheQueryIsTheCompletedWord()
    {
        var harness = Harness.Create();
        await harness.PromptReadyAsync();

        harness.Grid.SetLine("cd src/NovaTerm");
        harness.Controller.NotifyInputActivity();
        await harness.WaitForQueryAsync("cd src/NovaTerm");

        harness.Grid.SetLine("cd src/NovaTerminal.CommandAssist/");
        harness.Controller.NotifyInputActivity();
        await harness.WaitForQueryAsync("cd src/NovaTerminal.CommandAssist/");
    }

    /// <summary>
    /// The whole point, in one assertion: a trigger with no text argument still moves the query,
    /// because the text was never the trigger's to carry.
    /// </summary>
    [Fact]
    public async Task ATriggerCarriesNoText_AndTheQueryStillFollowsTheGrid()
    {
        var harness = Harness.Create();
        await harness.PromptReadyAsync();

        harness.Grid.SetLine("kubectl get pods");
        harness.Controller.NotifyInputActivity();
        await harness.WaitForQueryAsync("kubectl get pods");

        // A backspace, as far as Command Assist is concerned, is the same event as a keypress.
        harness.Grid.SetLine("kubectl get pod");
        harness.Controller.NotifyInputActivity();
        await harness.WaitForQueryAsync("kubectl get pod");
    }

    // ---- degraded mode -------------------------------------------------------------------

    /// <summary>
    /// No marks at all: a non-integrated local shell, or an un-instrumented SSH session. Typing
    /// produces no query, so passive suggestions have nothing to rank.
    /// </summary>
    [Fact]
    public async Task Degraded_TypingProducesNoQueryAndNoPassiveSuggestions()
    {
        var harness = Harness.CreateDegraded(seed: new[] { "git status" });

        harness.Controller.NotifyInputActivity();
        await harness.SettleAsync();

        Assert.Equal(string.Empty, harness.Controller.ViewModel.QueryText);
        Assert.Empty(harness.Controller.Suggestions);
        Assert.False(harness.Controller.ViewModel.IsVisible);
    }

    /// <summary>
    /// The degraded-mode Search decision: <c>Ctrl+R</c> still opens, and with no query to filter on
    /// it lists what was run most recently. History is per user, not per session, so a degraded
    /// session can still browse commands captured in instrumented ones.
    /// </summary>
    [Fact]
    public async Task Degraded_ExplicitHistorySearchShowsTheRecencyList()
    {
        var harness = Harness.CreateDegraded(seed: new[] { "dotnet build", "git status" });

        bool opened = harness.Controller.OpenHistorySearch();
        await harness.WaitForConditionAsync(() => harness.Controller.Suggestions.Count > 0);

        Assert.True(opened);

        // The label is the honest one: there is no query, there will not be one, and typing will
        // not narrow these rows. "History" on its own presents a filter box that cannot filter.
        Assert.Equal("History - recent", harness.Controller.ViewModel.ModeLabel);
        Assert.Equal(string.Empty, harness.Controller.ViewModel.QueryText);
        Assert.Contains(harness.Controller.Suggestions, s => s.InsertText == "git status");
        Assert.Contains(harness.Controller.Suggestions, s => s.InsertText == "dotnet build");
    }

    /// <summary>The other side of the label decision: a readable command line does filter.</summary>
    [Fact]
    public async Task Integrated_ExplicitHistorySearchIsLabelledAsAFilter()
    {
        var harness = Harness.Create(seed: new[] { "dotnet build", "git status" });
        await harness.PromptReadyAsync();
        harness.Grid.SetLine("git");

        harness.Controller.OpenHistorySearch();
        await harness.WaitForConditionAsync(() => harness.Controller.Suggestions.Count > 0);

        Assert.Equal("History", harness.Controller.ViewModel.ModeLabel);
        Assert.Equal("git", harness.Controller.ViewModel.QueryText);
    }

    /// <summary>
    /// <strong>The scope of replace-on-accept, as the controller reports it.</strong> Only an explicit
    /// history search means "the typed characters are a filter, so take the row whole"; every Suggest
    /// surface goes on meaning "extend what I have typed".
    /// </summary>
    /// <remarks>
    /// The state-space half of this - that <see cref="AssistSessionState.HistorySearch"/> is the only
    /// state mapping to <c>Search</c>, for all nine states - is pinned by
    /// <c>AssistSessionStateMachineTests.AcceptReplacesTypedQuery_IsTrueOnlyInHistorySearch</c>. This is
    /// the wiring: that the controller's public answer follows the real transitions the App drives.
    /// </remarks>
    [Fact]
    public async Task AcceptReplacesTypedQuery_IsTrueOnlyForExplicitHistorySearch()
    {
        var harness = Harness.Create(seed: new[] { "git status" });
        await harness.PromptReadyAsync();
        harness.Grid.SetLine("git");

        Assert.False(harness.Controller.AcceptReplacesTypedQuery);

        harness.Controller.ToggleAssist();
        await harness.WaitForConditionAsync(() => harness.Controller.Suggestions.Count > 0);
        Assert.False(harness.Controller.AcceptReplacesTypedQuery);

        harness.Controller.OpenHistorySearch();
        await harness.WaitForConditionAsync(() => harness.Controller.Suggestions.Count > 0);
        Assert.True(harness.Controller.AcceptReplacesTypedQuery);

        // An accept closes the session, and a closed session is a Suggest one again - so a stale
        // "replace" cannot outlive the surface that justified it.
        Assert.True(harness.Controller.TryAcceptSelection(out _));
        Assert.False(harness.Controller.AcceptReplacesTypedQuery);
    }

    /// <summary>
    /// And it survives typing, which is the whole point of PR #304: a filter keystroke narrows the list
    /// without leaving <see cref="AssistSessionState.HistorySearch"/>, so the accept that follows still
    /// takes the row whole rather than reverting to an append halfway through a search.
    /// </summary>
    [Fact]
    public async Task AcceptReplacesTypedQuery_SurvivesTypingInsideHistorySearch()
    {
        var harness = Harness.Create(seed: new[] { "echo git-alpha" });
        await harness.PromptReadyAsync();

        harness.Controller.OpenHistorySearch();
        await harness.WaitForConditionAsync(() => harness.Controller.Suggestions.Count > 0);
        Assert.True(harness.Controller.AcceptReplacesTypedQuery);

        harness.Grid.SetLine("git");
        harness.Controller.NotifyInputActivity();
        await harness.WaitForQueryAsync("git");

        Assert.Equal(AssistSessionState.HistorySearch, harness.Controller.SessionState);
        Assert.True(harness.Controller.AcceptReplacesTypedQuery);
    }

    /// <summary>
    /// <strong>The hint strip stops advertising insert on a row the planner is going to refuse.</strong>
    /// A row carrying a line break cannot be sent - the newline would submit rather than insert - so
    /// the bubble drops the clause instead of promising a chord that consumes the key and does nothing.
    /// </summary>
    /// <remarks>
    /// The row itself stays in the list and stays browsable; only the promise goes. See
    /// <c>CommandAssistController.SelectedRowCanBeInserted</c> for why that is the trade, and why the
    /// clause survives on the armed (<c>Enter</c>) path regardless.
    /// </remarks>
    [Fact]
    public async Task AMultiLineRowIsNotAdvertisedAsInsertable()
    {
        var harness = Harness.Create(seed: new[] { "echo one\necho two" });
        await harness.PromptReadyAsync();
        harness.Grid.SetLine("echo");

        harness.Controller.ToggleAssist();
        await harness.WaitForConditionAsync(() => harness.Controller.Suggestions.Count > 0);

        // The bubble, not the popup: the popup's strip is the armed path, where the clause is
        // unconditional by an older decision.
        Assert.False(harness.Controller.ViewModel.IsPopupOpen);
        Assert.Contains('\n', harness.Controller.Suggestions[0].InsertText);
        Assert.DoesNotContain(" insert", harness.Controller.ViewModel.Bubble.ShortcutHintText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control for the test above: the same surface with an ordinary single-line row still
    /// advertises the chord. Without this, "no insert clause" would be satisfied by a strip that never
    /// shows one.
    /// </summary>
    [Fact]
    public async Task ASingleLineRowIsStillAdvertisedAsInsertable()
    {
        var harness = Harness.Create(seed: new[] { "echo one" });
        await harness.PromptReadyAsync();
        harness.Grid.SetLine("echo");

        harness.Controller.ToggleAssist();
        await harness.WaitForConditionAsync(() => harness.Controller.Suggestions.Count > 0);

        Assert.False(harness.Controller.ViewModel.IsPopupOpen);
        Assert.Contains(" insert", harness.Controller.ViewModel.Bubble.ShortcutHintText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the degraded-Search decision: you can browse the rows, but nothing may be
    /// spliced into a command line nobody can see. The planner is what refuses; this pins that the
    /// controller hands it a null snapshot rather than an empty-string stand-in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Both styles, and the replace half is the load-bearing one.</strong> This is a
    /// <c>Ctrl+R</c> session, so <see cref="CommandAssistController.AcceptReplacesTypedQuery"/> is true
    /// here and an accept that got through would erase characters rather than only add them. Replace
    /// also needs strictly more than append did - a <em>count</em> - and there is nothing to count from
    /// a snapshot that does not exist. This test is the whole argument for why no new degraded-mode gate
    /// was added anywhere for the replace style: the refusal it already had covers it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Degraded_SelectingAHistoryRowYieldsNoInsertionPlan()
    {
        var harness = Harness.CreateDegraded(seed: new[] { "git status" });

        harness.Controller.OpenHistorySearch();
        await harness.WaitForConditionAsync(() => harness.Controller.Suggestions.Count > 0);

        Assert.True(harness.Controller.TryGetInsertionText(out string? selected));
        Assert.Equal("git status", selected);
        Assert.True(harness.Controller.AcceptReplacesTypedQuery);

        Assert.Null(harness.Controller.TryReadQuerySnapshot());
        Assert.False(CommandAssistInsertionPlanner.TryCreateInsertion(
            harness.Controller.TryReadQuerySnapshot(),
            selected,
            out string? textToSend));
        Assert.Null(textToSend);

        Assert.False(CommandAssistInsertionPlanner.TryCreatePlan(
            harness.Controller.TryReadQuerySnapshot(),
            selected,
            CommandAssistInsertionStyle.ReplaceTypedPrefix,
            out CommandAssistInsertionPlan plan));
        Assert.Equal(default, plan);
    }

    /// <summary>
    /// Help in a degraded session gets no command token from a query it does not have - but an
    /// explicit selection still works, which is the escape hatch that keeps Explain useful there.
    /// </summary>
    [Fact]
    public async Task Degraded_HelpTakesNoTokenFromTheQueryButStillHonoursASelection()
    {
        var docsProvider = new RecordingDocsProvider();
        var harness = Harness.CreateDegraded(docsProvider: docsProvider);

        await harness.Controller.OpenHelpAsync();
        Assert.Equal(string.Empty, docsProvider.LastQuery?.RawInput);
        Assert.Null(docsProvider.LastQuery?.CommandToken);

        await harness.Controller.ExplainSelectionAsync("fatal: not a git repository");
        Assert.Equal("fatal: not a git repository", docsProvider.LastQuery?.SelectedText);
    }

    /// <summary>
    /// An instrumented session gets its help token from the grid, not from a view-model field that
    /// the last ranking pass happened to write.
    /// </summary>
    [Fact]
    public async Task Integrated_HelpTakesItsTokenFromTheGrid()
    {
        var docsProvider = new RecordingDocsProvider();
        var harness = Harness.Create(docsProvider: docsProvider);
        await harness.PromptReadyAsync();
        harness.Grid.SetLine("docker compose up");

        await harness.Controller.OpenHelpAsync();

        Assert.Equal("docker compose up", docsProvider.LastQuery?.RawInput);
        Assert.Equal("docker", docsProvider.LastQuery?.CommandToken);
    }

    // ---- harness -------------------------------------------------------------------------

    private sealed class Harness
    {
        private readonly TestDispatcher _dispatcher;

        private Harness(
            CommandAssistController controller,
            FakeGrid grid,
            InMemoryHistoryStore history,
            TestDispatcher dispatcher)
        {
            Controller = controller;
            Grid = grid;
            History = history;
            _dispatcher = dispatcher;
        }

        public CommandAssistController Controller { get; }

        public FakeGrid Grid { get; }

        public InMemoryHistoryStore History { get; }

        public static Harness Create(string[]? seed = null, ICommandDocsProvider? docsProvider = null)
            => Build(new FakeGrid(), seed, docsProvider);

        /// <summary>A session with no OSC 133 marks: the provider never returns anything.</summary>
        public static Harness CreateDegraded(string[]? seed = null, ICommandDocsProvider? docsProvider = null)
            => Build(grid: null, seed, docsProvider);

        private static Harness Build(FakeGrid? grid, string[]? seed, ICommandDocsProvider? docsProvider)
        {
            var history = new InMemoryHistoryStore();
            if (seed != null)
            {
                history.Seed(seed);
            }

            var dispatcher = new TestDispatcher();

            var controller = new CommandAssistController(
                history,
                new SecretsFilter(),
                new CommandAssistSuggestionEngine(new NoPathSuggestionProvider()),
                snippetStore: null,
                commandDocsProvider: docsProvider,
                recipeProvider: null,
                errorInsightService: null,
                modeRouter: null,
                resultBuilder: null,
                queryProvider: grid == null ? null : grid.Read,
                commandInputGateProbe: grid == null ? null : () => grid.IsAcceptingCommandInput,

                // The pane's dispatcher, modelled rather than omitted - and that is what makes the
                // reads below safe. See TestDispatcher.
                dispatch: dispatcher.Post);

            return new Harness(controller, grid ?? new FakeGrid(), history, dispatcher);
        }

        /// <remarks>
        /// The gate moves first and synchronously, then the event goes to the controller - the exact
        /// order the production path has since #448: TerminalPane's parser callback opens the window
        /// on the buffer beside the mark write, and only then enqueues the event onto the pane's
        /// serialized dispatcher. A harness that moved the gate via the controller would be testing
        /// a seam that no longer exists.
        /// </remarks>
        public Task PromptReadyAsync()
        {
            Grid.OpenCommandInputWindow();
            return EmitAsync(ShellIntegrationEventType.CommandStarted, null, null);
        }

        public Task CommandAcceptedAsync(string commandText)
        {
            Grid.CloseCommandInputWindow();
            return EmitAsync(ShellIntegrationEventType.CommandAccepted, commandText, null);
        }

        public Task CommandFinishedAsync(int exitCode)
        {
            Grid.CloseCommandInputWindow();
            return EmitAsync(ShellIntegrationEventType.CommandFinished, null, exitCode);
        }

        /// <summary>An alt-screen switch, moved on the terminal and reported to the controller.</summary>
        /// <remarks>
        /// Both, because they are two different facts now: the buffer owns the gate, while the
        /// context keeps <c>IsAltScreenActive</c> for the consumers that check it separately.
        /// </remarks>
        public void SetAltScreen(bool isActive)
        {
            Grid.SetAltScreenActive(isActive);
            Controller.HandleAltScreenChanged(isActive);
        }

        private async Task EmitAsync(ShellIntegrationEventType type, string? commandText, int? exitCode)
        {
            await Controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
                Type: type,
                Timestamp: DateTimeOffset.UtcNow,
                CommandText: commandText,
                WorkingDirectory: null,
                ExitCode: exitCode,
                Duration: null));
            _dispatcher.Drain();
        }

        /// <summary>
        /// Waits long enough for a typing-triggered pass to have run and published.
        /// </summary>
        /// <remarks>
        /// Comfortably past <c>SuggestionOrchestrator.DefaultPassiveRefreshDebounce</c> (75 ms as of V2
        /// Phase 3b), which is what this harness gets: it builds a production controller rather than
        /// injecting a delay. It was 60 ms, which silently became "before the pass starts" the moment
        /// the debounce landed. Several tests here assert that nothing appeared, and those are the ones
        /// a too-short settle would pass for the wrong reason.
        /// </remarks>
        public async Task SettleAsync()
        {
            await Task.Delay(250);
            _dispatcher.Drain();
        }

        public Task WaitForQueryAsync(string expected)
            => WaitForConditionAsync(() => Controller.ViewModel.QueryText == expected);

        public async Task WaitForConditionAsync(Func<bool> predicate, int timeoutMs = 2000)
        {
            int elapsed = 0;
            _dispatcher.Drain();
            while (!predicate())
            {
                if (elapsed >= timeoutMs)
                {
                    throw new TimeoutException(
                        $"Timed out waiting for test condition. QueryText='{Controller.ViewModel.QueryText}', " +
                        $"rows={Controller.Suggestions.Count}, top='{Controller.ViewModel.TopSuggestionText}'.");
                }

                await Task.Delay(10);
                elapsed += 10;
                _dispatcher.Drain();
            }
        }

        /// <summary>
        /// The pane dispatcher's stand-in: queues the ranking pass's publish instead of running it
        /// on whatever thread finished the pass, and hands it to the test thread at the next
        /// <see cref="WaitForConditionAsync"/>, <see cref="SettleAsync"/> or event emit.
        /// </summary>
        /// <remarks>
        /// Omitting <c>dispatch</c> left the controller on its <c>action => action()</c> default,
        /// which is not what the App does: <c>TerminalPane</c> passes a dispatch that posts to the
        /// pane thread, so in production <c>ApplyRefreshOutcome</c> and every selection read are the
        /// same thread and cannot interleave. Inline meant the publish ran on the pass's threadpool
        /// thread while the test thread read the results - a race the harness invented rather than
        /// modelled.
        ///
        /// It bit as a rare CI failure in <c>AcceptReplacesTypedQuery_IsTrueOnlyForExplicitHistorySearch</c>,
        /// whose <c>Suggestions.Count > 0</c> wait is satisfied by the rows already on screen from
        /// the preceding <c>ToggleAssist</c>: the test walked on into the accept while a Search pass
        /// was mid-publish, and <c>ApplyRefreshOutcome</c> clears the list before it refills it, so
        /// an accept landing between the two saw no rows and refused. Under stress the same window
        /// throws outright, from the check-then-index in <c>GetSelectedSuggestion</c>.
        ///
        /// Fixed here rather than by lengthening that one wait, because the race was available to
        /// every test in this file and a sleep only makes it rarer. Keep the reads on the test
        /// thread: anything that adds a new await point should drain here too.
        /// </remarks>
        private sealed class TestDispatcher
        {
            private readonly object _gate = new();
            private readonly Queue<Action> _pending = new();

            public void Post(Action action)
            {
                lock (_gate)
                {
                    _pending.Enqueue(action);
                }
            }

            /// <summary>Runs everything queued so far on the calling thread.</summary>
            public void Drain()
            {
                while (true)
                {
                    Action next;
                    lock (_gate)
                    {
                        if (_pending.Count == 0)
                        {
                            return;
                        }

                        next = _pending.Dequeue();
                    }

                    next();
                }
            }
        }
    }

    /// <summary>
    /// The provider seam's stand-in. Locked rather than volatile because the real provider is read
    /// from the refresh pass's worker thread, not from the thread that set the line.
    /// </summary>
    /// <summary>
    /// Stands in for the terminal: both halves of the OSC 133 pair, because that is where they live
    /// now. <c>TerminalBuffer</c> owns the <c>133;B</c> mark the line is read from <em>and</em> the
    /// command-input gate that says the read is legal (#448); a harness that owned one and let the
    /// controller own the other would be modelling a split that no longer exists, and would keep
    /// passing if the two came apart again.
    /// </summary>
    private sealed class FakeGrid
    {
        private readonly object _gate = new();
        private AssistQuerySnapshot? _snapshot;
        private bool _acceptingCommandInput;
        private bool _altScreenActive;

        public AssistQuerySnapshot? Read()
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }

        /// <summary>The probe the controller reads, as <c>TerminalBuffer.IsAcceptingCommandInput</c>.</summary>
        public bool IsAcceptingCommandInput
        {
            get { lock (_gate) { return _acceptingCommandInput; } }
        }

        /// <summary><c>OSC 133;B</c>, as the parser applies it to the buffer.</summary>
        /// <remarks>
        /// Refused on the alt screen, mirroring <c>TerminalBuffer.OpenCommandInputWindow</c>: a
        /// full-screen TUI may legally draw its own prompt and emit <c>133;B</c>, and that must not
        /// open a window onto the TUI's grid.
        /// </remarks>
        public void OpenCommandInputWindow()
        {
            lock (_gate)
            {
                if (_altScreenActive)
                {
                    return;
                }

                _acceptingCommandInput = true;
            }
        }

        /// <summary>Entering the alt screen closes the window; leaving does not reopen it.</summary>
        public void SetAltScreenActive(bool isActive)
        {
            lock (_gate)
            {
                _altScreenActive = isActive;
                if (isActive)
                {
                    _acceptingCommandInput = false;
                }
            }
        }

        /// <summary><c>OSC 133;C</c> / <c>D</c>, likewise.</summary>
        public void CloseCommandInputWindow()
        {
            lock (_gate) { _acceptingCommandInput = false; }
        }

        public void SetLine(string text, int? cursorOffset = null)
        {
            lock (_gate)
            {
                _snapshot = new AssistQuerySnapshot(text, cursorOffset ?? text.Length, false, false);
            }
        }

        public void GoDark()
        {
            lock (_gate)
            {
                _snapshot = null;
            }
        }
    }

    private sealed class NoPathSuggestionProvider : IPathSuggestionProvider
    {
        public IReadOnlyList<AssistSuggestion> GetSuggestions(CommandAssistQueryContext context, int maxResults)
            => Array.Empty<AssistSuggestion>();
    }

    private sealed class RecordingDocsProvider : ICommandDocsProvider
    {
        public CommandHelpQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<CommandHelpItem>> GetHelpAsync(
            CommandHelpQuery query,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult<IReadOnlyList<CommandHelpItem>>(Array.Empty<CommandHelpItem>());
        }
    }

    private sealed class InMemoryHistoryStore : IHistoryStore
    {
        private readonly List<CommandHistoryEntry> _entries = new();

        public Task AppendAsync(CommandHistoryEntry entry, CancellationToken cancellationToken = default)
        {
            _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            _entries.Clear();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CommandHistoryEntry>> GetRecentAsync(int maxResults, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<CommandHistoryEntry> results = _entries
                .OrderByDescending(x => x.ExecutedAt)
                .Take(maxResults)
                .ToList();
            return Task.FromResult(results);
        }

        public Task<IReadOnlyList<CommandHistoryEntry>> SearchAsync(string query, int maxCandidates, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<CommandHistoryEntry> results = _entries
                .Where(x => x.CommandText.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.ExecutedAt)
                .Take(Math.Max(0, maxCandidates))
                .ToList();
            return Task.FromResult(results);
        }

        public Task<bool> TryMarkInvalidCommandAsync(string entryId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<int> TryMarkInvalidCommandsByFirstTokenAsync(string firstToken, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<bool> TryUpdateExecutionResultAsync(string entryId, int? exitCode, long? durationMs, CancellationToken cancellationToken = default, bool isInvalidCommand = false)
            => Task.FromResult(false);

        public void Seed(params string[] commandTexts)
        {
            DateTimeOffset at = DateTimeOffset.Parse("2026-03-01T10:00:00+00:00");
            foreach (string commandText in commandTexts)
            {
                _entries.Add(new CommandHistoryEntry(
                    Id: Guid.NewGuid().ToString("N"),
                    CommandText: commandText,
                    ExecutedAt: at,
                    ShellKind: "pwsh",
                    WorkingDirectory: @"C:\repo",
                    ProfileId: "profile-1",
                    SessionId: "session-1",
                    HostId: null,
                    ExitCode: 0,
                    IsRemote: false,
                    IsRedacted: false,
                    Source: CommandCaptureSource.Heuristic,
                    DurationMs: null));
                at = at.AddSeconds(-1);
            }
        }
    }
}

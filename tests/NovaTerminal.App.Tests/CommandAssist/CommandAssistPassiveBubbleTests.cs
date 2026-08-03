using NovaTerminal.CommandAssist.Application;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;

namespace NovaTerminal.Tests.CommandAssist;

/// <summary>
/// The V2 Phase 3b passive bubble: what appears while the user types, when, and what takes it away.
/// </summary>
/// <remarks>
/// <para>
/// These pin a deliberate reversal of M4.3's quiet-by-default policy (design doc Pillar 4). Before
/// this phase a passive Suggest pass was scoped to path suggestions only, so a user typing a command
/// saw nothing until they knew a shortcut; now the top row of the merged history/path ranking shows
/// in the bubble after two characters. <c>CommandAssistPassiveBubbleEnabled</c> restores the old
/// behavior and is tested here too, because a kill switch nobody tests is a kill switch that does
/// not work.
/// </para>
/// <para>
/// The debounce is tested through an injected delay rather than by sleeping: the orchestrator's
/// coalescing is a cancellation property, not a timing one, and a wall-clock test of it would be
/// exactly the kind of flake this suite cannot afford.
/// </para>
/// </remarks>
public sealed class CommandAssistPassiveBubbleTests
{
    // ------------------------------------------------------------------ the bubble appears

    /// <summary>
    /// The headline behavior: two characters in, the bubble carries the top-ranked history row, and
    /// the popup stays shut.
    /// </summary>
    [Fact]
    public async Task Typing_WithTwoCharacters_ShowsTheTopRankedHistoryRowWithoutOpeningThePopup()
    {
        var history = new RecordingHistoryStore();
        history.Seed("git status");
        history.Seed("grep -rn TODO");
        var grid = new FakeGrid();
        var delay = new GatedDelay();
        CommandAssistController controller = CreateController(history, grid, delay);

        grid.SetLine("gi");
        controller.NotifyInputActivity();
        delay.ReleaseAll();

        await WaitForAsync(() => controller.ViewModel.TopSuggestionText == "git status");
        Assert.True(controller.ViewModel.IsVisible);
        Assert.False(controller.ViewModel.IsPopupOpen);
        Assert.Equal(0, controller.ViewModel.SelectedIndex);
    }

    /// <summary>
    /// One character is not enough. Checked on the store as well as on the surface: the floor is
    /// meant to save the work, not just hide the result.
    /// </summary>
    [Fact]
    public async Task Typing_WithOneCharacter_ShowsNothingAndDoesNotTouchTheStore()
    {
        var history = new RecordingHistoryStore();
        history.Seed("git status");
        var grid = new FakeGrid();
        var delay = new GatedDelay();
        CommandAssistController controller = CreateController(history, grid, delay);

        grid.SetLine("g");
        controller.NotifyInputActivity();
        delay.ReleaseAll();

        await WaitForAsync(() => controller.ViewModel.QueryText == "g");
        Assert.Empty(controller.Suggestions);
        Assert.False(controller.ViewModel.IsVisible);
        Assert.Equal(0, history.SearchCount);
    }

    /// <summary>
    /// Backspacing below the floor takes the bubble away again, which is why the below-floor pass
    /// publishes an empty outcome instead of returning early.
    /// </summary>
    [Fact]
    public async Task Typing_BackToOneCharacter_TakesTheBubbleDown()
    {
        var history = new RecordingHistoryStore();
        history.Seed("git status");
        var grid = new FakeGrid();
        var delay = new GatedDelay();
        CommandAssistController controller = CreateController(history, grid, delay);

        grid.SetLine("gi");
        controller.NotifyInputActivity();
        delay.ReleaseAll();
        await WaitForAsync(() => controller.ViewModel.IsVisible);

        grid.SetLine("g");
        controller.NotifyInputActivity();
        delay.ReleaseAll();

        await WaitForAsync(() => !controller.ViewModel.IsVisible);
        Assert.Empty(controller.Suggestions);
    }

    /// <summary>
    /// "Merged" is the load-bearing word in the task line: the passive pass ranks history and paths
    /// against each other rather than picking a source.
    /// </summary>
    [Fact]
    public async Task Typing_WithBothSourcesInScope_RanksHistoryAndPathsTogether()
    {
        var history = new RecordingHistoryStore();
        history.Seed("cd ./docs");
        var grid = new FakeGrid();
        var delay = new GatedDelay();
        CommandAssistController controller = CreateController(
            history,
            grid,
            delay,
            pathProvider: new FixedPathSuggestionProvider(CreatePathRow("cd ./docs/api")));

        grid.SetLine("cd ./d");
        controller.NotifyInputActivity();
        delay.ReleaseAll();

        await WaitForAsync(() => controller.Suggestions.Count > 1);
        Assert.Contains(controller.Suggestions, row => row.Type == AssistSuggestionType.History);
        Assert.Contains(controller.Suggestions, row => row.Type == AssistSuggestionType.Path);
    }

    // ------------------------------------------------- PSReadLine's inline prediction (PR #293, B1)

    /// <summary>
    /// The headline of PR #293's first blocker: with a prediction painted past the cursor, the bubble
    /// ranks on the two characters the user typed, not on the whole painted line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The grid returns <c>Text = "echo hello-world"</c> with <c>CursorOffset = 2</c>, which is what
    /// PSReadLine produces for a user who has typed <c>ec</c> with
    /// <c>PredictionSource HistoryAndPlugin</c> / <c>PredictionViewStyle InlineView</c> on. The
    /// prediction's cells are real and dim; nothing on the grid marks them as not-input.
    /// </para>
    /// <para>
    /// <strong>Mutation check:</strong> revert <c>SuggestionOrchestrator</c> to <c>TryReadQuery()?.Text</c>
    /// and the recall arrives for <c>"echo hello-world"</c>, which no seeded row matches as a prefix -
    /// <c>echo hello</c> loses to nothing because the query is longer than every candidate.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Typing_WithAnInlinePredictionPainted_RanksOnTheTextBeforeTheCursor()
    {
        var history = new RecordingHistoryStore();
        history.Seed("echo hello");
        var grid = new FakeGrid();
        var delay = new GatedDelay();
        CommandAssistController controller = CreateController(history, grid, delay);

        grid.SetLine("echo hello-world", cursorOffset: 2);
        controller.NotifyInputActivity();
        delay.ReleaseAll();

        await WaitForAsync(() => controller.ViewModel.QueryText.Length > 0);
        Assert.Equal("ec", controller.ViewModel.QueryText);
        Assert.Equal(new[] { "ec" }, history.SearchQueries);
        Assert.Equal("echo hello", controller.ViewModel.TopSuggestionText);
    }

    /// <summary>
    /// The floor measures the typed prefix too, so a long prediction can no longer game it: one typed
    /// character shows nothing and costs no recall, however much the shell painted after the cursor.
    /// </summary>
    /// <remarks>
    /// This is the half of blocker 1 that was not just a wrong ranking. The floor exists to stop the
    /// bubble appearing on a barely-touched line, and measuring the painted line meant the very first
    /// character of a recognised command cleared it - the bubble appeared one keystroke early, every time,
    /// for exactly the users who have predictions on.
    /// </remarks>
    [Fact]
    public async Task Typing_OneCharacterWithALongPrediction_StaysBelowTheFloorAndDoesNotRecall()
    {
        var history = new RecordingHistoryStore();
        history.Seed("echo hello");
        var grid = new FakeGrid();
        var delay = new GatedDelay();
        CommandAssistController controller = CreateController(history, grid, delay);

        grid.SetLine("echo hello-world", cursorOffset: 1);
        controller.NotifyInputActivity();
        delay.ReleaseAll();

        await WaitForAsync(() => controller.ViewModel.QueryText == "e");
        Assert.Empty(controller.Suggestions);
        Assert.False(controller.ViewModel.IsVisible);
        Assert.Equal(0, history.SearchCount);
    }

    /// <summary>
    /// The ordinary case is untouched: cursor at the end of the line means the whole line is the query,
    /// which is what every pre-existing test in this file asserts and what this pins by name.
    /// </summary>
    [Fact]
    public async Task Typing_WithTheCursorAtTheEnd_RanksOnTheWholeLine()
    {
        var history = new RecordingHistoryStore();
        history.Seed("echo hello");
        var grid = new FakeGrid();
        var delay = new GatedDelay();
        CommandAssistController controller = CreateController(history, grid, delay);

        grid.SetLine("echo h");
        controller.NotifyInputActivity();
        delay.ReleaseAll();

        await WaitForAsync(() => controller.ViewModel.QueryText == "echo h");
        Assert.Equal(new[] { "echo h" }, history.SearchQueries);
        Assert.Equal("echo hello", controller.ViewModel.TopSuggestionText);
    }

    /// <summary>
    /// The hint strip stops promising "insert" while a prediction is showing, because
    /// <c>CommandAssistInsertionPlanner</c> refuses on a mid-line cursor and a promise it cannot keep is
    /// worse than a shorter strip. Browse and close survive - both still work.
    /// </summary>
    [Fact]
    public async Task Typing_WithAnInlinePredictionPainted_DropsTheInsertClauseFromTheHintStrip()
    {
        var history = new RecordingHistoryStore();
        history.Seed("echo hello");
        var grid = new FakeGrid();
        var delay = new GatedDelay();
        CommandAssistController controller = CreateController(history, grid, delay);

        grid.SetLine("echo hello-world", cursorOffset: 2);
        controller.NotifyInputActivity();
        delay.ReleaseAll();

        await WaitForAsync(() => controller.ViewModel.IsVisible);
        Assert.DoesNotContain("insert", controller.ViewModel.Bubble.ShortcutHintText, StringComparison.Ordinal);
        Assert.Contains("browse", controller.ViewModel.Bubble.ShortcutHintText, StringComparison.Ordinal);
        Assert.Contains("close", controller.ViewModel.Bubble.ShortcutHintText, StringComparison.Ordinal);
    }

    /// <summary>And with no prediction the clause is back, so the test above is not vacuous.</summary>
    [Fact]
    public async Task Typing_WithNoPrediction_KeepsTheInsertClause()
    {
        var history = new RecordingHistoryStore();
        history.Seed("echo hello");
        var grid = new FakeGrid();
        var delay = new GatedDelay();
        CommandAssistController controller = CreateController(history, grid, delay);

        grid.SetLine("echo h");
        controller.NotifyInputActivity();
        delay.ReleaseAll();

        await WaitForAsync(() => controller.ViewModel.IsVisible);
        Assert.Contains("insert", controller.ViewModel.Bubble.ShortcutHintText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the kill switch

    /// <summary>
    /// The kill switch, and the mutation check for the whole feature: with the passive bubble off, a
    /// history-only match produces no bubble and no recall - the M4.3 behavior exactly.
    /// </summary>
    [Fact]
    public async Task Typing_WhenThePassiveBubbleIsDisabled_StaysQuiet()
    {
        var history = new RecordingHistoryStore();
        history.Seed("git status");
        var grid = new FakeGrid();
        var delay = new GatedDelay();
        CommandAssistController controller = CreateController(history, grid, delay);
        controller.SetFeaturePolicy(isHistoryEnabled: true, isPassiveBubbleEnabled: false);

        grid.SetLine("gi");
        controller.NotifyInputActivity();
        delay.ReleaseAll();

        await WaitForAsync(() => controller.ViewModel.QueryText == "gi");
        Assert.Empty(controller.Suggestions);
        Assert.False(controller.ViewModel.IsVisible);
        Assert.Equal(0, history.SearchCount);
    }

    /// <summary>
    /// Off means "no unasked-for history", not "no assist": an explicit session still gets the full
    /// scope, which is what makes the switch a policy rather than a second master flag.
    /// </summary>
    [Fact]
    public async Task ExplicitSession_WhenThePassiveBubbleIsDisabled_StillRanksHistory()
    {
        var history = new RecordingHistoryStore();
        history.Seed("git status");
        var grid = new FakeGrid();
        CommandAssistController controller = CreateController(history, grid);
        controller.SetFeaturePolicy(isHistoryEnabled: true, isPassiveBubbleEnabled: false);

        controller.OpenHistorySearch();

        await WaitForAsync(() => controller.Suggestions.Count > 0);
        Assert.True(controller.ViewModel.IsVisible);
    }

    // ------------------------------------------------------------------ Escape

    /// <summary>
    /// Escape has to outlive the keystroke that follows it, or it does nothing at all: before this
    /// phase the next character queued a passive refresh and the bubble came straight back.
    /// </summary>
    [Fact]
    public async Task Escape_KeepsTheBubbleDownForTheRestOfTheCommand()
    {
        var history = new RecordingHistoryStore();
        history.Seed("git status");
        var grid = new FakeGrid();
        var delay = new GatedDelay();
        CommandAssistController controller = CreateController(history, grid, delay);

        grid.SetLine("gi");
        controller.NotifyInputActivity();
        delay.ReleaseAll();
        await WaitForAsync(() => controller.ViewModel.IsVisible);

        Assert.True(controller.HandleEscape());
        int searchesAtEscape = history.SearchCount;

        grid.SetLine("git");
        controller.NotifyInputActivity();
        grid.SetLine("git ");
        controller.NotifyInputActivity();
        delay.ReleaseAll();
        await Task.Delay(50);

        Assert.False(controller.ViewModel.IsVisible);
        Assert.Empty(controller.Suggestions);
        Assert.Equal(searchesAtEscape, history.SearchCount);
    }

    /// <summary>The scope is one command line: the next one starts unsuppressed.</summary>
    [Fact]
    public async Task Escape_ThenSubmission_LetsTheBubbleComeBackOnTheNextLine()
    {
        var history = new RecordingHistoryStore();
        history.Seed("git status");
        var grid = new FakeGrid();
        var delay = new GatedDelay();
        CommandAssistController controller = CreateController(history, grid, delay);

        grid.SetLine("gi");
        controller.NotifyInputActivity();
        delay.ReleaseAll();
        await WaitForAsync(() => controller.ViewModel.IsVisible);
        controller.HandleEscape();

        await controller.HandleEnterAsync("git status");
        grid.SetLine("gi");
        controller.NotifyInputActivity();
        delay.ReleaseAll();

        await WaitForAsync(() => controller.ViewModel.IsVisible);
        Assert.Equal("git status", controller.ViewModel.TopSuggestionText);
    }

    /// <summary>
    /// Suppression is about surfaces the user did not ask for. Having dismissed a bubble must not
    /// disable <c>Ctrl+R</c> for the rest of the line.
    /// </summary>
    [Fact]
    public async Task Escape_DoesNotSuppressAnExplicitHistorySearch()
    {
        var history = new RecordingHistoryStore();
        history.Seed("git status");
        var grid = new FakeGrid();
        var delay = new GatedDelay();
        CommandAssistController controller = CreateController(history, grid, delay);

        grid.SetLine("gi");
        controller.NotifyInputActivity();
        delay.ReleaseAll();
        await WaitForAsync(() => controller.ViewModel.IsVisible);
        controller.HandleEscape();

        Assert.True(controller.OpenHistorySearch());

        await WaitForAsync(() => controller.Suggestions.Count > 0);
        Assert.True(controller.ViewModel.IsVisible);
        Assert.True(controller.ViewModel.IsPopupOpen);
    }

    // ------------------------------------------------- suppression has to end somehow (PR #293, B2)

    /// <summary>
    /// The two-keystroke repro from the review. Escape takes the bubble down and suppresses it for the
    /// command line; a second Escape falls through to the shell, which clears the line without submitting
    /// anything; the shell prints a fresh prompt (<c>OSC 133;B</c>); and the bubble must work again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before <c>BeginCommandLine</c> the suppression flag was cleared by <c>CompleteSubmission</c> and
    /// nothing else, and <c>CompleteSubmission</c> only runs for an <c>Enter</c> Command Assist saw. So
    /// this sequence - which costs the user two keypresses and no unusual configuration - disabled the
    /// passive bubble for the rest of the pane's life.
    /// </para>
    /// <para>
    /// <strong>Mutation check:</strong> remove the <c>_state.BeginCommandLine()</c> call from the
    /// <c>CommandStarted</c> branch and this fails with the bubble still down.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Escape_ThenANewPromptWithoutASubmission_LetsTheBubbleComeBack()
    {
        var history = new RecordingHistoryStore();
        history.Seed("git status");
        var grid = new FakeGrid();
        var delay = new GatedDelay();
        CommandAssistController controller = CreateController(history, grid, delay);

        grid.SetLine("gi");
        controller.NotifyInputActivity();
        delay.ReleaseAll();
        await WaitForAsync(() => controller.ViewModel.IsVisible);

        // First Escape: ours. Second Escape: nothing is visible, so it falls through to the shell, which
        // clears the line. No OSC 133;C is emitted - nothing was submitted.
        Assert.True(controller.HandleEscape());
        Assert.False(controller.HandleEscape());

        // The shell reprints its prompt and hands back the line editor.
        grid.OpenPrompt(controller);

        grid.SetLine("gi");
        controller.NotifyInputActivity();
        delay.ReleaseAll();

        await WaitForAsync(() => controller.ViewModel.IsVisible);
        Assert.Equal("git status", controller.ViewModel.TopSuggestionText);
    }

    /// <summary>
    /// A submission Command Assist never saw a key for - a pasted line ending in a newline, a broadcast
    /// send, an agent-sent command - clears the surface, because <c>OSC 133;C</c> says the line was
    /// accepted.
    /// </summary>
    /// <remarks>
    /// Without this the bubble ranked for the line that just ran stayed on screen over that command's
    /// output, still offering to insert into a line editor the shell no longer owns.
    /// </remarks>
    [Fact]
    public async Task CommandAccepted_WithoutALocalEnter_ClearsTheSurface()
    {
        var history = new RecordingHistoryStore();
        history.Seed("git status");
        var grid = new FakeGrid();
        var delay = new GatedDelay();
        CommandAssistController controller = CreateController(history, grid, delay);

        grid.SetLine("gi");
        controller.NotifyInputActivity();
        delay.ReleaseAll();
        await WaitForAsync(() => controller.ViewModel.IsVisible);

        await controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.CommandAccepted,
            Timestamp: DateTimeOffset.UtcNow,
            CommandText: "git status",
            WorkingDirectory: null,
            ExitCode: null,
            Duration: null));

        Assert.False(controller.ViewModel.IsVisible);
        Assert.False(controller.ViewModel.IsPopupOpen);
        Assert.Empty(controller.Suggestions);
        Assert.Equal(string.Empty, controller.ViewModel.QueryText);
    }

    /// <summary>
    /// Escape inside the debounce window cancels the pass that has not landed yet and takes the
    /// suppression flag - while still returning "not handled", so the key reaches the shell.
    /// </summary>
    /// <remarks>
    /// The old early-out on <c>IsVisible</c> made Escape a no-op for the 75 ms after a keystroke, which is
    /// the window in which a user who does not want the bubble is most likely to press it: they typed, saw
    /// nothing yet, and pressed Escape - and the bubble appeared anyway (PR #293 review, non-blocking 1).
    /// </remarks>
    [Fact]
    public async Task Escape_DuringTheDebounceWindow_CancelsThePassAndSuppresses()
    {
        var history = new RecordingHistoryStore();
        history.Seed("git status");
        var grid = new FakeGrid();
        var delay = new GatedDelay();
        CommandAssistController controller = CreateController(history, grid, delay);

        grid.SetLine("gi");
        controller.NotifyInputActivity();
        Assert.False(controller.ViewModel.IsVisible);

        // Not handled - there is nothing on screen, so the shell gets its Escape - but the pending pass is
        // cancelled and the line is suppressed.
        Assert.False(controller.HandleEscape());

        delay.ReleaseAll();
        grid.SetLine("git");
        controller.NotifyInputActivity();
        delay.ReleaseAll();
        await Task.Delay(50);

        Assert.False(controller.ViewModel.IsVisible);
        Assert.Empty(controller.Suggestions);
        Assert.Equal(0, history.SearchCount);
    }

    /// <summary>
    /// And Escape on a genuinely untouched prompt is still a plain no-op: no pass, nothing to cancel,
    /// nothing suppressed, so the next keystroke gets a bubble.
    /// </summary>
    [Fact]
    public async Task Escape_OnAnUntouchedPrompt_DoesNotSuppressTheNextKeystroke()
    {
        var history = new RecordingHistoryStore();
        history.Seed("git status");
        var grid = new FakeGrid();
        var delay = new GatedDelay();
        CommandAssistController controller = CreateController(history, grid, delay);

        Assert.False(controller.HandleEscape());

        grid.SetLine("gi");
        controller.NotifyInputActivity();
        delay.ReleaseAll();

        await WaitForAsync(() => controller.ViewModel.IsVisible);
        Assert.Equal("git status", controller.ViewModel.TopSuggestionText);
    }

    // ------------------------------------------------------------------ the debounce

    /// <summary>
    /// The debounce, as a coalescing property: five characters typed before the delay elapses cost
    /// one recall and one ranking pass.
    /// </summary>
    /// <remarks>
    /// This is the mutation tripwire for the debounce itself. Setting the interval to zero - which is
    /// how the mechanism is disabled - makes this fail with five searches, which is what
    /// <see cref="Typing_WithTheDebounceDisabled_RanksOncePerKeystroke"/> pins from the other side.
    /// </remarks>
    [Fact]
    public async Task Typing_ABurstOfKeystrokes_ProducesASingleRankingPass()
    {
        var history = new RecordingHistoryStore();
        history.Seed("git status");
        var grid = new FakeGrid();
        var delay = new GatedDelay();
        CommandAssistController controller = CreateController(history, grid, delay);

        foreach (string line in new[] { "gi", "git", "git ", "git s", "git st" })
        {
            grid.SetLine(line);
            controller.NotifyInputActivity();
        }

        Assert.Equal(0, history.SearchCount);
        delay.ReleaseAll();

        await WaitForAsync(() => controller.ViewModel.QueryText == "git st");
        Assert.Equal(1, history.SearchCount);
        Assert.Equal(5, delay.RequestCount);
    }

    /// <summary>
    /// The same burst without the debounce, so the test above cannot pass by accident: the passes
    /// are per keystroke, which is the behavior V2 Phase 3b replaced.
    /// </summary>
    [Fact]
    public async Task Typing_WithTheDebounceDisabled_RanksOncePerKeystroke()
    {
        var history = new RecordingHistoryStore();
        history.Seed("git status");
        var grid = new FakeGrid();
        CommandAssistController controller = CreateController(
            history,
            grid,
            debounce: TimeSpan.Zero);

        foreach (string line in new[] { "gi", "git", "git s" })
        {
            grid.SetLine(line);
            controller.NotifyInputActivity();
        }

        await WaitForAsync(() => history.SearchCount == 3);
    }

    /// <summary>
    /// An explicit surface is not debounced: <c>Ctrl+R</c> is a single deliberate act, and the delay
    /// would be pure latency. The gated delay never releases here, so a debounced explicit pass would
    /// hang rather than merely be slow.
    /// </summary>
    [Fact]
    public async Task OpenHistorySearch_IsNotDebounced()
    {
        var history = new RecordingHistoryStore();
        history.Seed("git status");
        var grid = new FakeGrid();
        var delay = new GatedDelay();
        CommandAssistController controller = CreateController(history, grid, delay);

        controller.OpenHistorySearch();

        await WaitForAsync(() => controller.Suggestions.Count > 0);
        Assert.Equal(0, delay.RequestCount);
    }

    // ------------------------------------------------------------------ gating decoupled (task 3)

    /// <summary>
    /// History off, paths on. The decoupling in one test: nothing is recalled, and the assist is
    /// still useful.
    /// </summary>
    [Fact]
    public async Task Typing_WithHistoryDisabled_StillOffersPathSuggestions()
    {
        var history = new RecordingHistoryStore();
        history.Seed("cd ./docs");
        var grid = new FakeGrid();
        var delay = new GatedDelay();
        CommandAssistController controller = CreateController(
            history,
            grid,
            delay,
            pathProvider: new FixedPathSuggestionProvider(CreatePathRow("cd ./docs/api")));
        controller.SetFeaturePolicy(isHistoryEnabled: false, isPassiveBubbleEnabled: true);

        grid.SetLine("cd ./d");
        controller.NotifyInputActivity();
        delay.ReleaseAll();

        await WaitForAsync(() => controller.ViewModel.IsVisible);
        Assert.Equal(0, history.SearchCount);
        Assert.All(controller.Suggestions, row => Assert.Equal(AssistSuggestionType.Path, row.Type));
    }

    /// <summary>With history off nothing is written, which is the other half of what the flag gates.</summary>
    [Fact]
    public async Task HandleEnterAsync_WithHistoryDisabled_CapturesNothing()
    {
        var history = new RecordingHistoryStore();
        var grid = new FakeGrid();
        CommandAssistController controller = CreateController(history, grid);
        controller.SetFeaturePolicy(isHistoryEnabled: false, isPassiveBubbleEnabled: true);

        await controller.HandleEnterAsync("git status");

        Assert.Empty(history.Entries);
    }

    /// <summary>And with it on, the same submission is captured - so the test above is not vacuous.</summary>
    [Fact]
    public async Task HandleEnterAsync_WithHistoryEnabled_Captures()
    {
        var history = new RecordingHistoryStore();
        var grid = new FakeGrid();
        CommandAssistController controller = CreateController(history, grid);

        await controller.HandleEnterAsync("git status");

        Assert.Single(history.Entries);
    }

    // ------------------------------------------------------------------ helpers

    private static CommandAssistController CreateController(
        RecordingHistoryStore history,
        FakeGrid grid,
        GatedDelay? delay = null,
        IPathSuggestionProvider? pathProvider = null,
        TimeSpan? debounce = null)
    {
        var controller = new CommandAssistController(
            history,
            new SecretsFilter(),
            new CommandAssistSuggestionEngine(pathProvider ?? new NoPathSuggestionProvider()),
            snippetStore: null,
            commandDocsProvider: null,
            recipeProvider: null,
            errorInsightService: null,
            modeRouter: null,
            resultBuilder: null,
            queryProvider: grid.Read,
            renderedSurfaceProbe: null,
            dispatch: null,
            passiveRefreshDebounce: debounce ?? (delay == null ? TimeSpan.Zero : CommandAssistController_DefaultDebounce),
            refreshDelay: delay == null ? null : delay.Delay);

        grid.OpenPrompt(controller);
        return controller;
    }

    /// <summary>
    /// Any non-zero interval will do when the delay itself is gated: the number is what the injected
    /// delay is handed, and the gate decides when it completes.
    /// </summary>
    private static readonly TimeSpan CommandAssistController_DefaultDebounce = TimeSpan.FromMilliseconds(75);

    private static async Task WaitForAsync(Func<bool> predicate, int timeoutMs = 2000)
    {
        int elapsed = 0;
        while (!predicate())
        {
            if (elapsed >= timeoutMs)
            {
                throw new TimeoutException("Timed out waiting for test condition.");
            }

            await Task.Delay(10);
            elapsed += 10;
        }
    }

    private static AssistSuggestion CreatePathRow(string text) => new(
        Id: text,
        Type: AssistSuggestionType.Path,
        DisplayText: text,
        InsertText: text,
        Description: null,
        Badges: Array.Empty<string>(),
        Score: 50,
        WorkingDirectory: null,
        LastUsedAt: null,
        ExitCode: null);

    /// <summary>
    /// A stand-in for the debounce delay that completes only when the test says so, and cancels the
    /// moment the pass it belongs to is superseded.
    /// </summary>
    /// <remarks>
    /// The point is determinism. The orchestrator coalesces by cancelling the previous pass's token,
    /// so the observable property - "n keystrokes, one ranking pass" - does not depend on how long
    /// 75 ms is on the machine running the test.
    /// </remarks>
    private sealed class GatedDelay
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _pending = new();
        private int _requestCount;

        /// <summary>How many debounced passes asked to wait.</summary>
        public int RequestCount => Volatile.Read(ref _requestCount);

        public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_gate)
            {
                _pending.Add(completion);
            }

            // Mirrors Task.Delay's cancellation contract, which is what makes the supersession path
            // reachable at all: the orchestrator cancels the previous pass's token, and the pass
            // observes it as an OperationCanceledException out of the delay.
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }

        public void ReleaseAll()
        {
            TaskCompletionSource[] pending;
            lock (_gate)
            {
                pending = _pending.ToArray();
                _pending.Clear();
            }

            foreach (TaskCompletionSource completion in pending)
            {
                completion.TrySetResult();
            }
        }
    }

    private sealed class FakeGrid
    {
        private readonly object _gate = new();
        private AssistQuerySnapshot? _snapshot;

        public AssistQuerySnapshot? Read()
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }

        /// <summary>
        /// Paints <paramref name="text"/> with the cursor at <paramref name="cursorOffset"/>, defaulting to
        /// the end of the line.
        /// </summary>
        /// <remarks>
        /// The cursor knob is what makes PSReadLine's inline prediction expressible (PR #293 review,
        /// blocker 1): a prediction is real painted cells to the right of the cursor, so
        /// <c>SetLine("echo hello-world", cursorOffset: 2)</c> is exactly what the reader hands back when
        /// the user has typed <c>ec</c> and the shell has offered the rest.
        /// </remarks>
        public void SetLine(string text, int? cursorOffset = null)
        {
            lock (_gate)
            {
                _snapshot = new AssistQuerySnapshot(
                    text,
                    cursorOffset ?? text.Length,
                    IsMultiline: false,
                    RightPromptTrimmed: false);
            }
        }

        /// <summary><c>OSC 133;B</c>: the prompt is printed and the line editor is the user's.</summary>
        public void OpenPrompt(CommandAssistController controller)
        {
            SetLine(string.Empty);
            controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
                Type: ShellIntegrationEventType.CommandStarted,
                Timestamp: DateTimeOffset.UtcNow,
                CommandText: null,
                WorkingDirectory: null,
                ExitCode: null,
                Duration: null)).GetAwaiter().GetResult();
        }
    }

    private sealed class NoPathSuggestionProvider : IPathSuggestionProvider
    {
        public IReadOnlyList<AssistSuggestion> GetSuggestions(CommandAssistQueryContext context, int maxResults)
            => Array.Empty<AssistSuggestion>();
    }

    private sealed class FixedPathSuggestionProvider : IPathSuggestionProvider
    {
        private readonly AssistSuggestion[] _suggestions;

        public FixedPathSuggestionProvider(params AssistSuggestion[] suggestions)
        {
            _suggestions = suggestions;
        }

        public IReadOnlyList<AssistSuggestion> GetSuggestions(CommandAssistQueryContext context, int maxResults)
            => context.IncludePathSuggestions ? _suggestions : Array.Empty<AssistSuggestion>();
    }

    /// <summary>
    /// A history store that counts recalls, so a test can assert that a pass never reached for
    /// history - which is the observable difference between the scopes.
    /// </summary>
    private sealed class RecordingHistoryStore : IHistoryStore
    {
        private readonly object _gate = new();
        private readonly List<CommandHistoryEntry> _entries = new();
        private int _searchCount;

        public IReadOnlyList<CommandHistoryEntry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return _entries.ToArray();
                }
            }
        }

        public int SearchCount => Volatile.Read(ref _searchCount);

        /// <summary>
        /// Every query the store was asked for, in order. Asserting on the query rather than only on the
        /// rows is what pins <em>what the pass ranked on</em> - the whole subject of PR #293's blocker 1,
        /// where the rows happened to look plausible while the query was the shell's guess.
        /// </summary>
        public IReadOnlyList<string> SearchQueries
        {
            get
            {
                lock (_gate)
                {
                    return _searchQueries.ToArray();
                }
            }
        }

        private readonly List<string> _searchQueries = new();

        public void Seed(string commandText)
        {
            lock (_gate)
            {
                _entries.Add(new CommandHistoryEntry(
                    Id: Guid.NewGuid().ToString("N"),
                    CommandText: commandText,
                    ExecutedAt: DateTimeOffset.UtcNow,
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
            }
        }

        public Task AppendAsync(CommandHistoryEntry entry, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _entries.Add(entry);
            }

            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _entries.Clear();
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CommandHistoryEntry>> GetRecentAsync(int maxResults, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _searchCount);
            lock (_gate)
            {
                IReadOnlyList<CommandHistoryEntry> results = _entries
                    .OrderByDescending(entry => entry.ExecutedAt)
                    .Take(Math.Max(0, maxResults))
                    .ToArray();
                return Task.FromResult(results);
            }
        }

        public Task<IReadOnlyList<CommandHistoryEntry>> SearchAsync(string query, int maxCandidates, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _searchCount);
            string needle = query.Trim();
            lock (_gate)
            {
                _searchQueries.Add(query);
                IReadOnlyList<CommandHistoryEntry> results = _entries
                    .Where(entry => IsCandidate(entry.CommandText, needle))
                    .OrderByDescending(entry => entry.ExecutedAt)
                    .Take(Math.Max(0, maxCandidates))
                    .ToArray();
                return Task.FromResult(results);
            }
        }

        /// <summary>The documented recall gate: case-insensitive subsequence, no scoring.</summary>
        private static bool IsCandidate(string commandText, string query)
        {
            if (string.IsNullOrWhiteSpace(commandText))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            string text = commandText.ToLowerInvariant();
            string needle = query.ToLowerInvariant();
            int needleIndex = 0;
            for (int i = 0; i < text.Length && needleIndex < needle.Length; i++)
            {
                if (text[i] == needle[needleIndex])
                {
                    needleIndex++;
                }
            }

            return needleIndex == needle.Length;
        }

        public Task<bool> TryUpdateExecutionResultAsync(string entryId, int? exitCode, long? durationMs, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}

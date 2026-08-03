using NovaTerminal.CommandAssist.Application;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using System.Diagnostics;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class CommandAssistControllerTests
{
    [Fact]
    public void ResultBuilder_WhenGivenDocItems_BuildsDocSuggestions()
    {
        var builder = new CommandAssistResultBuilder();

        IReadOnlyList<AssistSuggestion> results = builder.BuildHelpSuggestions(
            [new CommandHelpItem("git checkout", "git checkout <branch>", "Switch branches.", "bash", ["Doc", "Git"])],
            AssistSuggestionType.Doc);

        Assert.Single(results);
        Assert.Equal(AssistSuggestionType.Doc, results[0].Type);
        Assert.Equal("Switch branches.", results[0].Description);
        Assert.Contains("Doc", results[0].Badges);
    }

    [Fact]
    public void ResultBuilder_WhenGivenRecipeItems_BuildsRecipeSuggestions()
    {
        var builder = new CommandAssistResultBuilder();

        IReadOnlyList<AssistSuggestion> results = builder.BuildHelpSuggestions(
            [new CommandHelpItem("git recipe", "git status --short", "Show concise status.", "bash", ["Recipe"])],
            AssistSuggestionType.Recipe);

        Assert.Single(results);
        Assert.Equal(AssistSuggestionType.Recipe, results[0].Type);
        Assert.Equal("Show concise status.", results[0].Description);
    }

    [Fact]
    public void ResultBuilder_WhenGivenFixItems_BuildsFixSuggestions()
    {
        var builder = new CommandAssistResultBuilder();

        IReadOnlyList<AssistSuggestion> results = builder.BuildFixSuggestions(
            [new CommandFixSuggestion("Did you mean git?", "git status", "Closest local match.", 0.95, ["Fix", "Typo"])]);

        Assert.Single(results);
        Assert.Equal(AssistSuggestionType.Fix, results[0].Type);
        Assert.Equal("Closest local match.", results[0].Description);
        Assert.Equal("git status", results[0].InsertText);
        Assert.Equal(0.95, results[0].Score);
    }

    [Fact]
    public void ResultBuilder_WhenGivenExistingSuggestions_PreservesSharedRowShape()
    {
        var builder = new CommandAssistResultBuilder();
        AssistSuggestion existing = new(
            Id: "history-1",
            Type: AssistSuggestionType.History,
            DisplayText: "git status",
            InsertText: "git status",
            Description: null,
            Badges: ["Worked"],
            Score: 10,
            WorkingDirectory: @"C:\repo",
            LastUsedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00"),
            ExitCode: 0);

        IReadOnlyList<AssistSuggestion> results = builder.BuildCombined([existing], Array.Empty<CommandHelpItem>(), Array.Empty<CommandHelpItem>(), Array.Empty<CommandFixSuggestion>());

        Assert.Single(results);
        Assert.Equal(AssistSuggestionType.History, results[0].Type);
        Assert.Contains("Worked", results[0].Badges);
    }

    [Fact]
    public async Task OpenHelpAsync_WhenQueryHasRecognizedCommand_ShowsHelpAndRecipeRows()
    {
        var docsProvider = new RecordingDocsProvider(
            [new CommandHelpItem("git checkout", "git checkout <branch>", "Switch branches.", "bash", ["Doc"])]);
        var recipeProvider = new RecordingRecipeProvider(
            [new CommandHelpItem("git recipe", "git status --short", "Show concise status.", "bash", ["Recipe"])]);
        var grid = new FakeGrid();
        var controller = CreateController(
            suggestionEngine: new CommandAssistSuggestionEngine(),
            commandDocsProvider: docsProvider,
            recipeProvider: recipeProvider,
            grid: grid);
        grid.SetLine("git checkout");

        bool opened = await controller.OpenHelpAsync();

        Assert.True(opened);
        Assert.True(controller.ViewModel.IsVisible);
        Assert.Equal("Help", controller.ViewModel.ModeLabel);
        Assert.True(controller.ViewModel.IsPopupOpen);
        Assert.True(controller.ViewModel.Popup.IsVisible);
        Assert.Contains(controller.Suggestions, item => item.Type == AssistSuggestionType.Doc);
        Assert.Contains(controller.Suggestions, item => item.Type == AssistSuggestionType.Recipe);
        Assert.Equal("git", docsProvider.LastQuery?.CommandToken);
    }

    [Fact]
    public async Task HandleCommandFailureAsync_WhenInsightIsHighConfidence_OpensFixMode()
    {
        var controller = CreateController(
            errorInsightService: new RecordingErrorInsightService(
                [new CommandFixSuggestion("Did you mean git?", "git status", "Closest local match.", 0.95, ["Fix"])]));

        bool opened = await controller.HandleCommandFailureAsync(CreateFailureContext("gti status", 127, "command not found"));

        Assert.True(opened);
        Assert.Equal("Fix", controller.ViewModel.ModeLabel);
        Assert.True(controller.ViewModel.IsVisible);
        Assert.True(controller.ViewModel.IsPopupOpen);
        Assert.True(controller.ViewModel.Popup.IsVisible);
        Assert.Equal(AssistSuggestionType.Fix, controller.Suggestions[0].Type);
    }

    [Fact]
    public async Task HandleCommandFailureAsync_WhenInsightIsLowConfidence_DoesNotAutoOpenFixMode()
    {
        var controller = CreateController(
            errorInsightService: new RecordingErrorInsightService(
                [new CommandFixSuggestion("Maybe try something else", "git status", "Low confidence.", 0.2, ["Fix"])]));

        bool opened = await controller.HandleCommandFailureAsync(CreateFailureContext("gti status", 1, "command failed"));

        Assert.False(opened);
        Assert.True(controller.ViewModel.IsVisible);
        Assert.Equal("Fix", controller.ViewModel.ModeLabel);
        Assert.False(controller.ViewModel.IsPopupOpen);
        Assert.False(controller.ViewModel.Popup.IsVisible);
        Assert.Contains(controller.Suggestions, item => item.Type == AssistSuggestionType.Fix);
    }

    [Fact]
    public async Task MoveSelectionDown_WhenLowConfidenceFixAffordanceIsVisible_OpensPopup()
    {
        var controller = CreateController(
            errorInsightService: new RecordingErrorInsightService(
                [new CommandFixSuggestion("Maybe try git status", "git status", "Low confidence.", 0.2, ["Fix"])]));

        await controller.HandleCommandFailureAsync(CreateFailureContext("gti status", 1, "command failed"));

        bool moved = controller.MoveSelectionDown();

        Assert.True(moved);
        Assert.True(controller.ViewModel.IsPopupOpen);
        Assert.True(controller.ViewModel.Popup.IsVisible);
    }

    [Fact]
    public async Task ExplainSelectionAsync_WhenSelectedTextProvided_PassesSelectionIntoHelpQuery()
    {
        var docsProvider = new RecordingDocsProvider(
            [new CommandHelpItem("fatal explanation", "git status", "Explain the failure.", "bash", ["Doc"])]);
        var controller = CreateController(commandDocsProvider: docsProvider);

        bool opened = await controller.ExplainSelectionAsync("fatal: not a git repository");

        Assert.True(opened);
        Assert.Equal("Help", controller.ViewModel.ModeLabel);
        Assert.Equal("fatal: not a git repository", docsProvider.LastQuery?.SelectedText);
    }

    [Fact]
    public async Task OpenHelpAsync_WhenAltScreenActive_KeepsHelperModesHidden()
    {
        var controller = CreateController(
            commandDocsProvider: new RecordingDocsProvider(
                [new CommandHelpItem("git checkout", "git checkout <branch>", "Switch branches.", "bash", ["Doc"])]));
        controller.HandleAltScreenChanged(true);

        bool opened = await controller.OpenHelpAsync("git checkout");

        Assert.False(opened);
        Assert.False(controller.ViewModel.IsVisible);
    }

    [Fact]
    public void ToggleAssist_WhenNotInAltScreen_ShowsAssistBar()
    {
        var controller = CreateController();

        controller.ToggleAssist();

        Assert.True(controller.ViewModel.IsVisible);
    }

    [Fact]
    public void HandleAltScreenChanged_WhenAssistIsVisible_HidesAssistBarImmediately()
    {
        var controller = CreateController();
        controller.ToggleAssist();

        controller.HandleAltScreenChanged(true);

        Assert.False(controller.ViewModel.IsVisible);
    }

    [Fact]
    public void HandleAltScreenChanged_WhenLeavingAltScreen_DoesNotAutoShowAssistAgain()
    {
        var controller = CreateController();
        controller.ToggleAssist();
        controller.HandleAltScreenChanged(true);

        controller.HandleAltScreenChanged(false);

        Assert.False(controller.ViewModel.IsVisible);
    }

    [Fact]
    public void OpenHistorySearch_WhenNotInAltScreen_ReturnsTrue()
    {
        var controller = CreateController();

        bool opened = controller.OpenHistorySearch();

        Assert.True(opened);
        Assert.True(controller.ViewModel.IsVisible);

        // No grid provider on the default harness, so this is a degraded session and the label says
        // so - see CommandAssistGridTruthTests for the pair of cases side by side.
        Assert.Equal("History - recent", controller.ViewModel.ModeLabel);
    }

    [Fact]
    public void OpenHistorySearch_WhenAltScreenActive_ReturnsFalse()
    {
        var controller = CreateController();
        controller.HandleAltScreenChanged(true);

        bool opened = controller.OpenHistorySearch();

        Assert.False(opened);
        Assert.False(controller.ViewModel.IsVisible);
    }

    [Fact]
    public async Task Typing_WhenHistoryExistsAndAssistNotExplicit_DoesNotShowHistorySuggestions()
    {
        var historyStore = new InMemoryHistoryStore();
        historyStore.Seed(
            CreateEntry("git status"),
            CreateEntry("dotnet test"));

        var grid = new FakeGrid();
        var controller = CreateController(
            historyStore: historyStore,
            snippetStore: null,
            suggestionEngine: new CommandAssistSuggestionEngine(),
            grid: grid);

        grid.SetLine("git ");
        controller.NotifyInputActivity();
        await WaitForConditionAsync(() => controller.ViewModel.QueryText == "git ");

        Assert.False(controller.ViewModel.HasSuggestions);
        Assert.Equal(string.Empty, controller.ViewModel.TopSuggestionText);
        Assert.Empty(controller.Suggestions);
    }

    [Fact]
    public async Task Typing_WhenSnippetExistsAndAssistNotExplicit_DoesNotShowSnippetSuggestions()
    {
        var snippetStore = new InMemorySnippetStore();
        snippetStore.Seed(new CommandSnippet(
            Id: "snippet-1",
            Name: "Git Status",
            CommandText: "git status",
            Description: null,
            ShellKind: "pwsh",
            WorkingDirectory: @"C:\repo",
            IsPinned: true,
            CreatedAt: DateTimeOffset.Parse("2026-03-01T09:00:00+00:00"),
            LastUsedAt: DateTimeOffset.Parse("2026-03-01T09:30:00+00:00")));

        var grid = new FakeGrid();
        var controller = CreateController(
            historyStore: new InMemoryHistoryStore(),
            snippetStore: snippetStore,
            suggestionEngine: new CommandAssistSuggestionEngine(),
            grid: grid);

        grid.SetLine("git st");
        controller.NotifyInputActivity();
        await WaitForConditionAsync(() => controller.ViewModel.QueryText == "git st");

        Assert.False(controller.ViewModel.HasSuggestions);
        Assert.Equal(string.Empty, controller.ViewModel.TopSuggestionText);
        Assert.Empty(controller.Suggestions);
    }

    [Fact]
    public async Task Typing_WhenAssistIsExplicit_UpdatesQueryAndTopSuggestion()
    {
        var historyStore = new InMemoryHistoryStore();
        historyStore.Seed(
            CreateEntry("git status"),
            CreateEntry("dotnet test"));

        var grid = new FakeGrid();
        var controller = CreateController(historyStore, grid: grid);
        controller.ToggleAssist();

        grid.SetLine("git ");
        controller.NotifyInputActivity();
        await historyStore.WaitForSearchSettledAsync();
        await WaitForConditionAsync(() => controller.ViewModel.TopSuggestionText == "git status");

        Assert.Equal("git ", controller.ViewModel.QueryText);
        Assert.Equal("git status", controller.ViewModel.TopSuggestionText);
        Assert.False(controller.ViewModel.IsPopupOpen);
        Assert.False(controller.ViewModel.Popup.IsVisible);
    }

    [Fact]
    public async Task Typing_WhenNoSuggestionsExist_HidesSuggestBubble()
    {
        var grid = new FakeGrid();
        var controller = CreateController(new InMemoryHistoryStore(), grid: grid);

        grid.SetLine("zzzz");
        controller.NotifyInputActivity();
        await WaitForConditionAsync(() => controller.ViewModel.QueryText == "zzzz");

        Assert.False(controller.ViewModel.HasSuggestions);
        Assert.False(controller.ViewModel.IsVisible);
        Assert.False(controller.ViewModel.Bubble.IsVisible);
        Assert.False(controller.ViewModel.Popup.IsVisible);
    }

    [Fact]
    public async Task Typing_WhenNoSuggestionsExist_DoesNotFlashVisibleBeforeRefreshCompletes()
    {
        var historyStore = new DelayedHistoryStore(TimeSpan.FromMilliseconds(250));
        var grid = new FakeGrid();
        var controller = CreateController(historyStore, grid: grid);

        grid.SetLine("zzzz");
        controller.NotifyInputActivity();

        Assert.False(controller.ViewModel.IsVisible);
        Assert.False(controller.ViewModel.Bubble.IsVisible);

        await historyStore.WaitForLastSearchAsync();
        Assert.False(controller.ViewModel.IsVisible);
        Assert.False(controller.ViewModel.Bubble.IsVisible);
    }

    [Fact]
    public async Task HandleEnterAsync_PersistsSingleLineRedactedCommand()
    {
        var historyStore = new InMemoryHistoryStore();
        var controller = CreateController(historyStore);

        await controller.HandleEnterAsync("gh auth login --password hunter2");

        Assert.Single(historyStore.Entries);
        Assert.Equal("gh auth login --password [REDACTED]", historyStore.Entries[0].CommandText);
        Assert.True(historyStore.Entries[0].IsRedacted);
        Assert.Equal(string.Empty, controller.ViewModel.QueryText);
    }

    /// <summary>
    /// The submission the host read off the grid can legitimately be multiline - a continuation
    /// entry is one command line - and capture still refuses it. Phase 1c did not relax this: a
    /// multiline grid read contains continuation-prompt cells the user never typed, so persisting
    /// it would put the shell's own decoration into history.
    /// </summary>
    [Fact]
    public async Task HandleEnterAsync_DoesNotPersistMultiLineScriptLikeInput()
    {
        var historyStore = new InMemoryHistoryStore();
        var controller = CreateController(historyStore);

        await controller.HandleEnterAsync("echo one\necho two");

        Assert.Empty(historyStore.Entries);
    }

    /// <summary>
    /// A markless session has no grid to read at Enter, so it captures nothing. This is the
    /// deliberate cost of deleting the shadow buffer: V1 captured whatever its keystroke mirror
    /// held, which for any line the user had edited with keys the mirror could not see was a
    /// command they never ran. Nothing is recoverable; wrong is not.
    /// </summary>
    [Fact]
    public async Task HandleEnterAsync_InADegradedSession_CapturesNothing()
    {
        var historyStore = new InMemoryHistoryStore();
        var controller = CreateController(historyStore);

        await controller.HandleEnterAsync(submittedText: null);

        Assert.Empty(historyStore.Entries);
    }

    /// <summary>
    /// Paste suppression end to end: a single-line paste is rejected by the suppression flag alone,
    /// with none of the other capture guards (multi-line, empty, structured capture) in play.
    /// </summary>
    [Fact]
    public async Task HandleEnterAsync_WhenSubmissionWasPasted_PersistsNothing()
    {
        var historyStore = new InMemoryHistoryStore();
        var controller = CreateController(historyStore);
        controller.NotifyPastedInput();

        // The grid can read pasted text perfectly well, so the submission text here is real; what
        // rejects it is provenance, which is the only thing paste handling still carries.
        await controller.HandleEnterAsync("git status");

        Assert.Empty(historyStore.Entries);
    }

    /// <summary>
    /// The other half of paste suppression: rows may still be on screen and a row may still be
    /// selected, but nothing may be inserted back into a line the user did not compose here.
    /// </summary>
    [Fact]
    public async Task TryGetInsertionText_WhenSubmissionWasPasted_IsInert()
    {
        var suggestionEngine = new DelayedSuggestionEngine(
            delay: TimeSpan.Zero,
            suggestions: new[]
            {
                new AssistSuggestion(
                    Id: "history-1",
                    Type: AssistSuggestionType.History,
                    DisplayText: "git status",
                    InsertText: "git status",
                    Description: null,
                    Badges: ["Worked"],
                    Score: 100,
                    WorkingDirectory: @"C:\repo",
                    LastUsedAt: null,
                    ExitCode: 0)
            });
        var grid = new FakeGrid();
        var controller = CreateController(
            historyStore: new InMemoryHistoryStore(),
            suggestionEngine: suggestionEngine,
            grid: grid);

        grid.SetLine("git stat");
        controller.NotifyPastedInput();
        await WaitForConditionAsync(() => controller.Suggestions.Count > 0);

        // A row is selected, so a false return can only come from the suppression flag.
        Assert.Equal(0, controller.ViewModel.SelectedIndex);
        Assert.False(controller.TryGetInsertionText(out string? insertionText));
        Assert.Null(insertionText);
        Assert.False(controller.TryAcceptSelection(out string? acceptedText));
        Assert.Null(acceptedText);
    }

    /// <summary>
    /// Submitting normalizes the session back to a passive Suggest bubble, including the mode: the
    /// controller this replaced left the mode field reading <c>Search</c> after a Ctrl+R submission.
    /// That was unobservable - every reachable follow-up reset the mode before reading it - and the
    /// normalized behavior is the correct one, so this pins it rather than restoring the stale value.
    /// </summary>
    [Fact]
    public async Task HandleEnterAsync_AfterHistorySearch_LeavesTheSessionInPassiveSuggestScope()
    {
        var historyStore = new InMemoryHistoryStore();
        historyStore.Seed(CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        var grid = new FakeGrid();
        var controller = CreateController(historyStore, grid: grid);

        controller.OpenHistorySearch();
        await historyStore.WaitForSearchSettledAsync();

        await controller.HandleEnterAsync("git status");
        int searchesAfterSubmission = historyStore.SearchCount;

        // Follow-up interactions that refresh with a non-empty command line. Search or
        // explicit-Suggest scope would reach for history; the passive path-only scope this
        // submission normalized back to must not.
        grid.SetLine("git st");
        controller.NotifyInputActivity();
        grid.SetLine("git s");
        controller.NotifyInputActivity();
        await historyStore.WaitForSearchSettledAsync();

        Assert.Equal(searchesAfterSubmission, historyStore.SearchCount);
        Assert.Empty(controller.Suggestions);
        Assert.False(controller.ViewModel.IsVisible);
    }

    [Fact]
    public async Task HandleEnterAsync_WhenHistoryStoreThrows_DoesNotPropagate()
    {
        var controller = CreateController(new ThrowingHistoryStore());

        await controller.HandleEnterAsync("git status");

        Assert.Equal(string.Empty, controller.ViewModel.QueryText);
        Assert.False(controller.ViewModel.IsVisible);
    }

    [Fact]
    public async Task Typing_DoesNotBlockWhileHistorySearchIsPending()
    {
        var historyStore = new DelayedHistoryStore(TimeSpan.FromMilliseconds(250), CreateEntry("git status"));
        var grid = new FakeGrid();
        var controller = CreateController(historyStore, grid: grid);
        controller.ToggleAssist();
        grid.SetLine("git");
        var stopwatch = Stopwatch.StartNew();

        controller.NotifyInputActivity();

        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 100, $"NotifyInputActivity blocked for {stopwatch.ElapsedMilliseconds}ms");

        await historyStore.WaitForLastSearchAsync();
        await WaitForConditionAsync(() => controller.ViewModel.QueryText == "git");
    }

    [Fact]
    public async Task Typing_DoesNotBlockWhileSuggestionEngineIsSlow()
    {
        var suggestionEngine = new DelayedSuggestionEngine(
            delay: TimeSpan.FromMilliseconds(250),
            suggestions: new[]
            {
                new AssistSuggestion(
                    Id: "path-1",
                    Type: AssistSuggestionType.Path,
                    DisplayText: "docs/",
                    InsertText: "cd ./docs/",
                    Description: "Directory",
                    Badges: ["Path", "Directory"],
                    Score: 100,
                    WorkingDirectory: @"C:\repo",
                    LastUsedAt: null,
                    ExitCode: null)
            });
        var grid = new FakeGrid();
        var controller = CreateController(
            historyStore: new InMemoryHistoryStore(),
            suggestionEngine: suggestionEngine,
            grid: grid);
        grid.SetLine("cd ./d");
        var stopwatch = Stopwatch.StartNew();

        controller.NotifyInputActivity();

        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 100, $"NotifyInputActivity blocked for {stopwatch.ElapsedMilliseconds}ms");

        await WaitForConditionAsync(() => controller.ViewModel.QueryText == "cd ./d", timeoutMs: 2000);
    }

    [Fact]
    public async Task MoveSelectionDown_WhenSuggestionsAreVisible_AdvancesSelectedSuggestion()
    {
        var historyStore = new InMemoryHistoryStore();
        historyStore.Seed(
            CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")),
            CreateEntry("git stash", DateTimeOffset.Parse("2026-03-01T09:59:00+00:00")));

        var grid = new FakeGrid();
        var controller = CreateController(historyStore, grid: grid);
        controller.OpenHistorySearch();
        grid.SetLine("git st");
        controller.NotifyInputActivity();
        await historyStore.WaitForSearchSettledAsync();
        await WaitForConditionAsync(() => controller.Suggestions.Count > 1 &&
                                          controller.ViewModel.TopSuggestionText == "git status");

        bool moved = controller.MoveSelectionDown();

        Assert.True(moved);
        Assert.Equal(1, controller.ViewModel.SelectedIndex);
        Assert.True(controller.ViewModel.IsPopupOpen);
        Assert.True(controller.ViewModel.Popup.IsVisible);
    }

    // ------------------------------------- accept-on-Enter, hint strip, mouse (V2 Phase 3a)

    /// <summary>
    /// The owner's first report end to end at the controller: <c>Ctrl+R</c>, move to a row, and now
    /// <c>Enter</c> is the assist's key - and the bubble says so, which it never did before because
    /// <c>ShortcutHintText</c> was on the view-model and bound nowhere.
    /// </summary>
    [Fact]
    public async Task AfterHistorySearchAndASelectionMove_EnterIsArmedAndTheHintSaysSo()
    {
        var historyStore = new InMemoryHistoryStore();
        historyStore.Seed(
            CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")),
            CreateEntry("git stash", DateTimeOffset.Parse("2026-03-01T09:59:00+00:00")));
        var controller = CreateController(historyStore);

        controller.OpenHistorySearch();
        await historyStore.WaitForSearchSettledAsync();
        await WaitForConditionAsync(() => controller.Suggestions.Count > 1);
        controller.MoveSelectionDown();

        Assert.True(controller.IsAcceptOnEnterArmed);
        Assert.True(controller.ViewModel.IsAcceptOnEnterArmed);
        Assert.Contains("Enter insert", controller.ViewModel.Bubble.ShortcutHintText);
        Assert.Contains("Enter insert", controller.ViewModel.Popup.ShortcutHintText);
    }

    /// <summary>
    /// The typing flow, which the keyboard change must not touch: a passive bubble is not a browse
    /// state, so <c>Enter</c> stays the shell's and the hint promises <c>Ctrl+Enter</c> instead.
    /// </summary>
    [Fact]
    public async Task WhileTypingWithOnlyABubbleUp_EnterIsNotArmed()
    {
        var historyStore = new InMemoryHistoryStore();
        historyStore.Seed(CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        var grid = new FakeGrid();
        var controller = CreateController(historyStore, grid: grid);
        controller.ToggleAssist();

        grid.SetLine("git st");
        controller.NotifyInputActivity();
        await historyStore.WaitForSearchSettledAsync();
        await WaitForConditionAsync(() => controller.ViewModel.TopSuggestionText == "git status");

        Assert.False(controller.ViewModel.IsPopupOpen);
        Assert.False(controller.IsAcceptOnEnterArmed);
        Assert.Contains("Ctrl+Enter insert", controller.ViewModel.Bubble.ShortcutHintText);
    }

    /// <summary>
    /// Typing after a browse must disarm <c>Enter</c> again, or the very next command submission would
    /// be swallowed by an insertion. The disarm is structural - typing closes the popup - and this pins
    /// it because the consequence of losing it is the user's Enter not running their command.
    /// </summary>
    [Fact]
    public async Task TypingAfterABrowse_DisarmsEnterAgain()
    {
        var historyStore = new InMemoryHistoryStore();
        historyStore.Seed(
            CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")),
            CreateEntry("git stash", DateTimeOffset.Parse("2026-03-01T09:59:00+00:00")));
        var grid = new FakeGrid();
        var controller = CreateController(historyStore, grid: grid);

        controller.OpenHistorySearch();
        await historyStore.WaitForSearchSettledAsync();
        await WaitForConditionAsync(() => controller.Suggestions.Count > 1);
        controller.MoveSelectionDown();
        Assert.True(controller.IsAcceptOnEnterArmed);

        grid.SetLine("git st");
        controller.NotifyInputActivity();

        Assert.False(controller.IsAcceptOnEnterArmed);
    }

    /// <summary>Escape disarms too: there is no row to accept once the surface is gone.</summary>
    [Fact]
    public async Task Escape_DisarmsEnter()
    {
        var historyStore = new InMemoryHistoryStore();
        historyStore.Seed(CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        var controller = CreateController(historyStore);

        controller.OpenHistorySearch();
        await historyStore.WaitForSearchSettledAsync();
        await WaitForConditionAsync(() => controller.Suggestions.Count > 0);
        controller.MoveSelectionDown();

        controller.HandleEscape();

        Assert.False(controller.IsAcceptOnEnterArmed);
    }

    /// <summary>
    /// A click selects a row by index and opens the popup exactly as an arrow key would - including
    /// arming <c>Enter</c>, so a click-then-Enter works the same as an arrow-then-Enter.
    /// </summary>
    [Fact]
    public async Task TrySelectSuggestionAt_SelectsThatRowAndOpensThePopup()
    {
        var historyStore = new InMemoryHistoryStore();
        historyStore.Seed(
            CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")),
            CreateEntry("git stash", DateTimeOffset.Parse("2026-03-01T09:59:00+00:00")));
        var grid = new FakeGrid();
        var controller = CreateController(historyStore, grid: grid);
        controller.ToggleAssist();
        await historyStore.WaitForSearchSettledAsync();
        await WaitForConditionAsync(() => controller.Suggestions.Count > 1);
        Assert.False(controller.ViewModel.IsPopupOpen);

        bool selected = controller.TrySelectSuggestionAt(1);

        Assert.True(selected);
        Assert.Equal(1, controller.ViewModel.SelectedIndex);
        Assert.True(controller.ViewModel.IsPopupOpen);

        // The row flags the popup view actually reads - the "is this row the selected one" question the
        // deleted IsSuggestionSelectedAt used to answer for nobody but this test (PR #290 review).
        Assert.False(controller.ViewModel.Suggestions[0].IsSelected);
        Assert.True(controller.ViewModel.Suggestions[1].IsSelected);
        Assert.True(controller.IsAcceptOnEnterArmed);
    }

    /// <summary>A click that arrives after the list shrank must not select anything.</summary>
    [Fact]
    public void TrySelectSuggestionAt_WithNoRows_Refuses()
    {
        var controller = CreateController();
        controller.ToggleAssist();

        Assert.False(controller.TrySelectSuggestionAt(0));
        Assert.Empty(controller.ViewModel.Suggestions);
    }

    // ----------------------- an invisible overlay cannot arm Enter (PR #290 review)

    /// <summary>
    /// The first blocker at the controller: the session can believe a popup is up while the pane has
    /// hidden or dimmed the overlay it renders - a passive popup on a short markless-SSH pane, which is
    /// not a user-requested surface and so bypasses none of the pane's hiding. An armed <c>Enter</c>
    /// there is a command line that silently does not submit.
    /// </summary>
    [Fact]
    public async Task WhenTheHostSaysTheOverlayIsNotRendered_EnterIsNotArmedAndTheHintDoesNotPromiseIt()
    {
        var historyStore = new InMemoryHistoryStore();
        historyStore.Seed(
            CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")),
            CreateEntry("git stash", DateTimeOffset.Parse("2026-03-01T09:59:00+00:00")));
        bool isOverlayRendered = true;
        var controller = CreateController(historyStore, renderedSurfaceProbe: () => isOverlayRendered);

        controller.OpenHistorySearch();
        await historyStore.WaitForSearchSettledAsync();
        await WaitForConditionAsync(() => controller.Suggestions.Count > 1);
        controller.MoveSelectionDown();

        // The control: with the overlay on screen this is the armed browse state.
        Assert.True(controller.IsAcceptOnEnterArmed);
        Assert.Contains("Enter insert", controller.ViewModel.Bubble.ShortcutHintText);

        isOverlayRendered = false;
        controller.NotifyRenderedSurfaceVisibilityChanged();

        Assert.False(controller.IsAcceptOnEnterArmed);
        Assert.False(controller.ViewModel.IsAcceptOnEnterArmed);
        Assert.Contains("Ctrl+Enter insert", controller.ViewModel.Bubble.ShortcutHintText);
        Assert.Contains("Ctrl+Enter insert", controller.ViewModel.Popup.ShortcutHintText);

        // Selection and popup state are untouched: the surface is hidden, not dismissed, and it comes
        // back the moment the pane finishes settling.
        Assert.True(controller.ViewModel.IsPopupOpen);
        Assert.Equal(1, controller.ViewModel.SelectedIndex);
    }

    // -------------------- Up belongs to the shell while typing (PR #290 review)

    /// <summary>
    /// The second blocker at the controller. Typing produces a passive bubble, and in it <c>Up</c> is
    /// the shell's history recall - the App asks this before routing the key.
    /// </summary>
    [Fact]
    public async Task WhileTypingWithOnlyABubbleUp_UpIsNotAssistOwned()
    {
        var grid = new FakeGrid();
        CommandAssistController controller = CreatePassiveBubbleController(grid);

        grid.SetLine("cd ./d");
        controller.NotifyInputActivity();
        await WaitForPassiveBubbleAsync(controller);

        Assert.True(controller.ViewModel.IsVisible);
        Assert.False(controller.ViewModel.IsPopupOpen);
        Assert.False(controller.IsSelectionUpOwned);

        // And the strip says so rather than advertising a key the surface has given back to the shell.
        Assert.Contains("Down browse", controller.ViewModel.Bubble.ShortcutHintText);
        Assert.DoesNotContain("Up/Down", controller.ViewModel.Bubble.ShortcutHintText);
    }

    /// <summary>
    /// The other half: <c>Down</c> opens the list from that same state, after which <c>Up</c> navigates
    /// it. Without this the rule above would be indistinguishable from "the assist never gets an arrow".
    /// </summary>
    [Fact]
    public async Task AfterDownOpensTheListFromAPassiveBubble_UpBecomesAssistOwned()
    {
        var grid = new FakeGrid();
        CommandAssistController controller = CreatePassiveBubbleController(grid);

        grid.SetLine("cd ./d");
        controller.NotifyInputActivity();
        await WaitForPassiveBubbleAsync(controller);

        Assert.True(controller.MoveSelectionDown());
        Assert.True(controller.ViewModel.IsPopupOpen);
        Assert.True(controller.IsSelectionUpOwned);
    }

    /// <summary>
    /// An explicitly summoned surface owns both arrows from the first keypress: the user asked for the
    /// list, so reaching it must not depend on which direction they reach in.
    /// </summary>
    [Fact]
    public void AfterTheAssistShortcut_UpIsAssistOwnedWithNoOpenList()
    {
        var controller = CreateController();

        controller.ToggleAssist();

        // Ctrl+Space leaves the popup closed, so this is the summoned-surface branch rather than the
        // open-list one - the two reasons Up can be owned, told apart.
        Assert.False(controller.ViewModel.IsPopupOpen);
        Assert.True(controller.IsSelectionUpOwned);
    }

    /// <summary>
    /// <c>Up</c> at the top of the list is a no-op, and specifically not "select row 0": the old clamp
    /// went through <c>SetSelectedIndex</c>, which opens the popup and arms <c>Enter</c>. That is how a
    /// key pressed for shell history built the surface that then swallowed the next <c>Enter</c>.
    /// </summary>
    [Fact]
    public async Task MoveSelectionUp_AtTheTopOfTheList_DoesNotOpenThePopupOrArmEnter()
    {
        var grid = new FakeGrid();
        CommandAssistController controller = CreatePassiveBubbleController(grid);

        grid.SetLine("cd ./d");
        controller.NotifyInputActivity();
        await WaitForPassiveBubbleAsync(controller);
        Assert.Equal(0, controller.ViewModel.SelectedIndex);
        Assert.False(controller.ViewModel.IsPopupOpen);

        bool moved = controller.MoveSelectionUp();

        Assert.False(moved);
        Assert.False(controller.ViewModel.IsPopupOpen);
        Assert.False(controller.IsAcceptOnEnterArmed);
    }

    /// <summary>
    /// And inside an open list it still navigates. <c>Up</c> from row 1 lands on row 0 and stays there.
    /// </summary>
    [Fact]
    public async Task MoveSelectionUp_InsideAnOpenList_MovesBackUpAndThenStops()
    {
        var historyStore = new InMemoryHistoryStore();
        historyStore.Seed(
            CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")),
            CreateEntry("git stash", DateTimeOffset.Parse("2026-03-01T09:59:00+00:00")));
        var controller = CreateController(historyStore);

        controller.OpenHistorySearch();
        await historyStore.WaitForSearchSettledAsync();
        await WaitForConditionAsync(() => controller.Suggestions.Count > 1);
        Assert.True(controller.MoveSelectionDown());
        Assert.Equal(1, controller.ViewModel.SelectedIndex);

        Assert.True(controller.MoveSelectionUp());
        Assert.Equal(0, controller.ViewModel.SelectedIndex);

        Assert.False(controller.MoveSelectionUp());
        Assert.Equal(0, controller.ViewModel.SelectedIndex);
        Assert.True(controller.ViewModel.IsPopupOpen);
    }

    /// <summary>
    /// Moving the selection must mutate the existing rows rather than replace them. Rebuilding is what
    /// the controller used to do, and it is what made the popup unusable with a mouse: the containers
    /// under the pointer are destroyed on every arrow key, so hover dies and the scroll position jumps
    /// back to the top.
    /// </summary>
    [Fact]
    public async Task MovingTheSelection_MutatesTheExistingRowsRatherThanRebuildingThem()
    {
        var historyStore = new InMemoryHistoryStore();
        historyStore.Seed(
            CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")),
            CreateEntry("git stash", DateTimeOffset.Parse("2026-03-01T09:59:00+00:00")));
        var controller = CreateController(historyStore);

        controller.OpenHistorySearch();
        await historyStore.WaitForSearchSettledAsync();
        await WaitForConditionAsync(() => controller.ViewModel.Suggestions.Count > 1);

        var rowsBefore = controller.ViewModel.Suggestions.ToArray();
        Assert.True(rowsBefore[0].IsSelected);

        controller.MoveSelectionDown();

        Assert.Same(rowsBefore[0], controller.ViewModel.Suggestions[0]);
        Assert.Same(rowsBefore[1], controller.ViewModel.Suggestions[1]);
        Assert.False(rowsBefore[0].IsSelected);
        Assert.True(rowsBefore[1].IsSelected);
        Assert.Equal(" ", rowsBefore[0].SelectionGlyph);
        Assert.Equal(">", rowsBefore[1].SelectionGlyph);
    }

    /// <summary>
    /// The visibility half of the third owner report: <c>Ctrl+R</c> is a surface the user asked for,
    /// which is the fact every placement heuristic in <c>TerminalPane</c> now consults before hiding
    /// anything.
    /// </summary>
    [Fact]
    public void OpenHistorySearch_MarksTheSurfaceAsUserRequested()
    {
        var controller = CreateController();

        Assert.False(controller.IsUserRequestedSurface);

        controller.OpenHistorySearch();

        Assert.True(controller.IsUserRequestedSurface);

        controller.HandleEscape();

        Assert.False(controller.IsUserRequestedSurface);
    }

    /// <summary>
    /// A passive typing bubble is not user-requested, so the conservative markless-SSH placement stack
    /// still applies to it. Without this the bypass would be unconditional.
    /// </summary>
    [Fact]
    public async Task Typing_DoesNotMarkTheSurfaceAsUserRequested()
    {
        var historyStore = new InMemoryHistoryStore();
        historyStore.Seed(CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        var grid = new FakeGrid();
        var controller = CreateController(historyStore, grid: grid);

        grid.SetLine("git st");
        controller.NotifyInputActivity();
        await historyStore.WaitForSearchSettledAsync();

        Assert.False(controller.IsUserRequestedSurface);
    }

    [Fact]
    public void HandleEscape_WhenAssistIsVisible_DismissesAssist()
    {
        var controller = CreateController();
        controller.ToggleAssist();

        bool handled = controller.HandleEscape();

        Assert.True(handled);
        Assert.False(controller.ViewModel.IsVisible);
    }

    [Fact]
    public async Task TryInsertSelection_WhenBufferIsSimpleReplacement_ReturnsSelectedCommandText()
    {
        var historyStore = new InMemoryHistoryStore();
        historyStore.Seed(
            CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")),
            CreateEntry("git stash", DateTimeOffset.Parse("2026-03-01T09:59:00+00:00")));

        var grid = new FakeGrid();
        var controller = CreateController(historyStore, grid: grid);
        controller.ToggleAssist();
        grid.SetLine("git st");
        controller.NotifyInputActivity();
        await historyStore.WaitForSearchSettledAsync();
        await WaitForConditionAsync(() => controller.Suggestions.Count > 1 &&
                                          controller.ViewModel.TopSuggestionText == "git status");
        controller.MoveSelectionDown();

        bool inserted = controller.TryGetInsertionText(out string? insertionText);

        Assert.True(inserted);
        Assert.Equal("git stash", insertionText);
    }

    [Fact]
    public async Task HandleCommandFinished_WhenPendingEntryExists_UpdatesHistoryExitCode()
    {
        var historyStore = new InMemoryHistoryStore();
        var controller = CreateController(historyStore);

        await controller.HandleEnterAsync("git status");
        await controller.HandleCommandFinishedAsync(23);

        Assert.Single(historyStore.Entries);
        Assert.Equal(23, historyStore.Entries[0].ExitCode);
    }

    [Fact]
    public async Task HandleShellIntegrationEventAsync_WhenCommandAccepted_PersistsShellIntegratedEntry()
    {
        var historyStore = new InMemoryHistoryStore();
        var controller = CreateController(historyStore);
        controller.SetShellIntegrationEnabled(true);

        await controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.CommandAccepted,
            Timestamp: DateTimeOffset.Parse("2026-03-09T12:00:00+00:00"),
            CommandText: "gh auth login --password hunter2",
            WorkingDirectory: @"C:\repo",
            ExitCode: null,
            Duration: null));

        Assert.Single(historyStore.Entries);
        Assert.Equal("gh auth login --password [REDACTED]", historyStore.Entries[0].CommandText);
        Assert.True(historyStore.Entries[0].IsRedacted);
        Assert.Equal(CommandCaptureSource.ShellIntegration, historyStore.Entries[0].Source);
    }

    [Fact]
    public async Task HandleEnterAsync_WhenCommandAcceptedMarkerWasObserved_DoesNotPersistHeuristicEntry()
    {
        var historyStore = new InMemoryHistoryStore();
        var controller = CreateController(historyStore);
        controller.SetShellIntegrationEnabled(true);
        await controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.CommandAccepted,
            Timestamp: DateTimeOffset.Parse("2026-03-09T11:59:59+00:00"),
            CommandText: "git status",
            WorkingDirectory: @"C:\repo",
            ExitCode: null,
            Duration: null));

        await controller.HandleEnterAsync("git status");

        Assert.Single(historyStore.Entries);
        Assert.Equal(CommandCaptureSource.ShellIntegration, historyStore.Entries[0].Source);
    }

    [Fact]
    public async Task HandleEnterAsync_WhenShellIntegrationConfiguredButNotConfirmed_PersistsHeuristicFallback()
    {
        var historyStore = new InMemoryHistoryStore();
        var controller = CreateController(historyStore);
        controller.SetShellIntegrationEnabled(true);
        await controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.PromptReady,
            Timestamp: DateTimeOffset.Parse("2026-03-09T11:59:59+00:00"),
            CommandText: null,
            WorkingDirectory: @"C:\repo",
            ExitCode: null,
            Duration: null));

        await controller.HandleEnterAsync("git status");

        Assert.Single(historyStore.Entries);
        Assert.Equal(CommandCaptureSource.Heuristic, historyStore.Entries[0].Source);
    }

    [Fact]
    public async Task HandleShellIntegrationEventAsync_WhenCommandFinished_UpdatesExitCodeAndDuration()
    {
        var historyStore = new InMemoryHistoryStore();
        var controller = CreateController(historyStore);
        controller.SetShellIntegrationEnabled(true);

        await controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.CommandAccepted,
            Timestamp: DateTimeOffset.Parse("2026-03-09T12:00:00+00:00"),
            CommandText: "git status",
            WorkingDirectory: @"C:\repo",
            ExitCode: null,
            Duration: null));
        await controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.CommandFinished,
            Timestamp: DateTimeOffset.Parse("2026-03-09T12:00:03+00:00"),
            CommandText: null,
            WorkingDirectory: @"C:\repo",
            ExitCode: 7,
            Duration: TimeSpan.FromSeconds(3)));

        Assert.Single(historyStore.Entries);
        Assert.Equal(7, historyStore.Entries[0].ExitCode);
        Assert.Equal(3000, historyStore.Entries[0].DurationMs);
    }

    [Fact]
    public async Task HandleShellIntegrationEventAsync_WhenCommandAcceptedIsMultiline_PersistsSingleHistoryEntry()
    {
        var historyStore = new InMemoryHistoryStore();
        var controller = CreateController(historyStore);
        controller.SetShellIntegrationEnabled(true);

        await controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.CommandAccepted,
            Timestamp: DateTimeOffset.Parse("2026-03-09T12:00:00+00:00"),
            CommandText: "foreach ($i in 1..3)\r\n    Write-Output $i\r\n}",
            WorkingDirectory: @"C:\repo",
            ExitCode: null,
            Duration: null));
        await controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.CommandFinished,
            Timestamp: DateTimeOffset.Parse("2026-03-09T12:00:03+00:00"),
            CommandText: null,
            WorkingDirectory: @"C:\repo",
            ExitCode: 0,
            Duration: TimeSpan.FromSeconds(3)));

        Assert.Single(historyStore.Entries);
        Assert.Equal("foreach ($i in 1..3)\r\n    Write-Output $i\r\n}", historyStore.Entries[0].CommandText);
        Assert.Equal(0, historyStore.Entries[0].ExitCode);
        Assert.Equal(3000, historyStore.Entries[0].DurationMs);
    }

    [Fact]
    public async Task HandleShellIntegrationEventAsync_WhenFinishedWithoutAcceptedCommand_DoesNotPatchHistory()
    {
        var historyStore = new InMemoryHistoryStore();
        var controller = CreateController(historyStore);
        controller.SetShellIntegrationEnabled(true);

        await controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.CommandFinished,
            Timestamp: DateTimeOffset.Parse("2026-03-09T12:00:03+00:00"),
            CommandText: null,
            WorkingDirectory: @"C:\repo",
            ExitCode: 1,
            Duration: TimeSpan.FromMilliseconds(500)));

        Assert.Empty(historyStore.Entries);
    }

    [Fact]
    public async Task HandleShellIntegrationEventAsync_WhenAcceptedMatchesPendingHeuristic_DoesNotCreateDuplicateEntry()
    {
        var historyStore = new InMemoryHistoryStore();
        var controller = CreateController(historyStore);
        controller.SetShellIntegrationEnabled(true);

        await controller.HandleEnterAsync("git status");
        await controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.CommandAccepted,
            Timestamp: DateTimeOffset.Parse("2026-03-09T12:00:00+00:00"),
            CommandText: "git status",
            WorkingDirectory: @"C:\repo",
            ExitCode: null,
            Duration: null));
        await controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.CommandFinished,
            Timestamp: DateTimeOffset.Parse("2026-03-09T12:00:01+00:00"),
            CommandText: null,
            WorkingDirectory: @"C:\repo",
            ExitCode: 0,
            Duration: TimeSpan.FromSeconds(1)));

        Assert.Single(historyStore.Entries);
        Assert.Equal(CommandCaptureSource.Heuristic, historyStore.Entries[0].Source);
        Assert.Equal(0, historyStore.Entries[0].ExitCode);
        Assert.Equal(1000, historyStore.Entries[0].DurationMs);
    }

    [Fact]
    public async Task HandleShellIntegrationEventAsync_ForZshSessionContext_TagsHistoryEntryWithZshShellKind()
    {
        var historyStore = new InMemoryHistoryStore();
        var controller = CreateController(historyStore);
        controller.SetShellIntegrationEnabled(true);
        controller.UpdateSessionContext(
            shellKind: "zsh",
            workingDirectory: "/repo",
            profileId: "profile-zsh",
            sessionId: "session-zsh",
            hostId: null,
            isRemote: false,
            isShellIntegrated: true);

        await controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.CommandAccepted,
            Timestamp: DateTimeOffset.Parse("2026-03-09T12:00:00+00:00"),
            CommandText: "git status",
            WorkingDirectory: "/repo",
            ExitCode: null,
            Duration: null));
        await controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.CommandFinished,
            Timestamp: DateTimeOffset.Parse("2026-03-09T12:00:01+00:00"),
            CommandText: null,
            WorkingDirectory: "/repo",
            ExitCode: 0,
            Duration: TimeSpan.FromSeconds(1)));

        Assert.Single(historyStore.Entries);
        Assert.Equal("zsh", historyStore.Entries[0].ShellKind);
        Assert.Equal(CommandCaptureSource.ShellIntegration, historyStore.Entries[0].Source);
        Assert.Equal(0, historyStore.Entries[0].ExitCode);
        Assert.Equal(1000, historyStore.Entries[0].DurationMs);
    }

    [Fact]
    public async Task HandleShellIntegrationEventAsync_ForBashSessionContext_TagsHistoryEntryWithBashShellKind()
    {
        var historyStore = new InMemoryHistoryStore();
        var controller = CreateController(historyStore);
        controller.SetShellIntegrationEnabled(true);
        controller.UpdateSessionContext(
            shellKind: "bash",
            workingDirectory: "/repo",
            profileId: "profile-bash",
            sessionId: "session-bash",
            hostId: null,
            isRemote: false,
            isShellIntegrated: true);

        await controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.CommandAccepted,
            Timestamp: DateTimeOffset.Parse("2026-03-09T12:00:00+00:00"),
            CommandText: "ls -la",
            WorkingDirectory: "/repo",
            ExitCode: null,
            Duration: null));

        Assert.Single(historyStore.Entries);
        Assert.Equal("bash", historyStore.Entries[0].ShellKind);
        Assert.Equal(CommandCaptureSource.ShellIntegration, historyStore.Entries[0].Source);
    }

    [Fact]
    public async Task UpdateSessionContext_WhenCommandAcceptedMarkerWasObserved_KeepsStructuredCaptureActive()
    {
        var historyStore = new InMemoryHistoryStore();
        var controller = CreateController(historyStore);
        controller.SetShellIntegrationEnabled(true);
        await controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.CommandAccepted,
            Timestamp: DateTimeOffset.Parse("2026-03-09T11:59:59+00:00"),
            CommandText: "git status",
            WorkingDirectory: @"C:\repo-a",
            ExitCode: null,
            Duration: null));

        controller.UpdateSessionContext(
            shellKind: "pwsh",
            workingDirectory: @"C:\repo-b",
            profileId: "profile-1",
            sessionId: "session-1",
            hostId: null,
            isRemote: false,
            isShellIntegrated: true);

        await controller.HandleEnterAsync("git status");

        Assert.Single(historyStore.Entries);
        Assert.Equal(CommandCaptureSource.ShellIntegration, historyStore.Entries[0].Source);
    }

    [Fact]
    public async Task Typing_WhenPinnedSnippetMatches_ShowsSnippetAsTopSuggestion()
    {
        var historyStore = new InMemoryHistoryStore();
        historyStore.Seed(CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        var snippetStore = new InMemorySnippetStore();
        snippetStore.Seed(new CommandSnippet(
            Id: "snippet-1",
            Name: "Git Status",
            CommandText: "git status",
            Description: null,
            ShellKind: "pwsh",
            WorkingDirectory: @"C:\repo",
            IsPinned: true,
            CreatedAt: DateTimeOffset.Parse("2026-03-01T09:00:00+00:00"),
            LastUsedAt: DateTimeOffset.Parse("2026-03-01T09:30:00+00:00")));

        var grid = new FakeGrid();
        var controller = CreateController(historyStore, snippetStore, new CommandAssistSuggestionEngine(), grid: grid);
        controller.ToggleAssist();
        grid.SetLine("git st");
        controller.NotifyInputActivity();

        await historyStore.WaitForSearchSettledAsync();
        await snippetStore.WaitForReadAsync();
        await WaitForConditionAsync(() => controller.ViewModel.TopSuggestionText == "Git Status" &&
                                          controller.Suggestions.Count > 0 &&
                                          controller.Suggestions[0].Type == AssistSuggestionType.Snippet);

        Assert.Equal("Git Status", controller.ViewModel.TopSuggestionText);
        Assert.Equal(AssistSuggestionType.Snippet, controller.Suggestions[0].Type);
    }

    [Fact]
    public async Task TogglePinSelectionAsync_WhenHistorySuggestionSelected_CreatesPinnedSnippet()
    {
        var historyStore = new InMemoryHistoryStore();
        historyStore.Seed(CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        var snippetStore = new InMemorySnippetStore();
        var grid = new FakeGrid();
        var controller = CreateController(historyStore, snippetStore, new CommandAssistSuggestionEngine(), grid: grid);
        controller.ToggleAssist();
        grid.SetLine("git st");
        controller.NotifyInputActivity();

        await historyStore.WaitForSearchSettledAsync();
        await snippetStore.WaitForReadAsync();
        await WaitForConditionAsync(() => controller.ViewModel.TopSuggestionText == "git status" &&
                                          controller.Suggestions.Count > 0);

        Assert.Equal("git status", controller.ViewModel.TopSuggestionText);
        Assert.Equal(AssistSuggestionType.History, controller.Suggestions[0].Type);

        bool toggled = await controller.TogglePinSelectionAsync();
        IReadOnlyList<CommandSnippet> snippets = await snippetStore.GetAllAsync();

        Assert.True(toggled);
        Assert.Single(snippets);
        Assert.True(snippets[0].IsPinned);
        Assert.Equal("git status", snippets[0].CommandText);
    }

    [Fact]
    public void CanTogglePinSelection_WhenNoSuggestionIsSelected_ReturnsFalse()
    {
        var controller = CreateController(
            historyStore: new InMemoryHistoryStore(),
            snippetStore: new InMemorySnippetStore(),
            suggestionEngine: new CommandAssistSuggestionEngine());
        controller.ToggleAssist();

        bool canToggle = controller.CanTogglePinSelection();

        Assert.False(canToggle);
    }

    [Fact]
    public async Task TogglePinSelectionAsync_WhenPinnedSnippetSelected_UnpinsSnippet()
    {
        var historyStore = new InMemoryHistoryStore();
        historyStore.Seed(CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        var snippetStore = new InMemorySnippetStore();
        snippetStore.Seed(new CommandSnippet(
            Id: "snippet-1",
            Name: "Git Status",
            CommandText: "git status",
            Description: null,
            ShellKind: "pwsh",
            WorkingDirectory: @"C:\repo",
            IsPinned: true,
            CreatedAt: DateTimeOffset.Parse("2026-03-01T09:00:00+00:00"),
            LastUsedAt: DateTimeOffset.Parse("2026-03-01T09:30:00+00:00")));

        var grid = new FakeGrid();
        var controller = CreateController(historyStore, snippetStore, new CommandAssistSuggestionEngine(), grid: grid);
        controller.ToggleAssist();
        grid.SetLine("git st");
        controller.NotifyInputActivity();
        await historyStore.WaitForSearchSettledAsync();
        await snippetStore.WaitForReadAsync();
        await WaitForConditionAsync(() => controller.ViewModel.TopSuggestionText == "Git Status" &&
                                          controller.Suggestions.Count > 0);

        bool toggled = await controller.TogglePinSelectionAsync();
        IReadOnlyList<CommandSnippet> snippets = await snippetStore.GetAllAsync();

        Assert.True(toggled);
        Assert.Single(snippets);
        Assert.False(snippets[0].IsPinned);
    }

    private static CommandAssistController CreateController(
        IHistoryStore? historyStore = null,
        ISnippetStore? snippetStore = null,
        ISuggestionEngine? suggestionEngine = null,
        ICommandDocsProvider? commandDocsProvider = null,
        IRecipeProvider? recipeProvider = null,
        IErrorInsightService? errorInsightService = null,
        FakeGrid? grid = null,
        Func<bool>? renderedSurfaceProbe = null)
    {
        historyStore ??= new InMemoryHistoryStore();
        var filter = new SecretsFilter();

        // Phase 0b deleted the test-only HistorySuggestionEngine that used to be the default here.
        // Its stand-in is the production engine with path suggestions stubbed out, which is what
        // the old engine effectively was: history-only ranking. Tests that care about path rows
        // still pass a real CommandAssistSuggestionEngine explicitly.
        var engine = suggestionEngine ?? new CommandAssistSuggestionEngine(new NoPathSuggestionProvider());

        var controller = new CommandAssistController(
            historyStore,
            filter,
            engine,
            snippetStore,
            commandDocsProvider,
            recipeProvider,
            errorInsightService,
            modeRouter: null,
            resultBuilder: null,
            queryProvider: grid == null ? null : grid.Read,
            renderedSurfaceProbe: renderedSurfaceProbe);

        if (grid != null)
        {
            // A controller handed a grid is standing in for an instrumented session sitting at its
            // prompt, so open the lifecycle gate once here rather than in twenty tests. The gate's
            // own behavior - that it opens on B, closes on C and D, and that a closed gate means no
            // query no matter what the grid says - is what CommandAssistGridTruthTests is for.
            grid.OpenPrompt(controller);
        }

        return controller;
    }

    /// <summary>
    /// Stands in for the terminal grid behind the App's provider seam.
    /// </summary>
    /// <remarks>
    /// Tests set the whole line rather than appending to it, which is the point of the Phase 1c
    /// model: the grid does not know or care whether the line got there by typing, by
    /// <c>Ctrl+U</c> then retyping, by an Up-arrow history recall or by the shell's own Tab
    /// completion. Every one of those is "the line is now this".
    /// </remarks>
    private sealed class FakeGrid
    {
        // Locked rather than volatile because the real provider is read from the refresh pass's
        // worker thread, not from the thread that set the line.
        private readonly object _gate = new();
        private AssistQuerySnapshot? _snapshot;

        public AssistQuerySnapshot? Read()
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }

        public void SetLine(
            string text,
            int? cursorOffset = null,
            bool isMultiline = false,
            bool rightPromptTrimmed = false)
        {
            lock (_gate)
            {
                _snapshot = new AssistQuerySnapshot(text, cursorOffset ?? text.Length, isMultiline, rightPromptTrimmed);
            }
        }

        /// <summary>The mark went away: scrollback reset, aged out, or the session ended.</summary>
        public void GoDark()
        {
            lock (_gate)
            {
                _snapshot = null;
            }
        }

        /// <summary><c>OSC 133;B</c> - the prompt finished printing and the line editor is live.</summary>
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

    private static CommandFailureContext CreateFailureContext(string commandText, int? exitCode, string? errorOutput)
    {
        return new CommandFailureContext(
            CommandText: commandText,
            ExitCode: exitCode,
            ShellKind: "pwsh",
            WorkingDirectory: @"C:\repo",
            ErrorOutput: errorOutput,
            IsRemote: false,
            SelectedText: null);
    }

    private static CommandHistoryEntry CreateEntry(string commandText, DateTimeOffset? executedAt = null)
    {
        return new CommandHistoryEntry(
            Id: Guid.NewGuid().ToString("N"),
            CommandText: commandText,
            ExecutedAt: executedAt ?? DateTimeOffset.UtcNow,
            ShellKind: "pwsh",
            WorkingDirectory: @"C:\repo",
            ProfileId: "profile-1",
            SessionId: "session-1",
            HostId: null,
            ExitCode: 0,
            IsRemote: false,
            IsRedacted: false,
            Source: CommandCaptureSource.Heuristic,
            DurationMs: null);
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate, int timeoutMs = 1000)
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

    /// <summary>
    /// A controller whose passive typing bubble actually has rows in it.
    /// </summary>
    /// <remarks>
    /// A passive Suggest session is scoped to <em>paths only</em> - unasked-for history rows were the
    /// noisiest part of V1, see <c>SuggestionOrchestrator.ResolveScope</c> - so seeding history is not
    /// enough to make one visible, and every history-seeded test in this file that calls
    /// <c>ToggleAssist</c> first is in the <em>explicit</em> bubble state instead. The PR #290 review's
    /// <c>Up</c> rule is specifically about the passive state, so it needs a path provider that answers.
    /// </remarks>
    private static CommandAssistController CreatePassiveBubbleController(FakeGrid grid)
    {
        return CreateController(
            suggestionEngine: new CommandAssistSuggestionEngine(new FixedPathSuggestionProvider(
                CreatePathRow("./docs/"),
                CreatePathRow("./deploy.sh"))),
            grid: grid);
    }

    /// <summary>
    /// Waits for a passive bubble's ranking pass to be <em>finished</em>, not merely to have produced
    /// rows.
    /// </summary>
    /// <remarks>
    /// The test dispatch is the identity function, so <c>ApplyRefreshOutcome</c> runs on the pass's own
    /// thread-pool thread while the test thread carries on. Waiting on the row count therefore returns
    /// mid-apply, and the writes that come after it - closing the popup, publishing visibility - land on
    /// top of whatever the test did next. <c>IsVisible</c> is the last of those writes, and it is false
    /// until a pass lands (<c>NotifyInputActivity</c> sets it from the rows that were already up, which
    /// is none), so it is the one flag that means "the pass is done".
    /// </remarks>
    private static Task WaitForPassiveBubbleAsync(CommandAssistController controller) =>
        WaitForConditionAsync(() => controller.ViewModel.IsVisible && controller.Suggestions.Count > 1);

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

    /// <summary>Stubs out the filesystem so controller tests never depend on the working directory.</summary>
    private sealed class NoPathSuggestionProvider : IPathSuggestionProvider
    {
        public IReadOnlyList<AssistSuggestion> GetSuggestions(CommandAssistQueryContext context, int maxResults)
            => Array.Empty<AssistSuggestion>();
    }

    /// <summary>The opposite: a filesystem that always has these two entries in it.</summary>
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

    private sealed class InMemoryHistoryStore : IHistoryStore
    {
        private readonly List<CommandHistoryEntry> _entries = new();
        private int _searchCount;

        public IReadOnlyList<CommandHistoryEntry> Entries => _entries;

        /// <summary>
        /// How many times the recall gate was queried. Lets a test assert that a refresh never
        /// reached for history at all, which is the observable difference between the history-backed
        /// scopes and the passive path-only one.
        /// </summary>
        public int SearchCount => Volatile.Read(ref _searchCount);

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

        /// <summary>
        /// Implements the documented <see cref="IHistoryStore"/> recall gate rather than an
        /// ad-hoc one: case-insensitive subsequence match, most recent first, no scoring.
        /// </summary>
        /// <remarks>
        /// A <c>Contains</c> filter is both narrower (it rejects the non-contiguous matches the
        /// real store admits) and unordered, so controller tests written against it would exercise
        /// gate semantics production never has. The point of these tests is the controller's
        /// behavior over a real candidate set.
        /// </remarks>
        public Task<IReadOnlyList<CommandHistoryEntry>> SearchAsync(string query, int maxCandidates, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _searchCount);
            string normalized = query.Trim();
            IReadOnlyList<CommandHistoryEntry> results = _entries
                .Where(x => IsCandidate(x.CommandText, normalized))
                .OrderByDescending(x => x.ExecutedAt)
                .Take(Math.Max(0, maxCandidates))
                .ToList();
            return Task.FromResult(results);
        }

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
        {
            int index = _entries.FindIndex(x => x.Id == entryId);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            _entries[index] = _entries[index] with
            {
                ExitCode = exitCode,
                DurationMs = durationMs ?? _entries[index].DurationMs
            };
            return Task.FromResult(true);
        }

        public void Seed(params CommandHistoryEntry[] entries)
        {
            _entries.AddRange(entries);
        }

        public Task WaitForSearchSettledAsync() => Task.Delay(50);
    }

    private sealed class DelayedHistoryStore : IHistoryStore
    {
        private readonly TimeSpan _delay;
        private readonly IReadOnlyList<CommandHistoryEntry> _results;
        private Task _lastSearchTask = Task.CompletedTask;

        public DelayedHistoryStore(TimeSpan delay, params CommandHistoryEntry[] results)
        {
            _delay = delay;
            _results = results;
        }

        public Task AppendAsync(CommandHistoryEntry entry, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ClearAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<CommandHistoryEntry>> GetRecentAsync(int maxResults, CancellationToken cancellationToken = default)
        {
            _lastSearchTask = Task.Delay(_delay, cancellationToken);
            return _lastSearchTask.ContinueWith(
                _ => _results.Take(maxResults).ToList() as IReadOnlyList<CommandHistoryEntry>,
                cancellationToken);
        }

        public Task<IReadOnlyList<CommandHistoryEntry>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default)
        {
            _lastSearchTask = Task.Delay(_delay, cancellationToken);
            return _lastSearchTask.ContinueWith(
                _ => _results.Take(maxResults).ToList() as IReadOnlyList<CommandHistoryEntry>,
                cancellationToken);
        }

        public Task<bool> TryUpdateExecutionResultAsync(string entryId, int? exitCode, long? durationMs, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task WaitForLastSearchAsync() => _lastSearchTask;
    }

    private sealed class ThrowingHistoryStore : IHistoryStore
    {
        public Task AppendAsync(CommandHistoryEntry entry, CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("simulated write failure"));

        public Task ClearAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<CommandHistoryEntry>> GetRecentAsync(int maxResults, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommandHistoryEntry>>(Array.Empty<CommandHistoryEntry>());

        public Task<IReadOnlyList<CommandHistoryEntry>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommandHistoryEntry>>(Array.Empty<CommandHistoryEntry>());

        public Task<bool> TryUpdateExecutionResultAsync(string entryId, int? exitCode, long? durationMs, CancellationToken cancellationToken = default)
            => Task.FromException<bool>(new InvalidOperationException("simulated write failure"));
    }

    private sealed class DelayedSuggestionEngine : ISuggestionEngine
    {
        private readonly TimeSpan _delay;
        private readonly IReadOnlyList<AssistSuggestion> _suggestions;
        private TaskCompletionSource<bool> _lastCallTcs = CreateCompletionSource();

        public DelayedSuggestionEngine(TimeSpan delay, IReadOnlyList<AssistSuggestion> suggestions)
        {
            _delay = delay;
            _suggestions = suggestions;
        }

        public IReadOnlyList<AssistSuggestion> GetSuggestions(
            IReadOnlyList<CommandHistoryEntry> entries,
            CommandAssistQueryContext context,
            int maxResults)
        {
            return GetSuggestions(entries, Array.Empty<CommandSnippet>(), context, maxResults);
        }

        public IReadOnlyList<AssistSuggestion> GetSuggestions(
            IReadOnlyList<CommandHistoryEntry> entries,
            IReadOnlyList<CommandSnippet> snippets,
            CommandAssistQueryContext context,
            int maxResults)
        {
            TaskCompletionSource<bool> currentCallTcs = CreateCompletionSource();
            TaskCompletionSource<bool> previousCallTcs = Interlocked.Exchange(ref _lastCallTcs, currentCallTcs);
            previousCallTcs.TrySetCanceled();

            try
            {
                Thread.Sleep(_delay);
                return _suggestions.Take(maxResults).ToArray();
            }
            finally
            {
                currentCallTcs.TrySetResult(true);
            }
        }

        private static TaskCompletionSource<bool> CreateCompletionSource()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class InMemorySnippetStore : ISnippetStore
    {
        private readonly List<CommandSnippet> _snippets = new();
        private Task _lastReadTask = Task.CompletedTask;

        public Task<IReadOnlyList<CommandSnippet>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            _lastReadTask = Task.CompletedTask;
            return Task.FromResult<IReadOnlyList<CommandSnippet>>(_snippets.ToList());
        }

        public Task UpsertAsync(CommandSnippet snippet, CancellationToken cancellationToken = default)
        {
            int index = _snippets.FindIndex(x => x.Id == snippet.Id);
            if (index >= 0)
            {
                _snippets[index] = snippet;
            }
            else
            {
                _snippets.Add(snippet);
            }

            return Task.CompletedTask;
        }

        public Task RemoveAsync(string snippetId, CancellationToken cancellationToken = default)
        {
            _snippets.RemoveAll(x => x.Id == snippetId);
            return Task.CompletedTask;
        }

        public void Seed(params CommandSnippet[] snippets)
        {
            _snippets.AddRange(snippets);
        }

        public Task WaitForReadAsync() => _lastReadTask;
    }

    private sealed class RecordingDocsProvider : ICommandDocsProvider
    {
        private readonly IReadOnlyList<CommandHelpItem> _results;

        public RecordingDocsProvider(IReadOnlyList<CommandHelpItem> results)
        {
            _results = results;
        }

        public CommandHelpQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<CommandHelpItem>> GetHelpAsync(CommandHelpQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(_results);
        }
    }

    private sealed class RecordingRecipeProvider : IRecipeProvider
    {
        private readonly IReadOnlyList<CommandHelpItem> _results;

        public RecordingRecipeProvider(IReadOnlyList<CommandHelpItem> results)
        {
            _results = results;
        }

        public Task<IReadOnlyList<CommandHelpItem>> GetRecipesAsync(CommandHelpQuery query, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_results);
        }
    }

    private sealed class RecordingErrorInsightService : IErrorInsightService
    {
        private readonly IReadOnlyList<CommandFixSuggestion> _results;

        public RecordingErrorInsightService(IReadOnlyList<CommandFixSuggestion> results)
        {
            _results = results;
        }

        public Task<IReadOnlyList<CommandFixSuggestion>> AnalyzeAsync(CommandFailureContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_results);
        }
    }
}

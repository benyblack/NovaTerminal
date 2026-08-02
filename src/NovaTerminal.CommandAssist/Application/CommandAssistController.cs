using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using NovaTerminal.CommandAssist.ViewModels;

namespace NovaTerminal.CommandAssist.Application;

/// <summary>
/// The facade the terminal pane talks to. It owns the assist view-model and the selected-row list,
/// and delegates everything else to three collaborators:
/// <see cref="AssistSessionStateMachine"/> (what the session is doing),
/// <see cref="CapturePipeline"/> (turning commands into history) and
/// <see cref="SuggestionOrchestrator"/> (producing ranked rows).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AssistSessionContext"/> carries the environment all three share. The controller keeps
/// the view-model writes because presentation is the facade's job: the state machine says the
/// session is in an explicit Suggest session, the controller decides that means the bubble is up and
/// the popup is not.
/// </para>
/// <para>
/// <strong>Query state (Phase 1c).</strong> The controller does not hold a query. It has no
/// <c>HandleTextInput(text)</c>, no backspace mirror and no paste mirror, because it has no buffer
/// for them to write into. The question "what is on the command line" is answered by reading the
/// terminal grid between the newest <c>OSC 133;B</c> mark and the cursor - see
/// <see cref="AssistQuerySnapshot"/> - and every consumer of the query (ranking, the help token, the
/// insertion planner, the view-model's <c>QueryText</c>) goes to that one source. Keystrokes remain
/// as <em>triggers</em>: <see cref="NotifyInputActivity"/> says "the line probably changed, look
/// again", and the looking is the orchestrator's job.
/// </para>
/// <para>
/// What survives from the old shadow buffer is exactly one bit:
/// <see cref="AssistSessionStateMachine.IsCurrentSubmissionSuppressed"/>. Paste suppression is a
/// statement about provenance ("the user did not compose this here"), which the grid cannot
/// reconstruct from pixels, and it gates history capture and insertion rather than the query.
/// </para>
/// </remarks>
public sealed class CommandAssistController
{
    private readonly AssistSessionStateMachine _state = new();
    private readonly AssistSessionContext _context = new();
    private readonly CapturePipeline _capturePipeline;
    private readonly SuggestionOrchestrator _suggestionOrchestrator;
    private readonly List<AssistSuggestion> _suggestions = new();
    private readonly Action<Action> _dispatch;
    private readonly ICommandDocsProvider _commandDocsProvider;
    private readonly IRecipeProvider _recipeProvider;
    private readonly IErrorInsightService _errorInsightService;
    private readonly CommandAssistModeRouter _modeRouter;
    private readonly CommandAssistResultBuilder _resultBuilder;

    public CommandAssistController(
        IHistoryStore historyStore,
        ISecretsFilter secretsFilter,
        ISuggestionEngine suggestionEngine,
        ISnippetStore? snippetStore,
        ICommandDocsProvider? commandDocsProvider,
        IRecipeProvider? recipeProvider,
        IErrorInsightService? errorInsightService,
        CommandAssistModeRouter? modeRouter,
        CommandAssistResultBuilder? resultBuilder,
        Func<AssistQuerySnapshot?>? queryProvider = null,
        Action<Action>? dispatch = null)
    {
        HistoryStore = historyStore;
        SecretsFilter = secretsFilter;
        SuggestionEngine = suggestionEngine;
        SnippetStore = snippetStore;
        _commandDocsProvider = commandDocsProvider ?? new EmptyCommandDocsProvider();
        _recipeProvider = recipeProvider ?? new EmptyRecipeProvider();
        _errorInsightService = errorInsightService ?? new EmptyErrorInsightService();
        _modeRouter = modeRouter ?? new CommandAssistModeRouter();
        _resultBuilder = resultBuilder ?? new CommandAssistResultBuilder();
        ViewModel = new CommandAssistBarViewModel();
        _dispatch = dispatch ?? (action => action());
        _capturePipeline = new CapturePipeline(historyStore, secretsFilter, _context);
        _suggestionOrchestrator = new SuggestionOrchestrator(
            historyStore,
            snippetStore,
            suggestionEngine,
            _context,
            // No provider means no grid, which is the same situation as a shell that emits no
            // marks: the session runs in degraded mode. Defaulting to "no truth available" rather
            // than throwing keeps every non-App host (tests, the MCP surface) on the honest path.
            queryProvider ?? (() => null),
            _dispatch,
            ApplyRefreshOutcome);
    }

    public IHistoryStore HistoryStore { get; }
    public ISnippetStore? SnippetStore { get; }
    public ISecretsFilter SecretsFilter { get; }
    public ISuggestionEngine SuggestionEngine { get; }
    public CommandAssistBarViewModel ViewModel { get; }
    public IReadOnlyList<AssistSuggestion> Suggestions => _suggestions;

    /// <summary>What the session is doing right now. Exposed for diagnostics and tests.</summary>
    internal AssistSessionState SessionState => _state.State;

    public void ToggleAssist()
    {
        if (_context.IsAltScreenActive)
        {
            ViewModel.IsVisible = false;
            return;
        }

        bool nextVisible = _state.ToggleSession(ViewModel.IsVisible) != AssistSessionState.Hidden;
        ViewModel.ModeLabel = "Suggest";
        ViewModel.IsPopupOpen = false;
        ViewModel.IsVisible = nextVisible;
        if (nextVisible)
        {
            QueueRefreshSuggestions();
        }
    }

    public bool OpenHistorySearch()
    {
        if (_context.IsAltScreenActive)
        {
            ViewModel.IsVisible = false;
            return false;
        }

        _state.OpenSearch();
        ViewModel.ModeLabel = "History";
        ViewModel.IsPopupOpen = true;
        ViewModel.IsVisible = true;
        QueueRefreshSuggestions();
        return true;
    }

    public bool OpenHelp()
    {
        if (_context.IsAltScreenActive)
        {
            ViewModel.IsVisible = false;
            return false;
        }

        _ = OpenHelpAsync();
        return true;
    }

    /// <summary>
    /// The user did something at the prompt that probably changed the command line - typed a
    /// character, pressed Backspace. Triggers a refresh; carries no text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole of what a keystroke means to Command Assist now. The character itself is
    /// not interesting: it is already on the screen, and so is everything the shell did that no
    /// keystroke describes - the <c>Ctrl+U</c> that emptied the line, the Up-arrow that recalled a
    /// command, the Tab the shell completed. V1 mirrored the four events it could see and was wrong
    /// about the line the moment any of the others happened.
    /// </para>
    /// <para>
    /// Backspace deliberately has no separate entry point and no "is there anything to delete"
    /// guard. Whether the line got shorter is the grid's business.
    /// </para>
    /// </remarks>
    public void NotifyInputActivity()
    {
        if (_context.IsAltScreenActive)
        {
            return;
        }

        _state.ObserveTypedInput();
        ViewModel.ModeLabel = "Suggest";
        ViewModel.IsPopupOpen = false;

        // Visibility follows the rows that are already up, not the rows this refresh will produce.
        // Setting it true here and false when the pass lands is the flash that #232's predecessor
        // shipped; the pass sets it for real in ApplyRefreshOutcome.
        ViewModel.IsVisible = ViewModel.HasSuggestions;
        QueueRefreshSuggestions();
    }

    /// <summary>
    /// The user pasted into the terminal. Suppresses the current submission and triggers a refresh;
    /// like <see cref="NotifyInputActivity"/> it carries no text.
    /// </summary>
    /// <remarks>
    /// Suppression is the one piece of the old paste handling that had to survive the shadow
    /// buffer's deletion, and it is not a query fact: it says the text on the line was not composed
    /// here, which stops it being written to history as though it were and stops a suggestion being
    /// spliced into it. The grid can read pasted text perfectly well; what it cannot see is where
    /// the text came from.
    /// </remarks>
    public void NotifyPastedInput()
    {
        _state.ObservePastedText();
        ViewModel.ModeLabel = "Suggest";
        ViewModel.IsPopupOpen = false;
        ViewModel.IsVisible = !_context.IsAltScreenActive && ViewModel.HasSuggestions;
        QueueRefreshSuggestions();
    }

    /// <summary>
    /// The user pressed Enter. Runs heuristic history capture over <paramref name="submittedText"/>
    /// and resets the session for the next command line.
    /// </summary>
    /// <param name="submittedText">
    /// What the host believes was submitted, read from the grid at the moment Enter was observed, or
    /// <see langword="null"/> when there was nothing truthful to read. Passed in rather than pulled
    /// from a snapshot here because the host owns the timing: Enter reaches the PTY before this runs,
    /// and the closer the read sits to the keypress the smaller the window in which the shell has
    /// already begun repainting.
    /// </param>
    /// <remarks>
    /// <para>
    /// A markless session passes <see langword="null"/> and captures nothing. That is a deliberate
    /// loss and the alternative was worse: V1's Enter-time capture read the shadow buffer, so any
    /// line the user had edited with the keys the mirror could not see was written to persistent
    /// history <em>wrong</em>. Recording no command is recoverable; recording a command the user
    /// never ran is not. Instrumented sessions are unaffected - the first command is captured from
    /// the grid here, and every command after it from the <c>OSC 133;C</c> payload, which is what
    /// <see cref="AssistSessionContext.IsStructuredCaptureActive"/> stands the heuristic down for.
    /// </para>
    /// </remarks>
    public async Task HandleEnterAsync(string? submittedText = null)
    {
        try
        {
            await _capturePipeline.CaptureSubmissionAsync(
                submittedText ?? string.Empty,
                _state.IsCurrentSubmissionSuppressed);
        }
        catch
        {
            // Capture is best-effort and Enter must reach the shell regardless. CapturePipeline
            // already swallows its own failures, so this only backstops a future change to it.
        }
        finally
        {
            ResetSubmissionState();
        }
    }

    /// <summary>
    /// The live command line, or <see langword="null"/> when the session is markless, the shell is
    /// not in its line editor, or the grid cannot be read.
    /// </summary>
    /// <remarks>
    /// Read fresh on every call rather than cached from the last refresh. Callers use it to decide
    /// what to send to a live terminal, and the line may have moved since the rows on screen were
    /// ranked.
    /// </remarks>
    public AssistQuerySnapshot? TryReadQuerySnapshot() => _suggestionOrchestrator.TryReadQuery();

    public void UpdateSessionContext(
        string? shellKind,
        string? workingDirectory,
        string? profileId,
        string? sessionId,
        string? hostId,
        bool isRemote,
        bool isShellIntegrated = false)
    {
        _context.UpdateSession(
            shellKind,
            workingDirectory,
            profileId,
            sessionId,
            hostId,
            isRemote,
            isShellIntegrated);
    }

    public void SetShellIntegrationEnabled(bool isEnabled)
    {
        _context.SetShellIntegrationEnabled(isEnabled);
    }

    public void Dismiss()
    {
        _suggestionOrchestrator.CancelPending();
        _state.Dismiss();
        ViewModel.IsVisible = false;
        ViewModel.IsPopupOpen = false;
        ClearSuggestionSurface();
    }

    public bool HandleEscape()
    {
        if (!ViewModel.IsVisible)
        {
            return false;
        }

        Dismiss();
        return true;
    }

    public bool MoveSelectionDown()
    {
        if (_suggestions.Count == 0)
        {
            return false;
        }

        int nextIndex = ViewModel.SelectedIndex < 0
            ? 0
            : Math.Min(ViewModel.SelectedIndex + 1, _suggestions.Count - 1);

        return SetSelectedIndex(nextIndex);
    }

    public bool MoveSelectionUp()
    {
        if (_suggestions.Count == 0)
        {
            return false;
        }

        int nextIndex = ViewModel.SelectedIndex <= 0
            ? 0
            : ViewModel.SelectedIndex - 1;

        return SetSelectedIndex(nextIndex);
    }

    public bool TryGetInsertionText(out string? insertionText)
    {
        AssistSuggestion? selected = GetSelectedSuggestion();
        if (selected == null || _state.IsCurrentSubmissionSuppressed || _context.IsAltScreenActive)
        {
            insertionText = null;
            return false;
        }

        insertionText = selected.InsertText;
        return true;
    }

    public bool TryAcceptSelection(out string? insertionText)
    {
        if (!TryGetInsertionText(out insertionText) || string.IsNullOrWhiteSpace(insertionText))
        {
            return false;
        }

        // No query write here. Accepting a row asks the host to send a delta to the terminal; the
        // line's new contents are then whatever the shell paints, and the next refresh reads that.
        // Writing the accepted text into QueryText was V1 predicting the outcome of an edit it did
        // not perform, and it was wrong whenever the send failed or the shell transformed the input.
        _state.AcceptSelection();
        ViewModel.ModeLabel = "Suggest";
        ViewModel.IsPopupOpen = false;
        Dismiss();
        return true;
    }

    public bool CanTogglePinSelection()
    {
        AssistSuggestion? selected = GetSelectedSuggestion();
        return SnippetStore != null &&
               selected != null &&
               selected.Type is AssistSuggestionType.History or AssistSuggestionType.Snippet;
    }

    public async Task HandleCommandFinishedAsync(int? exitCode)
    {
        await _capturePipeline.CompleteSubmissionAsync(exitCode);
    }

    public async Task<bool> OpenHelpAsync(string? queryText = null, string? selectedText = null)
    {
        if (_context.IsAltScreenActive)
        {
            ViewModel.IsVisible = false;
            return false;
        }

        // Falls back to grid truth, not to the view-model. The view-model holds whatever the last
        // ranking pass wrote, which is a frame behind at best and stale help-token extraction at
        // worst; in a markless session there is no query and the recognized command comes from the
        // selection alone, which is the degraded-mode contract.
        string effectiveQuery = queryText ?? TryReadQuerySnapshot()?.Text ?? string.Empty;
        CommandAssistContextSnapshot snapshot = _context.CreateSnapshot(
            effectiveQuery,
            selectedText);
        var helpQuery = new CommandHelpQuery(
            RawInput: snapshot.QueryText,
            CommandToken: snapshot.RecognizedCommand,
            ShellKind: snapshot.ShellKind,
            WorkingDirectory: snapshot.WorkingDirectory,
            SelectedText: snapshot.SelectedText,
            SessionId: snapshot.SessionId);

        IReadOnlyList<CommandHelpItem> docs = await _commandDocsProvider.GetHelpAsync(helpQuery);
        IReadOnlyList<CommandHelpItem> recipes = await _recipeProvider.GetRecipesAsync(helpQuery);
        IReadOnlyList<AssistSuggestion> suggestions = _resultBuilder.BuildCombined(
            Array.Empty<AssistSuggestion>(),
            docs,
            recipes,
            Array.Empty<CommandFixSuggestion>());

        _dispatch(() => ApplyHelperSuggestions(
            _modeRouter.ChooseModeForHelpRequest(),
            effectiveQuery,
            suggestions,
            "No local help found.",
            openPopup: true));

        return true;
    }

    public async Task<bool> ExplainSelectionAsync(string? selectedText)
    {
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            return false;
        }

        return await OpenHelpAsync(selectedText: selectedText);
    }

    public async Task<bool> HandleCommandFailureAsync(CommandFailureContext context)
    {
        if (_context.IsAltScreenActive)
        {
            ViewModel.IsVisible = false;
            return false;
        }

        IReadOnlyList<CommandFixSuggestion> fixes = await _errorInsightService.AnalyzeAsync(context);
        double highestConfidence = fixes.Count == 0 ? 0 : fixes.Max(item => item.Confidence);
        CommandAssistMode mode = _modeRouter.ChooseModeForFailure(highestConfidence);
        IReadOnlyList<AssistSuggestion> suggestions = _resultBuilder.BuildFixSuggestions(fixes);

        if (mode == CommandAssistMode.Fix)
        {
            _dispatch(() => ApplyHelperSuggestions(
                CommandAssistMode.Fix,
                context.CommandText,
                suggestions,
                "No likely local fix found.",
                openPopup: true));
            return true;
        }

        if (suggestions.Count == 0)
        {
            return false;
        }

        _dispatch(() => ApplyHelperSuggestions(
            CommandAssistMode.Fix,
            context.CommandText,
            suggestions,
            "No likely local fix found.",
            openPopup: false));
        return false;
    }

    /// <summary>
    /// Consumes an OSC 133 event: moves the command-input lifecycle gate, then lets the capture
    /// pipeline do whatever the event means for history.
    /// </summary>
    /// <remarks>
    /// The gate moves here rather than inside <see cref="CapturePipeline"/> because it is not a
    /// capture concern - it is what makes grid-truth reading legal, and the pipeline would be the
    /// wrong place to look for it. <c>B</c> opens the window, <c>C</c> and <c>D</c> close it; see
    /// <see cref="AssistSessionContext.IsAcceptingCommandInput"/> for why both closers are needed
    /// and why nothing else opens it.
    /// </remarks>
    public async Task HandleShellIntegrationEventAsync(ShellIntegrationEvent shellEvent)
    {
        switch (shellEvent.Type)
        {
            case ShellIntegrationEventType.CommandStarted:
                _context.OpenCommandInputWindow();
                break;
            case ShellIntegrationEventType.CommandAccepted:
            case ShellIntegrationEventType.CommandFinished:
                _context.CloseCommandInputWindow();
                break;
        }

        await _capturePipeline.HandleShellIntegrationEventAsync(shellEvent);
    }

    public async Task<bool> TogglePinSelectionAsync()
    {
        if (!CanTogglePinSelection())
        {
            return false;
        }

        ISnippetStore snippetStore = SnippetStore!;
        AssistSuggestion? selected = GetSelectedSuggestion();
        if (selected == null)
        {
            return false;
        }

        try
        {
            IReadOnlyList<CommandSnippet> snippets = await snippetStore.GetAllAsync();
            CommandSnippet? existing = snippets.FirstOrDefault(x => x.Id == selected.Id) ??
                                       snippets.FirstOrDefault(x =>
                                           string.Equals(x.CommandText, selected.InsertText, StringComparison.OrdinalIgnoreCase) &&
                                           string.Equals(x.ShellKind ?? string.Empty, _context.ShellKind ?? string.Empty, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                await snippetStore.UpsertAsync(existing with
                {
                    IsPinned = !existing.IsPinned,
                    LastUsedAt = DateTimeOffset.UtcNow
                });
            }
            else
            {
                var snippet = new CommandSnippet(
                    Id: Guid.NewGuid().ToString("N"),
                    Name: selected.DisplayText,
                    CommandText: selected.InsertText,
                    Description: selected.Description,
                    ShellKind: _context.ShellKind,
                    WorkingDirectory: _context.WorkingDirectory,
                    IsPinned: true,
                    CreatedAt: DateTimeOffset.UtcNow,
                    LastUsedAt: selected.LastUsedAt);

                await snippetStore.UpsertAsync(snippet);
            }

            QueueRefreshSuggestions();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void HandleAltScreenChanged(bool isAltScreenActive)
    {
        _context.SetAltScreenActive(isAltScreenActive);
        if (!isAltScreenActive)
        {
            return;
        }

        _suggestionOrchestrator.CancelPending();
        _state.HideForAltScreen();
        ViewModel.IsVisible = false;
        ViewModel.IsPopupOpen = false;
        ClearSuggestionSurface();
    }

    private void QueueRefreshSuggestions()
    {
        if (!_state.AllowsSuggestionRefresh)
        {
            return;
        }

        _suggestionOrchestrator.Refresh(_state.Mode, _state.IsExplicitSession);
    }

    /// <summary>
    /// Applies a ranking pass on the dispatcher thread. Outcomes for a mode the session has already
    /// left are dropped: the orchestrator's cancellation covers superseded queries, this covers a
    /// pass that was in flight when Help or Fix took the surface over.
    /// </summary>
    private void ApplyRefreshOutcome(SuggestionRefreshOutcome outcome)
    {
        if (_state.Mode != outcome.RequestedMode)
        {
            return;
        }

        // The one place a Suggest/Search query reaches the view-model. The pass read it off the
        // grid, so this is also where the surface catches up with edits no keystroke reported.
        ViewModel.QueryText = outcome.Query;

        if (outcome.Faulted)
        {
            ClearSuggestionSurface();
        }
        else
        {
            _suggestions.Clear();
            _suggestions.AddRange(outcome.Suggestions);
            ViewModel.SelectedIndex = _suggestions.Count > 0 ? 0 : -1;
            ViewModel.EmptyStateText = string.Empty;
            ViewModel.ShowEmptyState = false;
            SyncSuggestionViewModel();
        }

        if (outcome.RequestedMode != CommandAssistMode.Suggest)
        {
            return;
        }

        _state.ClosePopupAfterRefresh();
        ViewModel.IsPopupOpen = false;
        ViewModel.IsVisible = _state.IsExplicitSession || _suggestions.Count > 0;
    }

    private void ResetSubmissionState()
    {
        _suggestionOrchestrator.CancelPending();
        _state.CompleteSubmission();
        ViewModel.QueryText = string.Empty;
        ViewModel.IsPopupOpen = false;
        ClearSuggestionSurface();
        ViewModel.IsVisible = false;
    }

    /// <summary>Empties every row-derived view-model field and the backing suggestion list.</summary>
    private void ClearSuggestionSurface()
    {
        ViewModel.TopSuggestionText = string.Empty;
        ViewModel.SelectedIndex = -1;
        ViewModel.SelectedBadgesText = string.Empty;
        ViewModel.SelectedMetadataText = string.Empty;
        ViewModel.SelectedDescriptionText = string.Empty;
        ViewModel.EmptyStateText = string.Empty;
        ViewModel.ShowEmptyState = false;
        ViewModel.HasSuggestions = false;
        ViewModel.Suggestions.Clear();
        _suggestions.Clear();
    }

    private bool SetSelectedIndex(int index)
    {
        if (index < 0 || index >= _suggestions.Count)
        {
            return false;
        }

        ViewModel.SelectedIndex = index;
        if (!ViewModel.IsPopupOpen)
        {
            _state.OpenPopupForSelection();
            ViewModel.IsPopupOpen = true;
        }

        SyncSuggestionViewModel();
        return true;
    }

    private AssistSuggestion? GetSelectedSuggestion()
    {
        return ViewModel.SelectedIndex >= 0 && ViewModel.SelectedIndex < _suggestions.Count
            ? _suggestions[ViewModel.SelectedIndex]
            : null;
    }

    private void SyncSuggestionViewModel()
    {
        AssistSuggestion? selected = GetSelectedSuggestion();
        ViewModel.TopSuggestionText = selected?.DisplayText ?? string.Empty;
        ViewModel.SelectedBadgesText = selected == null ? string.Empty : string.Join("  ", selected.Badges);
        ViewModel.SelectedMetadataText = selected == null ? string.Empty : BuildMetadataText(selected);
        ViewModel.SelectedDescriptionText = selected?.Description ?? string.Empty;
        ViewModel.HasSuggestions = _suggestions.Count > 0;
        ViewModel.Suggestions.Clear();

        for (int i = 0; i < _suggestions.Count; i++)
        {
            AssistSuggestion suggestion = _suggestions[i];
            ViewModel.Suggestions.Add(new CommandAssistSuggestionItemViewModel(
                SelectionGlyph: i == ViewModel.SelectedIndex ? ">" : " ",
                DisplayText: suggestion.DisplayText,
                DescriptionText: suggestion.Description ?? string.Empty,
                BadgesText: string.Join("  ", suggestion.Badges),
                MetadataText: BuildMetadataText(suggestion),
                IsSelected: i == ViewModel.SelectedIndex,
                Type: suggestion.Type));
        }
    }

    private static string BuildMetadataText(AssistSuggestion suggestion)
    {
        List<string> parts = new();
        if (!string.IsNullOrWhiteSpace(suggestion.WorkingDirectory))
        {
            parts.Add(suggestion.WorkingDirectory!);
        }

        if (suggestion.LastUsedAt.HasValue)
        {
            parts.Add($"Used {suggestion.LastUsedAt.Value:yyyy-MM-dd HH:mm}");
        }

        if (suggestion.ExitCode.HasValue)
        {
            parts.Add(suggestion.ExitCode.Value == 0 ? "Exit 0" : $"Exit {suggestion.ExitCode.Value}");
        }

        return string.Join("  |  ", parts);
    }

    private void ApplyHelperSuggestions(
        CommandAssistMode mode,
        string? queryText,
        IReadOnlyList<AssistSuggestion> suggestions,
        string emptyStateText,
        bool openPopup)
    {
        _suggestionOrchestrator.CancelPending();
        _state.EnterHelperMode(mode, openPopup);
        ViewModel.ModeLabel = mode.ToString();

        // Help and Fix put their subject in the query field: the command Help was asked about, the
        // command that failed. That is a caption for content the user asked for, not a claim about
        // what is on the command line - and Fix in particular is shown *after* the line was
        // submitted, when there is no command line to be truthful about.
        ViewModel.QueryText = queryText ?? string.Empty;
        ViewModel.IsPopupOpen = openPopup;
        ViewModel.IsVisible = true;
        ViewModel.EmptyStateText = suggestions.Count == 0 ? emptyStateText : string.Empty;
        ViewModel.ShowEmptyState = suggestions.Count == 0;

        _suggestions.Clear();
        _suggestions.AddRange(suggestions);
        ViewModel.SelectedIndex = suggestions.Count > 0 ? 0 : -1;
        SyncSuggestionViewModel();
    }

    private sealed class EmptyCommandDocsProvider : ICommandDocsProvider
    {
        public Task<IReadOnlyList<CommandHelpItem>> GetHelpAsync(CommandHelpQuery query, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<CommandHelpItem>>(Array.Empty<CommandHelpItem>());
        }
    }

    private sealed class EmptyRecipeProvider : IRecipeProvider
    {
        public Task<IReadOnlyList<CommandHelpItem>> GetRecipesAsync(CommandHelpQuery query, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<CommandHelpItem>>(Array.Empty<CommandHelpItem>());
        }
    }

    private sealed class EmptyErrorInsightService : IErrorInsightService
    {
        public Task<IReadOnlyList<CommandFixSuggestion>> AnalyzeAsync(CommandFailureContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<CommandFixSuggestion>>(Array.Empty<CommandFixSuggestion>());
        }
    }
}

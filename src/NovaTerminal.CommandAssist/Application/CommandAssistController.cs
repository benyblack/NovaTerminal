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
/// <see cref="AssistSessionContext"/> carries the environment all three share. The controller keeps
/// the view-model writes because presentation is the facade's job: the state machine says the
/// session is in an explicit Suggest session, the controller decides that means the bubble is up and
/// the popup is not.
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
    public AssistSessionState SessionState => _state.State;

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

    public void HandleTextInput(string text)
    {
        if (_context.IsAltScreenActive || string.IsNullOrEmpty(text))
        {
            return;
        }

        _state.ObserveTypedInput();
        ViewModel.QueryText += text;
        ViewModel.ModeLabel = "Suggest";
        ViewModel.IsPopupOpen = false;
        ViewModel.IsVisible = ViewModel.HasSuggestions;
        QueueRefreshSuggestions();
    }

    public void HandleBackspace()
    {
        if (_context.IsAltScreenActive || string.IsNullOrEmpty(ViewModel.QueryText))
        {
            return;
        }

        ViewModel.QueryText = ViewModel.QueryText[..^1];
        QueueRefreshSuggestions();
    }

    public void HandlePastedText(string text)
    {
        _state.ObservePastedText();
        ViewModel.QueryText = text ?? string.Empty;
        ViewModel.ModeLabel = "Suggest";
        ViewModel.IsPopupOpen = false;
        ViewModel.IsVisible = !_context.IsAltScreenActive && ViewModel.HasSuggestions;
        QueueRefreshSuggestions();
    }

    public async Task HandleEnterAsync()
    {
        try
        {
            await _capturePipeline.CaptureSubmissionAsync(
                ViewModel.QueryText,
                _state.IsCurrentSubmissionSuppressed);
        }
        finally
        {
            ResetSubmissionState();
        }
    }

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

        ViewModel.QueryText = insertionText;
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

        CommandAssistContextSnapshot snapshot = _context.CreateSnapshot(
            queryText ?? ViewModel.QueryText,
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
            queryText ?? ViewModel.QueryText,
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

    public async Task HandleShellIntegrationEventAsync(ShellIntegrationEvent shellEvent)
    {
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

        _suggestionOrchestrator.Refresh(_state.Mode, _state.IsExplicitSession, ViewModel.QueryText);
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

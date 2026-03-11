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

public sealed class CommandAssistController
{
    private bool _isAltScreenActive;
    private string? _workingDirectory;
    private string? _shellKind;
    private string? _profileId;
    private string? _sessionId;
    private string? _hostId;
    private bool _isRemote;
    private bool _isShellIntegrationEnabled;
    private bool _hasObservedShellIntegrationMarker;
    private bool _hasObservedStructuredCommandCaptureMarker;
    private bool _ignoreCurrentSubmission;
    private int _refreshVersion;
    private string? _pendingHistoryEntryId;
    private string? _pendingHistoryCommandText;
    private CommandAssistMode _currentMode = CommandAssistMode.Suggest;
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
        Action<Action>? dispatch = null)
        : this(
            historyStore,
            secretsFilter,
            suggestionEngine,
            snippetStore: null,
            commandDocsProvider: null,
            recipeProvider: null,
            errorInsightService: null,
            modeRouter: null,
            resultBuilder: null,
            dispatch)
    {
    }

    public CommandAssistController(
        IHistoryStore historyStore,
        ISecretsFilter secretsFilter,
        ISuggestionEngine suggestionEngine,
        ISnippetStore? snippetStore,
        Action<Action>? dispatch = null)
        : this(
            historyStore,
            secretsFilter,
            suggestionEngine,
            snippetStore,
            commandDocsProvider: null,
            recipeProvider: null,
            errorInsightService: null,
            modeRouter: null,
            resultBuilder: null,
            dispatch)
    {
    }

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
    }

    public IHistoryStore HistoryStore { get; }
    public ISnippetStore? SnippetStore { get; }
    public ISecretsFilter SecretsFilter { get; }
    public ISuggestionEngine SuggestionEngine { get; }
    public CommandAssistBarViewModel ViewModel { get; }
    public IReadOnlyList<AssistSuggestion> Suggestions => _suggestions;

    public void ToggleAssist()
    {
        if (_isAltScreenActive)
        {
            ViewModel.IsVisible = false;
            return;
        }

        _currentMode = CommandAssistMode.Suggest;
        ViewModel.ModeLabel = "Suggest";
        ViewModel.IsPopupOpen = false;
        ViewModel.IsVisible = !ViewModel.IsVisible;
    }

    public bool OpenHistorySearch()
    {
        if (_isAltScreenActive)
        {
            ViewModel.IsVisible = false;
            return false;
        }

        _currentMode = CommandAssistMode.Search;
        ViewModel.ModeLabel = "History";
        ViewModel.IsPopupOpen = true;
        ViewModel.IsVisible = true;
        QueueRefreshSuggestions();
        return true;
    }

    public bool OpenHelp()
    {
        if (_isAltScreenActive)
        {
            ViewModel.IsVisible = false;
            return false;
        }

        _ = OpenHelpAsync();
        return true;
    }

    public void HandleTextInput(string text)
    {
        if (_isAltScreenActive || string.IsNullOrEmpty(text))
        {
            return;
        }

        _ignoreCurrentSubmission = false;
        _currentMode = CommandAssistMode.Suggest;
        ViewModel.QueryText += text;
        ViewModel.ModeLabel = "Suggest";
        ViewModel.IsPopupOpen = false;
        ViewModel.IsVisible = true;
        QueueRefreshSuggestions();
    }

    public void HandleBackspace()
    {
        if (_isAltScreenActive || string.IsNullOrEmpty(ViewModel.QueryText))
        {
            return;
        }

        ViewModel.QueryText = ViewModel.QueryText[..^1];
        QueueRefreshSuggestions();
    }

    public void HandlePastedText(string text)
    {
        _ignoreCurrentSubmission = true;
        _currentMode = CommandAssistMode.Suggest;
        ViewModel.QueryText = text ?? string.Empty;
        ViewModel.ModeLabel = "Suggest";
        ViewModel.IsPopupOpen = false;
        ViewModel.IsVisible = !_isAltScreenActive;
        QueueRefreshSuggestions();
    }

    public async Task HandleEnterAsync()
    {
        try
        {
            string submission = ViewModel.QueryText.Trim();
            bool shouldPersist = !_isAltScreenActive &&
                                 !IsStructuredShellIntegrationActive() &&
                                 !_ignoreCurrentSubmission &&
                                 !string.IsNullOrWhiteSpace(submission) &&
                                 !submission.Contains('\n') &&
                                 !submission.Contains('\r');

            if (shouldPersist)
            {
                RedactionResult redaction = SecretsFilter.Redact(submission);
                var entry = new CommandHistoryEntry(
                    Id: Guid.NewGuid().ToString("N"),
                    CommandText: redaction.RedactedText,
                    ExecutedAt: DateTimeOffset.UtcNow,
                    ShellKind: _shellKind ?? "unknown",
                    WorkingDirectory: _workingDirectory,
                    ProfileId: _profileId,
                    SessionId: _sessionId,
                    HostId: _hostId,
                    ExitCode: null,
                    IsRemote: _isRemote,
                    IsRedacted: redaction.WasRedacted,
                    Source: CommandCaptureSource.Heuristic,
                    DurationMs: null);

                await HistoryStore.AppendAsync(entry);
                _pendingHistoryEntryId = entry.Id;
                _pendingHistoryCommandText = NormalizeCommandText(submission);
            }
        }
        catch
        {
            // Assist capture is best-effort; Enter should still reach the shell path even if persistence fails.
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
        _shellKind = shellKind;
        _workingDirectory = workingDirectory;
        _profileId = profileId;
        _sessionId = sessionId;
        _hostId = hostId;
        _isRemote = isRemote;
        _isShellIntegrationEnabled = isShellIntegrated;
        _hasObservedShellIntegrationMarker = _hasObservedShellIntegrationMarker && isShellIntegrated;
        _hasObservedStructuredCommandCaptureMarker = _hasObservedStructuredCommandCaptureMarker && isShellIntegrated;
    }

    public void SetShellIntegrationEnabled(bool isEnabled)
    {
        _isShellIntegrationEnabled = isEnabled;
        if (!isEnabled)
        {
            _hasObservedShellIntegrationMarker = false;
            _hasObservedStructuredCommandCaptureMarker = false;
        }
    }

    public void Dismiss()
    {
        CancelPendingRefreshes();
        _currentMode = CommandAssistMode.Suggest;
        ViewModel.IsVisible = false;
        ViewModel.IsPopupOpen = false;
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
        if (selected == null || _ignoreCurrentSubmission || _isAltScreenActive)
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
        _currentMode = CommandAssistMode.Suggest;
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
        string? pendingEntryId = _pendingHistoryEntryId;
        _pendingHistoryEntryId = null;
        if (string.IsNullOrWhiteSpace(pendingEntryId))
        {
            return;
        }

        try
        {
            await HistoryStore.TryUpdateExecutionResultAsync(pendingEntryId, exitCode, durationMs: null);
        }
        catch
        {
            // History metadata enrichment is best-effort only.
        }
    }

    public async Task<bool> OpenHelpAsync(string? queryText = null, string? selectedText = null)
    {
        if (_isAltScreenActive)
        {
            ViewModel.IsVisible = false;
            return false;
        }

        CommandAssistContextSnapshot snapshot = CreateContextSnapshot(queryText, selectedText);
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
        if (_isAltScreenActive)
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
        if (shellEvent.WorkingDirectory != null)
        {
            _workingDirectory = shellEvent.WorkingDirectory;
        }

        if (shellEvent.Type is ShellIntegrationEventType.PromptReady or
            ShellIntegrationEventType.CommandAccepted or
            ShellIntegrationEventType.CommandStarted or
            ShellIntegrationEventType.CommandFinished)
        {
            _hasObservedShellIntegrationMarker = true;
        }

        if (shellEvent.Type is ShellIntegrationEventType.CommandAccepted)
        {
            _hasObservedStructuredCommandCaptureMarker = true;
        }

        switch (shellEvent.Type)
        {
            case ShellIntegrationEventType.WorkingDirectoryChanged:
            case ShellIntegrationEventType.PromptReady:
            case ShellIntegrationEventType.CommandStarted:
                return;

            case ShellIntegrationEventType.CommandAccepted:
                await HandleShellIntegratedCommandAcceptedAsync(shellEvent);
                return;

            case ShellIntegrationEventType.CommandFinished:
                await HandleShellIntegratedCommandFinishedAsync(shellEvent);
                return;
        }
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
                                           string.Equals(x.ShellKind ?? string.Empty, _shellKind ?? string.Empty, StringComparison.OrdinalIgnoreCase));

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
                    ShellKind: _shellKind,
                    WorkingDirectory: _workingDirectory,
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
        _isAltScreenActive = isAltScreenActive;
        if (isAltScreenActive)
        {
            CancelPendingRefreshes();
            _currentMode = CommandAssistMode.Suggest;
            ViewModel.IsVisible = false;
            ViewModel.IsPopupOpen = false;
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
    }

    private void QueueRefreshSuggestions()
    {
        if (_currentMode is not (CommandAssistMode.Suggest or CommandAssistMode.Search))
        {
            return;
        }

        int refreshVersion = Interlocked.Increment(ref _refreshVersion);
        CommandAssistMode requestedMode = _currentMode;
        string query = ViewModel.QueryText;
        var context = new CommandAssistQueryContext(query, _workingDirectory, _shellKind, _profileId);
        _ = RefreshSuggestionsAsync(query, context, refreshVersion, requestedMode);
    }

    private async Task RefreshSuggestionsAsync(
        string query,
        CommandAssistQueryContext context,
        int refreshVersion,
        CommandAssistMode requestedMode)
    {
        try
        {
            IReadOnlyList<CommandHistoryEntry> history = string.IsNullOrWhiteSpace(query)
                ? await HistoryStore.GetRecentAsync(5)
                : await HistoryStore.SearchAsync(query, 5);
            IReadOnlyList<CommandSnippet> snippets = SnippetStore == null
                ? Array.Empty<CommandSnippet>()
                : await SnippetStore.GetAllAsync();

            IReadOnlyList<AssistSuggestion> suggestions = SuggestionEngine.GetSuggestions(history, snippets, context, 5);
            _dispatch(() =>
            {
                if (refreshVersion != _refreshVersion || _currentMode != requestedMode)
                {
                    return;
                }

                _suggestions.Clear();
                _suggestions.AddRange(suggestions);
                ViewModel.SelectedIndex = suggestions.Count > 0 ? 0 : -1;
                ViewModel.EmptyStateText = string.Empty;
                ViewModel.ShowEmptyState = false;
                SyncSuggestionViewModel();
            });
        }
        catch
        {
            _dispatch(() =>
            {
                if (refreshVersion != _refreshVersion || _currentMode != requestedMode)
                {
                    return;
                }

                _suggestions.Clear();
                ViewModel.TopSuggestionText = string.Empty;
                ViewModel.SelectedIndex = -1;
                ViewModel.SelectedBadgesText = string.Empty;
                ViewModel.SelectedMetadataText = string.Empty;
                ViewModel.SelectedDescriptionText = string.Empty;
                ViewModel.EmptyStateText = string.Empty;
                ViewModel.ShowEmptyState = false;
                ViewModel.HasSuggestions = false;
                ViewModel.Suggestions.Clear();
            });
        }
    }

    private void CancelPendingRefreshes()
    {
        Interlocked.Increment(ref _refreshVersion);
    }

    private void ResetSubmissionState()
    {
        CancelPendingRefreshes();
        _ignoreCurrentSubmission = false;
        ViewModel.QueryText = string.Empty;
        ViewModel.IsPopupOpen = false;
        ViewModel.TopSuggestionText = string.Empty;
        ViewModel.SelectedIndex = -1;
        ViewModel.SelectedBadgesText = string.Empty;
        ViewModel.SelectedMetadataText = string.Empty;
        ViewModel.SelectedDescriptionText = string.Empty;
        ViewModel.EmptyStateText = string.Empty;
        ViewModel.ShowEmptyState = false;
        ViewModel.HasSuggestions = false;
        ViewModel.Suggestions.Clear();
        ViewModel.IsVisible = false;
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

    private CommandAssistContextSnapshot CreateContextSnapshot(string? queryText, string? selectedText)
    {
        string effectiveQuery = queryText ?? ViewModel.QueryText;
        string recognizedSource = string.IsNullOrWhiteSpace(effectiveQuery) ? selectedText ?? string.Empty : effectiveQuery;

        return new CommandAssistContextSnapshot(
            QueryText: effectiveQuery,
            RecognizedCommand: RecognizedCommandParser.ParsePrimaryCommand(recognizedSource),
            ShellKind: _shellKind,
            WorkingDirectory: _workingDirectory,
            ProfileId: _profileId,
            SessionId: _sessionId,
            HostId: _hostId,
            IsRemote: _isRemote,
            SelectedText: selectedText);
    }

    private void ApplyHelperSuggestions(
        CommandAssistMode mode,
        string? queryText,
        IReadOnlyList<AssistSuggestion> suggestions,
        string emptyStateText,
        bool openPopup)
    {
        CancelPendingRefreshes();
        _currentMode = mode;
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

    private async Task HandleShellIntegratedCommandAcceptedAsync(ShellIntegrationEvent shellEvent)
    {
        if (!_isShellIntegrationEnabled || _isAltScreenActive)
        {
            return;
        }

        string commandText = shellEvent.CommandText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return;
        }

        string normalizedCommandText = NormalizeCommandText(commandText);
        if (!string.IsNullOrWhiteSpace(_pendingHistoryEntryId) &&
            string.Equals(_pendingHistoryCommandText, normalizedCommandText, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            RedactionResult redaction = SecretsFilter.Redact(commandText);
            var entry = new CommandHistoryEntry(
                Id: Guid.NewGuid().ToString("N"),
                CommandText: redaction.RedactedText,
                ExecutedAt: shellEvent.Timestamp,
                ShellKind: _shellKind ?? "unknown",
                WorkingDirectory: shellEvent.WorkingDirectory ?? _workingDirectory,
                ProfileId: _profileId,
                SessionId: _sessionId,
                HostId: _hostId,
                ExitCode: null,
                IsRemote: _isRemote,
                IsRedacted: redaction.WasRedacted,
                Source: CommandCaptureSource.ShellIntegration,
                DurationMs: null);

            await HistoryStore.AppendAsync(entry);
            _pendingHistoryEntryId = entry.Id;
            _pendingHistoryCommandText = normalizedCommandText;
        }
        catch
        {
            // Structured capture is best-effort and must not affect shell execution.
        }
    }

    private async Task HandleShellIntegratedCommandFinishedAsync(ShellIntegrationEvent shellEvent)
    {
        string? pendingEntryId = _pendingHistoryEntryId;
        if (string.IsNullOrWhiteSpace(pendingEntryId))
        {
            return;
        }

        long? durationMs = shellEvent.Duration.HasValue
            ? (long)Math.Round(shellEvent.Duration.Value.TotalMilliseconds)
            : null;

        try
        {
            await HistoryStore.TryUpdateExecutionResultAsync(pendingEntryId, shellEvent.ExitCode, durationMs);
            _pendingHistoryEntryId = null;
            _pendingHistoryCommandText = null;
        }
        catch
        {
            // Structured metadata updates are best-effort only.
        }
    }

    private bool IsStructuredShellIntegrationActive()
    {
        return _isShellIntegrationEnabled && _hasObservedStructuredCommandCaptureMarker;
    }

    private static string NormalizeCommandText(string commandText)
    {
        return commandText.Trim();
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

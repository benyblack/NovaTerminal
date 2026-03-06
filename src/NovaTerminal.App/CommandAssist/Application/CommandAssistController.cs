using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;
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
    private bool _ignoreCurrentSubmission;
    private int _refreshVersion;
    private readonly List<AssistSuggestion> _suggestions = new();
    private readonly Action<Action> _dispatch;

    public CommandAssistController(
        IHistoryStore historyStore,
        ISecretsFilter secretsFilter,
        ISuggestionEngine suggestionEngine,
        Action<Action>? dispatch = null)
    {
        HistoryStore = historyStore;
        SecretsFilter = secretsFilter;
        SuggestionEngine = suggestionEngine;
        ViewModel = new CommandAssistBarViewModel();
        _dispatch = dispatch ?? (action => action());
    }

    public IHistoryStore HistoryStore { get; }
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

        ViewModel.IsVisible = !ViewModel.IsVisible;
    }

    public bool OpenHistorySearch()
    {
        if (_isAltScreenActive)
        {
            ViewModel.IsVisible = false;
            return false;
        }

        ViewModel.ModeLabel = "History";
        ViewModel.IsVisible = true;
        QueueRefreshSuggestions();
        return true;
    }

    public void HandleTextInput(string text)
    {
        if (_isAltScreenActive || string.IsNullOrEmpty(text))
        {
            return;
        }

        _ignoreCurrentSubmission = false;
        ViewModel.QueryText += text;
        ViewModel.ModeLabel = "Suggest";
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
        ViewModel.QueryText = text ?? string.Empty;
        ViewModel.ModeLabel = "Suggest";
        ViewModel.IsVisible = !_isAltScreenActive;
        QueueRefreshSuggestions();
    }

    public async Task HandleEnterAsync()
    {
        try
        {
            string submission = ViewModel.QueryText.Trim();
            bool shouldPersist = !_isAltScreenActive &&
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
                    Source: CommandCaptureSource.Heuristic);

                await HistoryStore.AppendAsync(entry);
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
        bool isRemote)
    {
        _shellKind = shellKind;
        _workingDirectory = workingDirectory;
        _profileId = profileId;
        _sessionId = sessionId;
        _hostId = hostId;
        _isRemote = isRemote;
    }

    public void Dismiss()
    {
        CancelPendingRefreshes();
        ViewModel.IsVisible = false;
        ViewModel.TopSuggestionText = string.Empty;
        _suggestions.Clear();
    }

    public void HandleAltScreenChanged(bool isAltScreenActive)
    {
        _isAltScreenActive = isAltScreenActive;
        if (isAltScreenActive)
        {
            CancelPendingRefreshes();
            ViewModel.IsVisible = false;
            ViewModel.TopSuggestionText = string.Empty;
            _suggestions.Clear();
        }
    }

    private void QueueRefreshSuggestions()
    {
        int refreshVersion = Interlocked.Increment(ref _refreshVersion);
        string query = ViewModel.QueryText;
        var context = new CommandAssistQueryContext(query, _workingDirectory, _shellKind);
        _ = RefreshSuggestionsAsync(query, context, refreshVersion);
    }

    private async Task RefreshSuggestionsAsync(string query, CommandAssistQueryContext context, int refreshVersion)
    {
        try
        {
            IReadOnlyList<CommandHistoryEntry> history = string.IsNullOrWhiteSpace(query)
                ? await HistoryStore.GetRecentAsync(5)
                : await HistoryStore.SearchAsync(query, 5);

            IReadOnlyList<AssistSuggestion> suggestions = SuggestionEngine.GetSuggestions(history, context, 5);
            _dispatch(() =>
            {
                if (refreshVersion != _refreshVersion)
                {
                    return;
                }

                _suggestions.Clear();
                _suggestions.AddRange(suggestions);
                ViewModel.TopSuggestionText = suggestions.FirstOrDefault()?.DisplayText ?? string.Empty;
            });
        }
        catch
        {
            _dispatch(() =>
            {
                if (refreshVersion != _refreshVersion)
                {
                    return;
                }

                _suggestions.Clear();
                ViewModel.TopSuggestionText = string.Empty;
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
        ViewModel.TopSuggestionText = string.Empty;
        ViewModel.IsVisible = false;
        _suggestions.Clear();
    }
}

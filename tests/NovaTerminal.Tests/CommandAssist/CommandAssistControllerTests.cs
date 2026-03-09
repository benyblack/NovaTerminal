using NovaTerminal.CommandAssist.Application;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using System.Diagnostics;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class CommandAssistControllerTests
{
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
        Assert.Equal("History", controller.ViewModel.ModeLabel);
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
    public void HandleTextInput_UpdatesQueryAndTopSuggestion()
    {
        var historyStore = new InMemoryHistoryStore();
        historyStore.Seed(
            CreateEntry("git status"),
            CreateEntry("dotnet test"));

        var controller = CreateController(historyStore);

        controller.HandleTextInput("git ");

        Assert.Equal("git ", controller.ViewModel.QueryText);
        Assert.Equal("git status", controller.ViewModel.TopSuggestionText);
    }

    [Fact]
    public async Task HandleEnterAsync_PersistsSingleLineRedactedCommand()
    {
        var historyStore = new InMemoryHistoryStore();
        var controller = CreateController(historyStore);
        controller.HandleTextInput("gh auth login --password hunter2");

        await controller.HandleEnterAsync();

        Assert.Single(historyStore.Entries);
        Assert.Equal("gh auth login --password [REDACTED]", historyStore.Entries[0].CommandText);
        Assert.True(historyStore.Entries[0].IsRedacted);
        Assert.Equal(string.Empty, controller.ViewModel.QueryText);
    }

    [Fact]
    public async Task HandleEnterAsync_DoesNotPersistMultiLineScriptLikeInput()
    {
        var historyStore = new InMemoryHistoryStore();
        var controller = CreateController(historyStore);
        controller.HandleTextInput("echo one");
        controller.HandlePastedText("echo one\necho two");

        await controller.HandleEnterAsync();

        Assert.Empty(historyStore.Entries);
    }

    [Fact]
    public async Task HandleEnterAsync_WhenHistoryStoreThrows_DoesNotPropagate()
    {
        var controller = CreateController(new ThrowingHistoryStore());
        controller.HandleTextInput("git status");

        await controller.HandleEnterAsync();

        Assert.Equal(string.Empty, controller.ViewModel.QueryText);
        Assert.False(controller.ViewModel.IsVisible);
    }

    [Fact]
    public async Task HandleTextInput_DoesNotBlockWhileHistorySearchIsPending()
    {
        var historyStore = new DelayedHistoryStore(TimeSpan.FromMilliseconds(250), CreateEntry("git status"));
        var controller = CreateController(historyStore);
        var stopwatch = Stopwatch.StartNew();

        controller.HandleTextInput("git");

        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 100, $"HandleTextInput blocked for {stopwatch.ElapsedMilliseconds}ms");
        Assert.Equal("git", controller.ViewModel.QueryText);

        await historyStore.WaitForLastSearchAsync();
    }

    [Fact]
    public void MoveSelectionDown_WhenSuggestionsAreVisible_AdvancesSelectedSuggestion()
    {
        var historyStore = new InMemoryHistoryStore();
        historyStore.Seed(
            CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")),
            CreateEntry("git stash", DateTimeOffset.Parse("2026-03-01T09:59:00+00:00")));

        var controller = CreateController(historyStore);
        controller.OpenHistorySearch();
        controller.HandleTextInput("git st");

        bool moved = controller.MoveSelectionDown();

        Assert.True(moved);
        Assert.Equal(1, controller.ViewModel.SelectedIndex);
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

        var controller = CreateController(historyStore);
        controller.HandleTextInput("git st");
        await historyStore.WaitForSearchSettledAsync();
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
        controller.HandleTextInput("git status");

        await controller.HandleEnterAsync();
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
        controller.HandleTextInput("git status");

        await controller.HandleEnterAsync();

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
        controller.HandleTextInput("git status");

        await controller.HandleEnterAsync();

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
        controller.HandleTextInput("git status");

        await controller.HandleEnterAsync();
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
        controller.HandleTextInput("git status");

        await controller.HandleEnterAsync();

        Assert.Single(historyStore.Entries);
        Assert.Equal(CommandCaptureSource.ShellIntegration, historyStore.Entries[0].Source);
    }

    [Fact]
    public async Task HandleTextInput_WhenPinnedSnippetMatches_ShowsSnippetAsTopSuggestion()
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

        var controller = CreateController(historyStore, snippetStore, new CommandAssistSuggestionEngine());
        controller.HandleTextInput("git st");

        await snippetStore.WaitForReadAsync();

        Assert.Equal("Git Status", controller.ViewModel.TopSuggestionText);
        Assert.Equal(AssistSuggestionType.Snippet, controller.Suggestions[0].Type);
    }

    [Fact]
    public async Task TogglePinSelectionAsync_WhenHistorySuggestionSelected_CreatesPinnedSnippet()
    {
        var historyStore = new InMemoryHistoryStore();
        historyStore.Seed(CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        var snippetStore = new InMemorySnippetStore();
        var controller = CreateController(historyStore, snippetStore, new CommandAssistSuggestionEngine());
        controller.HandleTextInput("git st");

        bool toggled = await controller.TogglePinSelectionAsync();
        IReadOnlyList<CommandSnippet> snippets = await snippetStore.GetAllAsync();

        Assert.True(toggled);
        Assert.Single(snippets);
        Assert.True(snippets[0].IsPinned);
        Assert.Equal("git status", snippets[0].CommandText);
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

        var controller = CreateController(historyStore, snippetStore, new CommandAssistSuggestionEngine());
        controller.HandleTextInput("git st");
        await snippetStore.WaitForReadAsync();

        bool toggled = await controller.TogglePinSelectionAsync();
        IReadOnlyList<CommandSnippet> snippets = await snippetStore.GetAllAsync();

        Assert.True(toggled);
        Assert.Single(snippets);
        Assert.False(snippets[0].IsPinned);
    }

    private static CommandAssistController CreateController(
        IHistoryStore? historyStore = null,
        ISnippetStore? snippetStore = null,
        ISuggestionEngine? suggestionEngine = null)
    {
        historyStore ??= new InMemoryHistoryStore();
        var filter = new SecretsFilter();
        var engine = suggestionEngine ?? new HistorySuggestionEngine();

        return snippetStore == null
            ? new CommandAssistController(historyStore, filter, engine)
            : new CommandAssistController(historyStore, filter, engine, snippetStore);
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

    private sealed class InMemoryHistoryStore : IHistoryStore
    {
        private readonly List<CommandHistoryEntry> _entries = new();
        public IReadOnlyList<CommandHistoryEntry> Entries => _entries;

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

        public Task<IReadOnlyList<CommandHistoryEntry>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<CommandHistoryEntry> results = _entries
                .Where(x => x.CommandText.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(maxResults)
                .ToList();
            return Task.FromResult(results);
        }

        public Task<bool> TryUpdateExitCodeAsync(string entryId, int? exitCode, CancellationToken cancellationToken = default)
        {
            int index = _entries.FindIndex(x => x.Id == entryId);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            _entries[index] = _entries[index] with { ExitCode = exitCode };
            return Task.FromResult(true);
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

        public Task<bool> TryUpdateExitCodeAsync(string entryId, int? exitCode, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

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

        public Task<bool> TryUpdateExitCodeAsync(string entryId, int? exitCode, CancellationToken cancellationToken = default)
            => Task.FromException<bool>(new InvalidOperationException("simulated write failure"));

        public Task<bool> TryUpdateExecutionResultAsync(string entryId, int? exitCode, long? durationMs, CancellationToken cancellationToken = default)
            => Task.FromException<bool>(new InvalidOperationException("simulated write failure"));
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
}

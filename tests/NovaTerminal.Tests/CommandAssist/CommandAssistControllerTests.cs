using NovaTerminal.CommandAssist.Application;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;
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

    private static CommandAssistController CreateController(IHistoryStore? historyStore = null)
    {
        historyStore ??= new InMemoryHistoryStore();
        var filter = new SecretsFilter();
        var engine = new HistorySuggestionEngine();

        return new CommandAssistController(historyStore, filter, engine);
    }

    private static CommandHistoryEntry CreateEntry(string commandText)
    {
        return new CommandHistoryEntry(
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
            Source: CommandCaptureSource.Heuristic);
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

        public void Seed(params CommandHistoryEntry[] entries)
        {
            _entries.AddRange(entries);
        }
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
    }
}

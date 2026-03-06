using System.Text.Json;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.Storage;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class JsonHistoryStoreTests : IDisposable
{
    private readonly string _tempRoot;

    public JsonHistoryStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nova_command_assist_history_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task AppendAsync_PersistsEntriesAcrossStoreInstances()
    {
        string filePath = Path.Combine(_tempRoot, "history.json");
        var store = new JsonHistoryStore(filePath, maxEntries: 50);

        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));

        var reloaded = new JsonHistoryStore(filePath, maxEntries: 50);
        IReadOnlyList<CommandHistoryEntry> entries = await reloaded.GetRecentAsync(10);

        Assert.Single(entries);
        Assert.Equal("git status", entries[0].CommandText);
    }

    [Fact]
    public async Task AppendAsync_EnforcesRetentionLimitByKeepingMostRecentEntries()
    {
        string filePath = Path.Combine(_tempRoot, "history.json");
        var store = new JsonHistoryStore(filePath, maxEntries: 2);

        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        await store.AppendAsync(CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00")));
        await store.AppendAsync(CreateEntry("npm run build", executedAt: DateTimeOffset.Parse("2026-03-01T10:02:00+00:00")));

        IReadOnlyList<CommandHistoryEntry> entries = await store.GetRecentAsync(10);

        Assert.Equal(2, entries.Count);
        Assert.DoesNotContain(entries, entry => entry.CommandText == "git status");
        Assert.Equal("npm run build", entries[0].CommandText);
        Assert.Equal("dotnet test", entries[1].CommandText);
    }

    [Fact]
    public async Task ClearAsync_RemovesPersistedHistory()
    {
        string filePath = Path.Combine(_tempRoot, "history.json");
        var store = new JsonHistoryStore(filePath, maxEntries: 50);

        await store.AppendAsync(CreateEntry("git status"));
        await store.ClearAsync();

        IReadOnlyList<CommandHistoryEntry> entries = await store.GetRecentAsync(10);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task GetRecentAsync_WhenFileContainsInvalidJson_ReturnsEmptyHistory()
    {
        string filePath = Path.Combine(_tempRoot, "history.json");
        await File.WriteAllTextAsync(filePath, "{ this is not valid json");

        var store = new JsonHistoryStore(filePath, maxEntries: 50);
        IReadOnlyList<CommandHistoryEntry> entries = await store.GetRecentAsync(10);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task SearchAsync_ReturnsPrefixAndFuzzyMatchesOrderedByRelevance()
    {
        string filePath = Path.Combine(_tempRoot, "history.json");
        var store = new JsonHistoryStore(filePath, maxEntries: 50);

        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        await store.AppendAsync(CreateEntry("git stash pop", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00")));
        await store.AppendAsync(CreateEntry("docker status", executedAt: DateTimeOffset.Parse("2026-03-01T10:02:00+00:00")));

        IReadOnlyList<CommandHistoryEntry> entries = await store.SearchAsync("git sta", maxResults: 5);

        Assert.Equal(2, entries.Count);
        Assert.Equal("git status", entries[0].CommandText);
        Assert.Equal("git stash pop", entries[1].CommandText);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static CommandHistoryEntry CreateEntry(string commandText, DateTimeOffset? executedAt = null)
    {
        return new CommandHistoryEntry(
            Id: Guid.NewGuid().ToString("N"),
            CommandText: commandText,
            ExecutedAt: executedAt ?? DateTimeOffset.Parse("2026-03-01T10:00:00+00:00"),
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
}

using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.Storage;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class JsonSnippetStoreTests : IDisposable
{
    private readonly string _tempRoot;

    public JsonSnippetStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nova_command_assist_snippets_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task UpsertAsync_PersistsPinnedSnippetAcrossStoreInstances()
    {
        string filePath = Path.Combine(_tempRoot, "snippets.json");
        var store = new JsonSnippetStore(filePath);
        var snippet = new CommandSnippet(
            Id: "snippet-1",
            Name: "Git Status",
            CommandText: "git status",
            Description: "Show working tree state",
            ShellKind: "pwsh",
            WorkingDirectory: @"C:\repo",
            IsPinned: true,
            CreatedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00"),
            LastUsedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00"));

        await store.UpsertAsync(snippet);

        var reloaded = new JsonSnippetStore(filePath);
        IReadOnlyList<CommandSnippet> snippets = await reloaded.GetAllAsync();

        Assert.Single(snippets);
        Assert.True(snippets[0].IsPinned);
        Assert.Equal("git status", snippets[0].CommandText);
    }

    [Fact]
    public async Task RemoveAsync_DeletesSnippetFromPersistence()
    {
        string filePath = Path.Combine(_tempRoot, "snippets.json");
        var store = new JsonSnippetStore(filePath);
        var snippet = new CommandSnippet(
            Id: "snippet-1",
            Name: "Git Status",
            CommandText: "git status",
            Description: null,
            ShellKind: "pwsh",
            WorkingDirectory: null,
            IsPinned: false,
            CreatedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00"),
            LastUsedAt: null);

        await store.UpsertAsync(snippet);
        await store.RemoveAsync(snippet.Id);

        IReadOnlyList<CommandSnippet> snippets = await store.GetAllAsync();

        Assert.Empty(snippets);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}

using NovaTerminal.CommandAssist.Application;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.Storage;

namespace NovaTerminal.Tests.CommandAssist;

/// <summary>
/// The rules behind the Settings snippet manager (V2 Phase 4b, Phase 4 task 4).
/// </summary>
/// <remarks>
/// Run against a real <see cref="JsonSnippetStore"/> over a temp file rather than a fake, because
/// the interesting half of the behavior is the round trip: what survives an edit, what the store's
/// re-sort does to the order, and whether a delete actually reaches the file. A fake store would
/// test the editor against a mirror of its own assumptions. This is also the level the snippet UI is
/// tested at - <c>SettingsWindow</c> is an Avalonia <c>Window</c> that no test in this repo
/// constructs, so its handlers are kept to reading text boxes and calling these methods.
/// </remarks>
public sealed class SnippetEditorTests : IDisposable
{
    private readonly string _directory;
    private readonly JsonSnippetStore _store;

    public SnippetEditorTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nova_snippet_editor_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _store = new JsonSnippetStore(Path.Combine(_directory, "snippets.json"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }

    [Fact]
    public async Task LoadAsync_WhenTheStoreIsEmpty_ReturnsNothing()
    {
        var editor = new SnippetEditor(_store);

        Assert.Empty(await editor.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AddAsync_writes_through_to_the_store()
    {
        var editor = new SnippetEditor(_store);
        await editor.LoadAsync(CancellationToken.None);

        CommandSnippet? created = await editor.AddAsync("Deploy", "kubectl rollout restart deploy/api", CancellationToken.None);

        Assert.NotNull(created);

        // Read through a second editor over the same file: this is the round trip the UI depends on.
        var reader = new SnippetEditor(_store);
        CommandSnippet stored = Assert.Single(await reader.LoadAsync(CancellationToken.None));
        Assert.Equal("Deploy", stored.Name);
        Assert.Equal("kubectl rollout restart deploy/api", stored.CommandText);
        Assert.True(stored.IsPinned);
    }

    [Fact]
    public async Task AddAsync_refuses_a_blank_command()
    {
        // A snippet with no command is a row that does nothing when the user accepts it.
        var editor = new SnippetEditor(_store);
        await editor.LoadAsync(CancellationToken.None);

        Assert.Null(await editor.AddAsync("Nameless", "   ", CancellationToken.None));
        Assert.Empty(editor.Snippets);
    }

    [Fact]
    public async Task AddAsync_derives_a_name_from_the_command_when_none_is_given()
    {
        // "I want to save this command" is the whole intent; demanding a label first is a form.
        var editor = new SnippetEditor(_store);
        await editor.LoadAsync(CancellationToken.None);

        CommandSnippet? created = await editor.AddAsync(null, "  git status --short  ", CancellationToken.None);

        Assert.Equal("git status --short", created!.Name);
        Assert.Equal("git status --short", created.CommandText);
    }

    [Fact]
    public async Task AddAsync_truncates_a_derived_name_to_one_readable_line()
    {
        var editor = new SnippetEditor(_store);
        await editor.LoadAsync(CancellationToken.None);

        CommandSnippet? created = await editor.AddAsync(
            string.Empty,
            "docker run --rm -it --name a-very-long-container-name ubuntu:24.04 bash",
            CancellationToken.None);

        Assert.EndsWith("...", created!.Name, StringComparison.Ordinal);
        Assert.True(created.Name.Length <= 43);
    }

    [Fact]
    public async Task AddAsync_keeps_a_multiline_command_off_the_derived_name()
    {
        var editor = new SnippetEditor(_store);
        await editor.LoadAsync(CancellationToken.None);

        CommandSnippet? created = await editor.AddAsync(null, "cd repo\nmake test", CancellationToken.None);

        Assert.Equal("cd repo", created!.Name);
        Assert.Equal("cd repo\nmake test", created.CommandText);
    }

    [Fact]
    public async Task UpdateAsync_renames_and_rewrites_and_survives_a_reload()
    {
        var editor = new SnippetEditor(_store);
        await editor.LoadAsync(CancellationToken.None);
        CommandSnippet created = (await editor.AddAsync("Old", "old --command", CancellationToken.None))!;

        Assert.True(await editor.UpdateAsync(created.Id, "New", "new --command", CancellationToken.None));

        var reader = new SnippetEditor(_store);
        CommandSnippet stored = Assert.Single(await reader.LoadAsync(CancellationToken.None));
        Assert.Equal("New", stored.Name);
        Assert.Equal("new --command", stored.CommandText);
        Assert.Equal(created.Id, stored.Id);
    }

    [Fact]
    public async Task UpdateAsync_preserves_the_fields_the_editor_does_not_show()
    {
        // A two-field editor that wrote a whole record would discard the cwd a pinned suggestion was
        // captured with, and the loss would only show up later as a snippet that stopped ranking.
        var created = new CommandSnippet(
            Id: "snippet-1",
            Name: "Build",
            CommandText: "make",
            Description: "Captured from history",
            ShellKind: "bash",
            WorkingDirectory: "/repo",
            IsPinned: true,
            CreatedAt: DateTimeOffset.UnixEpoch,
            LastUsedAt: DateTimeOffset.UnixEpoch.AddDays(3));
        await _store.UpsertAsync(created, CancellationToken.None);

        var editor = new SnippetEditor(_store);
        await editor.LoadAsync(CancellationToken.None);
        Assert.True(await editor.UpdateAsync("snippet-1", "Build all", "make all", CancellationToken.None));

        CommandSnippet stored = Assert.Single(editor.Snippets);
        Assert.Equal("Build all", stored.Name);
        Assert.Equal("make all", stored.CommandText);
        Assert.Equal("Captured from history", stored.Description);
        Assert.Equal("bash", stored.ShellKind);
        Assert.Equal("/repo", stored.WorkingDirectory);
        Assert.True(stored.IsPinned);
        Assert.Equal(DateTimeOffset.UnixEpoch, stored.CreatedAt);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(3), stored.LastUsedAt);
    }

    [Fact]
    public async Task UpdateAsync_refuses_a_blank_command_and_changes_nothing()
    {
        var editor = new SnippetEditor(_store);
        await editor.LoadAsync(CancellationToken.None);
        CommandSnippet created = (await editor.AddAsync("Keep", "keep --this", CancellationToken.None))!;

        Assert.False(await editor.UpdateAsync(created.Id, "Keep", "  ", CancellationToken.None));

        CommandSnippet stored = Assert.Single(editor.Snippets);
        Assert.Equal("keep --this", stored.CommandText);
    }

    [Fact]
    public async Task UpdateAsync_on_an_unknown_id_reports_failure()
    {
        var editor = new SnippetEditor(_store);
        await editor.LoadAsync(CancellationToken.None);

        Assert.False(await editor.UpdateAsync("does-not-exist", "x", "y", CancellationToken.None));
    }

    [Fact]
    public async Task RemoveAsync_deletes_from_the_file()
    {
        // ISnippetStore.RemoveAsync had no caller in the app before this. This is it.
        var editor = new SnippetEditor(_store);
        await editor.LoadAsync(CancellationToken.None);
        CommandSnippet keep = (await editor.AddAsync("Keep", "keep", CancellationToken.None))!;
        CommandSnippet drop = (await editor.AddAsync("Drop", "drop", CancellationToken.None))!;

        Assert.True(await editor.RemoveAsync(drop.Id, CancellationToken.None));

        var reader = new SnippetEditor(_store);
        CommandSnippet stored = Assert.Single(await reader.LoadAsync(CancellationToken.None));
        Assert.Equal(keep.Id, stored.Id);
    }

    [Fact]
    public async Task RemoveAsync_on_an_unknown_id_reports_failure_and_deletes_nothing()
    {
        var editor = new SnippetEditor(_store);
        await editor.LoadAsync(CancellationToken.None);
        await editor.AddAsync("Keep", "keep", CancellationToken.None);

        Assert.False(await editor.RemoveAsync("does-not-exist", CancellationToken.None));
        Assert.Single(editor.Snippets);
    }

    [Fact]
    public async Task The_listed_order_is_the_store_order_after_a_rename()
    {
        // JsonSnippetStore re-sorts on write (pinned first, then by name). A locally patched list
        // would disagree with the file the moment a rename crossed a sort boundary, which is why
        // every mutation re-reads.
        var editor = new SnippetEditor(_store);
        await editor.LoadAsync(CancellationToken.None);
        await editor.AddAsync("alpha", "a", CancellationToken.None);
        CommandSnippet zulu = (await editor.AddAsync("zulu", "z", CancellationToken.None))!;

        Assert.Equal(["alpha", "zulu"], editor.Snippets.Select(x => x.Name));

        await editor.UpdateAsync(zulu.Id, "aardvark", "z", CancellationToken.None);

        Assert.Equal(["aardvark", "alpha"], editor.Snippets.Select(x => x.Name));
    }
}

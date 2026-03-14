using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class FileSystemPathSuggestionProviderTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _homeRoot;

    public FileSystemPathSuggestionProviderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "nova2-path-provider-tests", Guid.NewGuid().ToString("N"));
        _homeRoot = Path.Combine(_tempRoot, "home");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_homeRoot);
    }

    [Fact]
    public void GetSuggestions_WhenPrefixMatchesDirAndFile_OrdersDirectoryBeforeFile()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "apps"));
        File.WriteAllText(Path.Combine(_tempRoot, "apple.txt"), "x");

        var provider = new FileSystemPathSuggestionProvider(homeDirectoryOverride: _homeRoot);
        var context = new CommandAssistQueryContext(
            Input: "cd app",
            WorkingDirectory: _tempRoot,
            ShellKind: "pwsh",
            ProfileId: "profile-1");

        IReadOnlyList<AssistSuggestion> suggestions = provider.GetSuggestions(context, maxResults: 10);

        Assert.NotEmpty(suggestions);
        Assert.Equal(AssistSuggestionType.Path, suggestions[0].Type);
        Assert.Contains("Directory", suggestions[0].Badges);
        Assert.DoesNotContain("File", suggestions[0].Badges);
    }

    [Fact]
    public void GetSuggestions_WhenInputIsRemote_ReturnsNoLocalPathSuggestions()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "docs"));

        var provider = new FileSystemPathSuggestionProvider(homeDirectoryOverride: _homeRoot);
        var context = new CommandAssistQueryContext(
            Input: "cd d",
            WorkingDirectory: _tempRoot,
            ShellKind: "pwsh",
            ProfileId: "profile-1",
            IsRemote: true);

        IReadOnlyList<AssistSuggestion> suggestions = provider.GetSuggestions(context, maxResults: 10);

        Assert.Empty(suggestions);
    }

    [Fact]
    public void GetSuggestions_WhenUsingTildePrefix_ExtendsExistingQuery()
    {
        Directory.CreateDirectory(Path.Combine(_homeRoot, "docs"));

        var provider = new FileSystemPathSuggestionProvider(homeDirectoryOverride: _homeRoot);
        var context = new CommandAssistQueryContext(
            Input: "cd ~/do",
            WorkingDirectory: _tempRoot,
            ShellKind: "pwsh",
            ProfileId: "profile-1");

        IReadOnlyList<AssistSuggestion> suggestions = provider.GetSuggestions(context, maxResults: 10);

        AssistSuggestion suggestion = Assert.Single(suggestions);
        Assert.StartsWith(context.Input, suggestion.InsertText, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSuggestions_WhenPwshPathContainsSpace_EscapesInsertedSuffix()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "My Folder"));

        var provider = new FileSystemPathSuggestionProvider(homeDirectoryOverride: _homeRoot);
        var context = new CommandAssistQueryContext(
            Input: @"cd .\M",
            WorkingDirectory: _tempRoot,
            ShellKind: "pwsh",
            ProfileId: "profile-1");

        IReadOnlyList<AssistSuggestion> suggestions = provider.GetSuggestions(context, maxResults: 10);

        AssistSuggestion suggestion = Assert.Single(suggestions);
        Assert.Equal(@"cd .\My` Folder\", suggestion.InsertText);
    }

    [Fact]
    public void GetSuggestions_WhenPwshPathTokenIsQuoted_DoesNotEscapeInsertedSuffix()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "My Folder"));

        var provider = new FileSystemPathSuggestionProvider(homeDirectoryOverride: _homeRoot);
        var context = new CommandAssistQueryContext(
            Input: "cd \".\\M",
            WorkingDirectory: _tempRoot,
            ShellKind: "pwsh",
            ProfileId: "profile-1");

        IReadOnlyList<AssistSuggestion> suggestions = provider.GetSuggestions(context, maxResults: 10);

        AssistSuggestion suggestion = Assert.Single(suggestions);
        Assert.Equal("cd \".\\My Folder\\", suggestion.InsertText);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup for temp test folders.
        }
    }
}

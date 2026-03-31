using System.Linq;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class CommandAssistSuggestionEngineTests
{
    [Fact]
    public void AssistSuggestion_CanCarryHelperDescriptionAndBadges()
    {
        var suggestion = new AssistSuggestion(
            Id: "doc-1",
            Type: AssistSuggestionType.Doc,
            DisplayText: "git checkout",
            InsertText: "git checkout <branch>",
            Description: "Switch branches or restore files.",
            Badges: ["Doc", "Git"],
            Score: 42,
            WorkingDirectory: @"C:\repo",
            LastUsedAt: null,
            ExitCode: null,
            CanExecuteDirectly: false);

        Assert.Equal("Switch branches or restore files.", suggestion.Description);
        Assert.Contains("Doc", suggestion.Badges);
        Assert.Equal(AssistSuggestionType.Doc, suggestion.Type);
    }

    [Theory]
    [InlineData(AssistSuggestionType.Recipe)]
    [InlineData(AssistSuggestionType.Doc)]
    [InlineData(AssistSuggestionType.Fix)]
    public void AssistSuggestionType_DefinesHelperRowKinds(AssistSuggestionType type)
    {
        var suggestion = new AssistSuggestion(
            Id: Guid.NewGuid().ToString("N"),
            Type: type,
            DisplayText: "helper row",
            InsertText: "helper row",
            Description: "Helper detail",
            Badges: ["Helper"],
            Score: 1,
            WorkingDirectory: null,
            LastUsedAt: null,
            ExitCode: null,
            CanExecuteDirectly: false);

        Assert.Equal(type, suggestion.Type);
        Assert.Equal("Helper detail", suggestion.Description);
    }

    [Fact]
    public void GetSuggestions_PinnedSnippetRanksAboveHistoryWithSimilarMatch()
    {
        var engine = new CommandAssistSuggestionEngine();
        var context = new CommandAssistQueryContext(
            Input: "git st",
            WorkingDirectory: @"C:\repo",
            ShellKind: "pwsh",
            ProfileId: "profile-1");

        var history = new[]
        {
            CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00"))
        };

        var snippets = new[]
        {
            CreateSnippet("Git Status", "git status", isPinned: true)
        };

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(history, snippets, context, maxResults: 5);

        Assert.Equal(AssistSuggestionType.Snippet, results[0].Type);
        Assert.Null(results[0].Description);
        Assert.Contains("Pinned", results[0].Badges);
    }

    [Fact]
    public void GetSuggestions_SuccessfulCommandBeatsFailedCommandWhenTextSignalsMatch()
    {
        var engine = new CommandAssistSuggestionEngine();
        var context = new CommandAssistQueryContext(
            Input: "dotnet t",
            WorkingDirectory: @"C:\repo",
            ShellKind: "pwsh",
            ProfileId: "profile-1");

        var history = new[]
        {
            CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00"), exitCode: 1),
            CreateEntry("dotnet tool list", executedAt: DateTimeOffset.Parse("2026-03-01T09:59:00+00:00"), exitCode: 0)
        };

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(history, Array.Empty<CommandSnippet>(), context, maxResults: 5);

        Assert.Equal("dotnet tool list", results[0].InsertText);
        Assert.Contains("Worked", results[0].Badges);
    }

    [Fact]
    public void GetSuggestions_SameProfileBoostBreaksOtherwiseEquivalentMatches()
    {
        var engine = new CommandAssistSuggestionEngine();
        var context = new CommandAssistQueryContext(
            Input: "cargo t",
            WorkingDirectory: @"C:\repo",
            ShellKind: "pwsh",
            ProfileId: "profile-a");

        var history = new[]
        {
            CreateEntry("cargo test", profileId: "profile-b", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")),
            CreateEntry("cargo tree", profileId: "profile-a", executedAt: DateTimeOffset.Parse("2026-03-01T09:59:00+00:00"))
        };

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(history, Array.Empty<CommandSnippet>(), context, maxResults: 5);

        Assert.Equal("cargo tree", results[0].InsertText);
    }

    [Fact]
    public void GetSuggestions_UnrelatedNonEmptyQuery_DoesNotReturnPinnedSnippet()
    {
        var engine = new CommandAssistSuggestionEngine();
        var context = new CommandAssistQueryContext(
            Input: "kubectl",
            WorkingDirectory: @"C:\repo",
            ShellKind: "pwsh",
            ProfileId: "profile-1");

        var snippets = new[]
        {
            CreateSnippet("Git Status", "git status", isPinned: true)
        };

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(
            Array.Empty<CommandHistoryEntry>(),
            snippets,
            context,
            maxResults: 5);

        Assert.Empty(results);
    }

    [Fact]
    public void GetSuggestions_WhenPathProviderReturnsMatches_IncludesPathRows()
    {
        var engine = new CommandAssistSuggestionEngine(
            pathSuggestionProvider: new FakePathSuggestionProvider(
                new[]
                {
                    new AssistSuggestion(
                        Id: "path-1",
                        Type: AssistSuggestionType.Path,
                        DisplayText: "docs/",
                        InsertText: "cd ./docs/",
                        Description: "Directory",
                        Badges: ["Path", "Directory"],
                        Score: 500,
                        WorkingDirectory: @"C:\repo",
                        LastUsedAt: null,
                        ExitCode: null,
                        CanExecuteDirectly: false)
                }));
        var context = new CommandAssistQueryContext(
            Input: "cd ./d",
            WorkingDirectory: @"C:\repo",
            ShellKind: "pwsh",
            ProfileId: "profile-1");

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(
            Array.Empty<CommandHistoryEntry>(),
            Array.Empty<CommandSnippet>(),
            context,
            maxResults: 5);

        Assert.NotEmpty(results);
        Assert.Equal(AssistSuggestionType.Path, results[0].Type);
    }

    [Fact]
    public void GetSuggestions_WhenPathRowsExist_PrioritizesPathOverHighScoreHistory()
    {
        var engine = new CommandAssistSuggestionEngine(
            pathSuggestionProvider: new FakePathSuggestionProvider(
                new[]
                {
                    new AssistSuggestion(
                        Id: "path-1",
                        Type: AssistSuggestionType.Path,
                        DisplayText: "docs/",
                        InsertText: "cd ./docs/",
                        Description: "Directory",
                        Badges: ["Path", "Directory"],
                        Score: 5,
                        WorkingDirectory: @"C:\repo",
                        LastUsedAt: null,
                        ExitCode: null,
                        CanExecuteDirectly: false)
                }));

        var context = new CommandAssistQueryContext(
            Input: "cd ",
            WorkingDirectory: @"C:\repo",
            ShellKind: "pwsh",
            ProfileId: "profile-1");

        var history = Enumerable.Range(0, 20)
            .Select(i => CreateEntry("cd C:\\repo", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00").AddMinutes(i)))
            .ToArray();

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(
            history,
            Array.Empty<CommandSnippet>(),
            context,
            maxResults: 5);

        Assert.NotEmpty(results);
        Assert.Equal(AssistSuggestionType.Path, results[0].Type);
    }

    [Fact]
    public void GetSuggestions_WhenContextDisablesHistoryAndSnippets_ReturnsOnlyPathRows()
    {
        var engine = new CommandAssistSuggestionEngine(
            pathSuggestionProvider: new FakePathSuggestionProvider(
                new[]
                {
                    new AssistSuggestion(
                        Id: "path-1",
                        Type: AssistSuggestionType.Path,
                        DisplayText: "docs/",
                        InsertText: "cd ./docs/",
                        Description: "Directory",
                        Badges: ["Path", "Directory"],
                        Score: 100,
                        WorkingDirectory: @"C:\repo",
                        LastUsedAt: null,
                        ExitCode: null,
                        CanExecuteDirectly: false)
                }));

        var context = new CommandAssistQueryContext(
            Input: "git st",
            WorkingDirectory: @"C:\repo",
            ShellKind: "pwsh",
            ProfileId: "profile-1",
            IsRemote: false,
            IncludeHistorySuggestions: false,
            IncludeSnippetSuggestions: false,
            IncludePathSuggestions: true);

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(
            new[] { CreateEntry("git status") },
            new[] { CreateSnippet("Git Status", "git status", isPinned: true) },
            context,
            maxResults: 5);

        Assert.Single(results);
        Assert.Equal(AssistSuggestionType.Path, results[0].Type);
    }

    [Fact]
    public void GetSuggestions_WhenContextDisablesPathRows_DoesNotReturnPathSuggestions()
    {
        var engine = new CommandAssistSuggestionEngine(
            pathSuggestionProvider: new FakePathSuggestionProvider(
                new[]
                {
                    new AssistSuggestion(
                        Id: "path-1",
                        Type: AssistSuggestionType.Path,
                        DisplayText: "docs/",
                        InsertText: "cd ./docs/",
                        Description: "Directory",
                        Badges: ["Path", "Directory"],
                        Score: 100,
                        WorkingDirectory: @"C:\repo",
                        LastUsedAt: null,
                        ExitCode: null,
                        CanExecuteDirectly: false)
                }));

        var context = new CommandAssistQueryContext(
            Input: "git st",
            WorkingDirectory: @"C:\repo",
            ShellKind: "pwsh",
            ProfileId: "profile-1",
            IsRemote: false,
            IncludeHistorySuggestions: true,
            IncludeSnippetSuggestions: false,
            IncludePathSuggestions: false);

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(
            new[] { CreateEntry("git status") },
            Array.Empty<CommandSnippet>(),
            context,
            maxResults: 5);

        Assert.NotEmpty(results);
        Assert.All(results, suggestion => Assert.NotEqual(AssistSuggestionType.Path, suggestion.Type));
    }

    private static CommandHistoryEntry CreateEntry(
        string commandText,
        string? profileId = "profile-1",
        DateTimeOffset? executedAt = null,
        int? exitCode = 0)
    {
        return new CommandHistoryEntry(
            Id: Guid.NewGuid().ToString("N"),
            CommandText: commandText,
            ExecutedAt: executedAt ?? DateTimeOffset.Parse("2026-03-01T10:00:00+00:00"),
            ShellKind: "pwsh",
            WorkingDirectory: @"C:\repo",
            ProfileId: profileId,
            SessionId: "session-1",
            HostId: null,
            ExitCode: exitCode,
            IsRemote: false,
            IsRedacted: false,
            Source: CommandCaptureSource.Heuristic,
            DurationMs: null);
    }

    private static CommandSnippet CreateSnippet(string name, string commandText, bool isPinned)
    {
        return new CommandSnippet(
            Id: Guid.NewGuid().ToString("N"),
            Name: name,
            CommandText: commandText,
            Description: null,
            ShellKind: "pwsh",
            WorkingDirectory: @"C:\repo",
            IsPinned: isPinned,
            CreatedAt: DateTimeOffset.Parse("2026-03-01T09:00:00+00:00"),
            LastUsedAt: DateTimeOffset.Parse("2026-03-01T09:30:00+00:00"));
    }

    private sealed class FakePathSuggestionProvider : IPathSuggestionProvider
    {
        private readonly IReadOnlyList<AssistSuggestion> _suggestions;

        public FakePathSuggestionProvider(IReadOnlyList<AssistSuggestion> suggestions)
        {
            _suggestions = suggestions;
        }

        public IReadOnlyList<AssistSuggestion> GetSuggestions(CommandAssistQueryContext context, int maxResults)
        {
            return _suggestions.Take(maxResults).ToArray();
        }
    }
}

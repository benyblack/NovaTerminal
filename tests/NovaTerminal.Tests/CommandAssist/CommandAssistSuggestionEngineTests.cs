using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class CommandAssistSuggestionEngineTests
{
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
            Source: CommandCaptureSource.Heuristic);
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
}

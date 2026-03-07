using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class HistorySuggestionEngineTests
{
    [Fact]
    public void GetSuggestions_ExactPrefixMatchRanksAboveFuzzyMatch()
    {
        var engine = new HistorySuggestionEngine();
        var context = new CommandAssistQueryContext(
            Input: "git sta",
            WorkingDirectory: @"C:\repo",
            ShellKind: "pwsh",
            ProfileId: "profile-1");

        var entries = new[]
        {
            CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")),
            CreateEntry("grep git src/status.cs", executedAt: DateTimeOffset.Parse("2026-03-01T11:00:00+00:00"))
        };

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(entries, context, maxResults: 5);

        Assert.Equal("git status", results[0].InsertText);
    }

    [Fact]
    public void GetSuggestions_MoreRecentEntryWinsWhenTextMatchQualityIsEqual()
    {
        var engine = new HistorySuggestionEngine();
        var context = new CommandAssistQueryContext(
            Input: "dotnet t",
            WorkingDirectory: @"C:\repo",
            ShellKind: "pwsh",
            ProfileId: "profile-1");

        var entries = new[]
        {
            CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T09:00:00+00:00")),
            CreateEntry("dotnet tool list", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00"))
        };

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(entries, context, maxResults: 5);

        Assert.Equal("dotnet tool list", results[0].InsertText);
    }

    [Fact]
    public void GetSuggestions_MoreFrequentEntryWinsWhenOtherSignalsAreSimilar()
    {
        var engine = new HistorySuggestionEngine();
        var context = new CommandAssistQueryContext(
            Input: "npm run",
            WorkingDirectory: @"C:\repo",
            ShellKind: "pwsh",
            ProfileId: "profile-1");

        var entries = new[]
        {
            CreateEntry("npm run build", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")),
            CreateEntry("npm run build", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00")),
            CreateEntry("npm run test", executedAt: DateTimeOffset.Parse("2026-03-01T10:02:00+00:00"))
        };

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(entries, context, maxResults: 5);

        Assert.Equal("npm run build", results[0].InsertText);
    }

    [Fact]
    public void GetSuggestions_SameWorkingDirectoryGetsBoost()
    {
        var engine = new HistorySuggestionEngine();
        var context = new CommandAssistQueryContext(
            Input: "cargo t",
            WorkingDirectory: @"C:\repo-a",
            ShellKind: "pwsh",
            ProfileId: "profile-1");

        var entries = new[]
        {
            CreateEntry("cargo test", workingDirectory: @"C:\repo-b", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")),
            CreateEntry("cargo tree", workingDirectory: @"C:\repo-a", executedAt: DateTimeOffset.Parse("2026-03-01T09:59:00+00:00"))
        };

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(entries, context, maxResults: 5);

        Assert.Equal("cargo tree", results[0].InsertText);
    }

    private static CommandHistoryEntry CreateEntry(
        string commandText,
        string? workingDirectory = null,
        DateTimeOffset? executedAt = null)
    {
        return new CommandHistoryEntry(
            Id: Guid.NewGuid().ToString("N"),
            CommandText: commandText,
            ExecutedAt: executedAt ?? DateTimeOffset.Parse("2026-03-01T10:00:00+00:00"),
            ShellKind: "pwsh",
            WorkingDirectory: workingDirectory ?? @"C:\repo",
            ProfileId: "profile-1",
            SessionId: "session-1",
            HostId: null,
            ExitCode: 0,
            IsRemote: false,
            IsRedacted: false,
            Source: CommandCaptureSource.Heuristic);
    }
}

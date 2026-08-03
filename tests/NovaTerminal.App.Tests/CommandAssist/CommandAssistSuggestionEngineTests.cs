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
            ExitCode: null);

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
            ExitCode: null);

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
                        ExitCode: null)
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
                        ExitCode: null)
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
                        ExitCode: null)
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
                        ExitCode: null)
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

    // ------------------------------------------- context-scoped ranking (V2 Phase 3a)
    //
    // The owner's second report: "the list shows commands from all sessions/tabs indiscriminately".
    // The fix is an ordering rule, not a filter - a command run somewhere else is still in the list,
    // below the entries from here, because reaching for a command you remember running on another box
    // is the reason a shared history exists at all.

    /// <summary>
    /// <c>Ctrl+R</c> on an SSH pane: this host's commands come first, and the local ones are still
    /// there afterwards.
    /// </summary>
    [Fact]
    public void GetSuggestions_WithNoQueryOnARemotePane_RanksThisHostsCommandsFirst()
    {
        var engine = new CommandAssistSuggestionEngine(new FakePathSuggestionProvider(Array.Empty<AssistSuggestion>()));
        var context = new CommandAssistQueryContext(
            Input: string.Empty,
            WorkingDirectory: "/home/nova",
            ShellKind: "bash",
            ProfileId: "profile-ssh",
            IsRemote: true,
            HostId: "ubuntu.example");

        // The local entry is the most recent, so pure recency (the pre-Phase-3a rule) puts it first.
        var history = new[]
        {
            CreateEntry("dotnet build", executedAt: DateTimeOffset.Parse("2026-03-01T12:00:00+00:00")),
            CreateRemoteEntry("systemctl status nova", "ubuntu.example", DateTimeOffset.Parse("2026-03-01T11:00:00+00:00")),
            CreateRemoteEntry("journalctl -u nova", "other.example", DateTimeOffset.Parse("2026-03-01T11:30:00+00:00"))
        };

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(history, context, maxResults: 10);

        Assert.Equal("systemctl status nova", results[0].DisplayText);
        Assert.Contains(results, item => item.DisplayText == "dotnet build");
        Assert.Contains(results, item => item.DisplayText == "journalctl -u nova");
    }

    /// <summary>
    /// The same rule on a local pane, where "here" means "not on somebody else's machine": there is no
    /// local host id to compare, so localness is the context.
    /// </summary>
    [Fact]
    public void GetSuggestions_WithNoQueryOnALocalPane_RanksLocalCommandsFirst()
    {
        var engine = new CommandAssistSuggestionEngine(new FakePathSuggestionProvider(Array.Empty<AssistSuggestion>()));
        var context = new CommandAssistQueryContext(
            Input: string.Empty,
            WorkingDirectory: @"C:\repo",
            ShellKind: "pwsh",
            ProfileId: "profile-1");

        var history = new[]
        {
            CreateRemoteEntry("apt install ripgrep", "ubuntu.example", DateTimeOffset.Parse("2026-03-01T12:00:00+00:00")),
            CreateEntry("dotnet build", executedAt: DateTimeOffset.Parse("2026-03-01T11:00:00+00:00"))
        };

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(history, context, maxResults: 10);

        Assert.Equal("dotnet build", results[0].DisplayText);
        Assert.Contains(results, item => item.DisplayText == "apt install ripgrep");
    }

    /// <summary>
    /// Two remote hosts, one pane. Another host's entries rank below this host's - which is the case
    /// the owner actually hit, since a single global recency list is dominated by whichever pane ran a
    /// command last.
    /// </summary>
    [Fact]
    public void GetSuggestions_WithNoQueryOnARemotePane_RanksOtherHostsBelowThisOne()
    {
        var engine = new CommandAssistSuggestionEngine(new FakePathSuggestionProvider(Array.Empty<AssistSuggestion>()));
        var context = new CommandAssistQueryContext(
            Input: string.Empty,
            WorkingDirectory: "/home/nova",
            ShellKind: "bash",
            ProfileId: "profile-ssh",
            IsRemote: true,
            HostId: "ubuntu.example");

        var history = new[]
        {
            CreateRemoteEntry("uptime", "other.example", DateTimeOffset.Parse("2026-03-01T12:00:00+00:00")),
            CreateRemoteEntry("df -h", "ubuntu.example", DateTimeOffset.Parse("2026-03-01T09:00:00+00:00"))
        };

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(history, context, maxResults: 10);

        Assert.Equal("df -h", results[0].DisplayText);
        Assert.Equal("uptime", results[1].DisplayText);
    }

    /// <summary>
    /// An SSH profile whose host is not yet known must not match everything. An unknown context is not
    /// a context, and the fallback is the pre-Phase-3a recency order.
    /// </summary>
    [Fact]
    public void GetSuggestions_OnARemotePaneWithNoHostId_AppliesNoContextBoost()
    {
        var engine = new CommandAssistSuggestionEngine(new FakePathSuggestionProvider(Array.Empty<AssistSuggestion>()));
        var context = new CommandAssistQueryContext(
            Input: string.Empty,
            WorkingDirectory: "/home/nova",
            ShellKind: "bash",

            // Null so that the profile term cannot stand in for the host term this test is about.
            ProfileId: null,
            IsRemote: true,
            HostId: null);

        var history = new[]
        {
            CreateEntry("dotnet build", executedAt: DateTimeOffset.Parse("2026-03-01T12:00:00+00:00")),
            CreateRemoteEntry("df -h", "ubuntu.example", DateTimeOffset.Parse("2026-03-01T11:00:00+00:00"))
        };

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(history, context, maxResults: 10);

        Assert.Equal("dotnet build", results[0].DisplayText);
    }

    /// <summary>
    /// The text-query path gets a host boost alongside the existing profile boost, sized as a nudge:
    /// with equally good text matches the same-host row wins.
    /// </summary>
    [Fact]
    public void GetSuggestions_WithATextQueryOnARemotePane_PrefersTheSameHostAmongEqualMatches()
    {
        var engine = new CommandAssistSuggestionEngine(new FakePathSuggestionProvider(Array.Empty<AssistSuggestion>()));
        var context = new CommandAssistQueryContext(
            Input: "systemctl s",
            WorkingDirectory: "/home/nova",
            ShellKind: "bash",
            ProfileId: null,
            IsRemote: true,
            HostId: "ubuntu.example");

        var history = new[]
        {
            CreateRemoteEntry("systemctl start nova", "other.example", DateTimeOffset.Parse("2026-03-01T12:00:00+00:00")),
            CreateRemoteEntry("systemctl status nova", "ubuntu.example", DateTimeOffset.Parse("2026-03-01T09:00:00+00:00"))
        };

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(history, context, maxResults: 10);

        Assert.Equal("systemctl status nova", results[0].DisplayText);
        Assert.Equal("systemctl start nova", results[1].DisplayText);
    }

    /// <summary>
    /// And the nudge must stay a nudge: a same-host row that matches the query worse than a local row
    /// still loses. A partition here would read as the list ignoring what the user typed.
    /// </summary>
    [Fact]
    public void GetSuggestions_WithATextQuery_DoesNotLetTheContextBoostBeatABetterTextMatch()
    {
        var engine = new CommandAssistSuggestionEngine(new FakePathSuggestionProvider(Array.Empty<AssistSuggestion>()));
        var context = new CommandAssistQueryContext(
            Input: "git st",
            WorkingDirectory: "/home/nova",
            ShellKind: "bash",
            ProfileId: null,
            IsRemote: true,
            HostId: "ubuntu.example");

        var history = new[]
        {
            // Prefix match (120) locally...
            CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T09:00:00+00:00")),

            // ...against a mere subsequence match (12) plus the host boost (30) on this host.
            CreateRemoteEntry("grep -r it standalone", "ubuntu.example", DateTimeOffset.Parse("2026-03-01T12:00:00+00:00"))
        };

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(history, context, maxResults: 10);

        Assert.Equal("git status", results[0].DisplayText);
    }

    /// <summary>
    /// Pinned snippets keep their place at the top of an empty-query list. They have no host or
    /// remoteness to compare, and pinning already means "in scope everywhere", so they share the
    /// context band rather than being pushed below every same-host history row.
    /// </summary>
    [Fact]
    public void GetSuggestions_WithNoQuery_KeepsPinnedSnippetsAboveContextMatchedHistory()
    {
        var engine = new CommandAssistSuggestionEngine(new FakePathSuggestionProvider(Array.Empty<AssistSuggestion>()));
        var context = new CommandAssistQueryContext(
            Input: string.Empty,
            WorkingDirectory: @"C:\repo",
            ShellKind: "pwsh",
            ProfileId: "profile-1");

        var history = new[]
        {
            CreateEntry("dotnet build", executedAt: DateTimeOffset.Parse("2026-03-01T12:00:00+00:00"))
        };

        var snippets = new[]
        {
            CreateSnippet("Deploy", "./deploy.sh --prod", isPinned: true)
        };

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(history, snippets, context, maxResults: 10);

        Assert.Equal(AssistSuggestionType.Snippet, results[0].Type);
    }

    private static CommandHistoryEntry CreateRemoteEntry(
        string commandText,
        string hostId,
        DateTimeOffset executedAt)
    {
        return new CommandHistoryEntry(
            Id: Guid.NewGuid().ToString("N"),
            CommandText: commandText,
            ExecutedAt: executedAt,
            ShellKind: "bash",
            WorkingDirectory: "/home/nova",
            ProfileId: "profile-ssh",
            SessionId: "session-ssh",
            HostId: hostId,
            ExitCode: 0,
            IsRemote: true,
            IsRedacted: false,
            Source: CommandCaptureSource.ShellIntegration,
            DurationMs: null);
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

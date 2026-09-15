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

    // ---------------------------------------------- UX-polish round: recency-first empty query

    /// <summary>
    /// The owner's exact complaint, as a test: the command he ran seconds ago must outrank the one he
    /// ran five times last week.
    /// </summary>
    /// <remarks>
    /// <para>
    /// His screenshot had <c>vim .env</c> and <c>git pull</c> at the top, badged "Frequent", from a
    /// directory he was no longer in - while the commands he had just run sat below the fold. The old
    /// empty-query score was <c>1 + min(frequency,5) + bands</c> with recency only a tiebreak on exact
    /// equality, so five repetitions (1006) beat anything run once (1002) no matter when.
    /// </para>
    /// <para>
    /// <strong>This is the mutation check for the ordering.</strong> Reverting
    /// <c>CommandAssistSuggestionEngine</c> to score frequency on the empty-query path puts
    /// <c>vim .env</c> first and fails here.
    /// </para>
    /// </remarks>
    [Fact]
    public void GetSuggestions_WithAnEmptyQuery_RanksTheMostRecentCommandAboveTheMostFrequentOne()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var engine = new CommandAssistSuggestionEngine(clock: () => now);
        var context = new CommandAssistQueryContext(
            Input: string.Empty,
            WorkingDirectory: "/srv/app",
            ShellKind: "bash",
            ProfileId: "profile-1",
            IncludePathSuggestions: false);

        // Five runs of the same old command, and one run of a fresh one - the shape of his history.
        var history = Enumerable
            .Range(0, 5)
            .Select(i => CreateEntry(
                "vim .env",
                executedAt: now.AddDays(-4).AddMinutes(i),
                workingDirectory: "/root/apps/agent1"))
            .Append(CreateEntry(
                "docker compose up -d",
                executedAt: now.AddSeconds(-20),
                workingDirectory: "/srv/app"))
            .ToArray();

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(history, context, maxResults: 5);

        Assert.Equal("docker compose up -d", results[0].DisplayText);
        Assert.Equal("vim .env", results[1].DisplayText);
    }

    /// <summary>
    /// Frequency survives as a tiebreak between two rows of the same age, which is all it is now for.
    /// </summary>
    [Fact]
    public void GetSuggestions_WithAnEmptyQuery_UsesFrequencyOnlyToBreakARecencyTie()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        DateTimeOffset sameMoment = now.AddMinutes(-5);
        var engine = new CommandAssistSuggestionEngine(clock: () => now);
        var context = new CommandAssistQueryContext(
            Input: string.Empty,
            WorkingDirectory: "/srv/app",
            ShellKind: "bash",
            ProfileId: "profile-1",
            IncludePathSuggestions: false);

        var history = new[]
        {
            CreateEntry("make test", executedAt: sameMoment, workingDirectory: "/srv/app"),
            CreateEntry("make build", executedAt: sameMoment.AddMinutes(-30), workingDirectory: "/srv/app"),
            CreateEntry("make test", executedAt: sameMoment, workingDirectory: "/srv/app")
        };

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(history, context, maxResults: 5);

        Assert.Equal("make test", results[0].DisplayText);
        Assert.Equal(2, results[0].Frequency);
    }

    /// <summary>
    /// The same-directory bonus is worth half an hour of recency: enough to lift the directory's own
    /// recent work, not enough to bury something the user ran moments ago somewhere else.
    /// </summary>
    /// <remarks>
    /// Both halves are asserted deliberately. A cwd <em>band</em> would satisfy the first and fail the
    /// second, and would reproduce the owner's complaint with a different signal in the driving seat -
    /// see <c>CommandAssistSuggestionEngine.EmptyQuerySameDirectoryRecencyBonus</c>.
    /// </remarks>
    [Fact]
    public void GetSuggestions_WithAnEmptyQuery_TreatsSameDirectoryAsARecencyBonusNotABand()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var engine = new CommandAssistSuggestionEngine(clock: () => now);
        var context = new CommandAssistQueryContext(
            Input: string.Empty,
            WorkingDirectory: "/srv/app",
            ShellKind: "bash",
            ProfileId: "profile-1",
            IncludePathSuggestions: false);

        // The same-dir entry is older, but by less than the bonus, so it wins.
        var lifts = new[]
        {
            CreateEntry("npm run dev", executedAt: now.AddMinutes(-20), workingDirectory: "/srv/app"),
            CreateEntry("tail -f /var/log/syslog", executedAt: now.AddMinutes(-5), workingDirectory: "/var/log")
        };

        Assert.Equal("npm run dev", engine.GetSuggestions(lifts, context, maxResults: 5)[0].DisplayText);

        // The same-dir entry is older by more than the bonus, so recency wins and the bonus does not
        // become a band.
        var doesNotLift = new[]
        {
            CreateEntry("npm run dev", executedAt: now.AddHours(-3), workingDirectory: "/srv/app"),
            CreateEntry("tail -f /var/log/syslog", executedAt: now.AddMinutes(-5), workingDirectory: "/var/log")
        };

        Assert.Equal("tail -f /var/log/syslog", engine.GetSuggestions(doesNotLift, context, maxResults: 5)[0].DisplayText);
    }

    /// <summary>
    /// The badges explain the placement, in the order the sort applied the signals.
    /// </summary>
    [Fact]
    public void GetSuggestions_BadgesTheRowWithWhatPutItThere()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var engine = new CommandAssistSuggestionEngine(clock: () => now);
        var context = new CommandAssistQueryContext(
            Input: string.Empty,
            WorkingDirectory: "/srv/app",
            ShellKind: "bash",
            ProfileId: "profile-1",
            IncludePathSuggestions: false);

        var history = new[]
        {
            CreateEntry("docker ps", executedAt: now.AddMinutes(-2), workingDirectory: "/srv/app"),
            CreateEntry("docker ps", executedAt: now.AddMinutes(-3), workingDirectory: "/srv/app"),
            CreateEntry("apt update", executedAt: now.AddDays(-2), workingDirectory: "/etc")
        };

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(history, context, maxResults: 5);

        AssistSuggestion recent = Assert.Single(results, x => x.DisplayText == "docker ps");
        Assert.Equal(["Recent", "Same dir", "Frequent", "Worked"], recent.Badges);

        AssistSuggestion old = Assert.Single(results, x => x.DisplayText == "apt update");
        Assert.DoesNotContain("Recent", old.Badges);
        Assert.DoesNotContain("Same dir", old.Badges);
    }

    // ---------------------------------------------- UX-polish round: typos are never suggested

    /// <summary>
    /// The owner typed <c>gti status</c>, it was captured, and the terminal offered it straight back.
    /// </summary>
    /// <remarks>
    /// <strong>This is the mutation check for the invalid-command flag.</strong> Dropping the
    /// <c>Where(x =&gt; !x.IsInvalidCommand)</c> filter in <c>BuildHistorySuggestions</c> makes
    /// <c>gti status</c> the top suggestion for <c>gt</c> again and fails here.
    /// </remarks>
    [Fact]
    public void GetSuggestions_NeverOffersACommandTheShellCouldNotResolve()
    {
        var engine = new CommandAssistSuggestionEngine();
        var context = new CommandAssistQueryContext(
            Input: "gt",
            WorkingDirectory: @"C:\repo",
            ShellKind: "pwsh",
            ProfileId: "profile-1",
            IncludePathSuggestions: false);

        var history = new[]
        {
            CreateEntry("gti status", exitCode: 127, isInvalidCommand: true),
            CreateEntry("git status", exitCode: 0)
        };

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(history, context, maxResults: 5);

        Assert.DoesNotContain(results, x => x.DisplayText == "gti status");
        Assert.Contains(results, x => x.DisplayText == "git status");
    }

    /// <summary>
    /// Excluded on every path, including an explicit search for the typo's own text.
    /// </summary>
    /// <remarks>
    /// The alternative - hide it in the passive bubble, keep it in an explicit <c>Ctrl+R</c> search -
    /// was considered and rejected. There is no query for which the honest answer is a misspelling,
    /// and a user who types <c>gti</c> looking for what went wrong wants the scrollback, not an offer
    /// to run it again.
    /// </remarks>
    [Fact]
    public void GetSuggestions_ExcludesInvalidCommandsFromExplicitSearchToo()
    {
        var engine = new CommandAssistSuggestionEngine();
        var context = new CommandAssistQueryContext(
            Input: "gti",
            WorkingDirectory: @"C:\repo",
            ShellKind: "pwsh",
            ProfileId: "profile-1",
            IncludePathSuggestions: false);

        var history = new[] { CreateEntry("gti status", exitCode: 127, isInvalidCommand: true) };

        Assert.Empty(engine.GetSuggestions(history, context, maxResults: 5));
    }

    /// <summary>
    /// An unflagged entry with the same text still ranks: the filter is per entry, not per command
    /// name, so installing the thing you kept mistyping brings it back.
    /// </summary>
    [Fact]
    public void GetSuggestions_WhenTheSameTextLaterSucceeds_OffersTheGoodEntry()
    {
        var engine = new CommandAssistSuggestionEngine();
        var context = new CommandAssistQueryContext(
            Input: "gti",
            WorkingDirectory: @"C:\repo",
            ShellKind: "pwsh",
            ProfileId: "profile-1",
            IncludePathSuggestions: false);

        var history = new[]
        {
            CreateEntry("gti status", exitCode: 127, isInvalidCommand: true),
            CreateEntry("gti status", exitCode: 0)
        };

        Assert.Contains(engine.GetSuggestions(history, context, maxResults: 5), x => x.DisplayText == "gti status");
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

    /// <summary>
    /// The PR #290 review's third blocker, and the decision it forced: an unpinned snippet sits in the
    /// same-profile band on the empty-query path - above every cross-context history row, below the rows
    /// this pane's own context produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The numbers are asserted rather than described because the band is the whole point: 50 local rows
    /// score ~1202 each (1 + frequency + 1000 context + 200 profile), the snippet 202, and the row from
    /// another host 2. So the snippet lands at index 50, immediately after the local rows and
    /// immediately before the foreign one. Under the <c>+8</c> nudge this replaced it scored 10 and lost
    /// to nothing except that foreign row - which is the state the review found.
    /// </para>
    /// <para>
    /// <c>maxResults: 60</c> is deliberate and is the honest part of this decision: with a display cap
    /// below the number of same-context rows, an unpinned snippet is below the fold. Pinning is the
    /// answer to that (<c>GetSuggestions_WithNoQuery_KeepsPinnedSnippetsAboveContextMatchedHistory</c>),
    /// and the test says so rather than choosing a cap that hides the trade-off.
    /// </para>
    /// </remarks>
    [Fact]
    public void GetSuggestions_WithNoQuery_RanksUnpinnedSnippetsBelowThisContextsHistoryAndAboveTheRest()
    {
        var engine = new CommandAssistSuggestionEngine(new FakePathSuggestionProvider(Array.Empty<AssistSuggestion>()));
        var context = new CommandAssistQueryContext(
            Input: string.Empty,
            WorkingDirectory: @"C:\repo",
            ShellKind: "pwsh",
            ProfileId: "profile-1");

        List<CommandHistoryEntry> history = Enumerable.Range(0, 50)
            .Select(i => CreateEntry(
                $"dotnet build --project p{i}",
                executedAt: DateTimeOffset.Parse("2026-03-01T12:00:00+00:00").AddMinutes(i)))
            .ToList();
        history.Add(CreateRemoteEntry("apt update", "other.example", DateTimeOffset.Parse("2026-03-01T13:00:00+00:00")));

        var snippets = new[]
        {
            CreateSnippet("Deploy", "./deploy.sh --prod", isPinned: false)
        };

        IReadOnlyList<AssistSuggestion> results = engine.GetSuggestions(history, snippets, context, maxResults: 60);

        int snippetIndex = IndexOfDisplayText(results, "Deploy");
        int foreignIndex = IndexOfDisplayText(results, "apt update");

        Assert.Equal(52, results.Count);
        Assert.Equal(50, snippetIndex);
        Assert.Equal(51, foreignIndex);
        Assert.All(
            results.Take(snippetIndex),
            row => Assert.Equal(AssistSuggestionType.History, row.Type));
    }

    private static int IndexOfDisplayText(IReadOnlyList<AssistSuggestion> results, string displayText)
    {
        for (int i = 0; i < results.Count; i++)
        {
            if (string.Equals(results[i].DisplayText, displayText, StringComparison.Ordinal))
            {
                return i;
            }
        }

        Assert.Fail($"'{displayText}' is not in the list at all: [{string.Join(", ", results.Select(x => x.DisplayText))}]");
        return -1;
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
        int? exitCode = 0,
        string? workingDirectory = @"C:\repo",
        bool isInvalidCommand = false)
    {
        return new CommandHistoryEntry(
            Id: Guid.NewGuid().ToString("N"),
            CommandText: commandText,
            ExecutedAt: executedAt ?? DateTimeOffset.Parse("2026-03-01T10:00:00+00:00"),
            ShellKind: "pwsh",
            WorkingDirectory: workingDirectory,
            ProfileId: profileId,
            SessionId: "session-1",
            HostId: null,
            ExitCode: exitCode,
            IsRemote: false,
            IsRedacted: false,
            Source: CommandCaptureSource.Heuristic,
            DurationMs: null,
            IsInvalidCommand: isInvalidCommand);
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

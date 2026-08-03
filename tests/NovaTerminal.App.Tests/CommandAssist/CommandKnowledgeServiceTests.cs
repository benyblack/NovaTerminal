using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.Tests.CommandAssist;

/// <summary>
/// Lookup, normalization and probing behavior of <see cref="CommandKnowledgeService"/>
/// (V2 Phase 4b, Phase 4 task 3; closes #250).
/// </summary>
/// <remarks>
/// Two halves, deliberately. The first runs against the real embedded catalogue, because "Help is
/// useful for arbitrary common commands" is a claim about the shipped asset and a synthetic
/// catalogue could not test it - this is the migrated intent of
/// <c>LocalCommandDocsProviderTests</c> and <c>SeedRecipeProviderTests</c>, which asserted the same
/// shapes against seven hard-coded commands. The second uses the internal catalogue seam for the
/// edges (an empty catalogue, a throwing probe) that the real asset cannot be made to produce.
/// </remarks>
public sealed class CommandKnowledgeServiceTests
{
    [Fact]
    public async Task GetHelpAsync_WhenCommandIsInTheCatalogue_ReturnsOneDocRow()
    {
        var service = new CommandKnowledgeService();

        IReadOnlyList<CommandHelpItem> help = await service.GetHelpAsync(Query("ssh user@host"), CancellationToken.None);

        CommandHelpItem item = Assert.Single(help);
        Assert.Equal("ssh", item.Title);
        Assert.False(string.IsNullOrWhiteSpace(item.Description));
        Assert.Contains("Doc", item.Badges!);
    }

    [Fact]
    public async Task GetRecipesAsync_WhenCommandIsInTheCatalogue_ReturnsInsertableExamples()
    {
        var service = new CommandKnowledgeService();

        IReadOnlyList<CommandHelpItem> recipes = await service.GetRecipesAsync(Query("ssh"), CancellationToken.None);

        Assert.NotEmpty(recipes);
        Assert.All(recipes, item =>
        {
            // The row's display text *is* the command it inserts. The seed provider showed a prose
            // title over a command the user could not see; a row that says what Enter will do cannot
            // mislead about it.
            Assert.Equal(item.Title, item.Command);
            Assert.Contains("Recipe", item.Badges!);
        });
        Assert.Contains(recipes, item => item.Command.StartsWith("ssh ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetHelpAsync_WhenCommandIsUnknown_ReturnsEmptyList()
    {
        var service = new CommandKnowledgeService();

        IReadOnlyList<CommandHelpItem> help = await service.GetHelpAsync(
            Query("frobnicate --all"),
            CancellationToken.None);

        Assert.Empty(help);
    }

    [Fact]
    public async Task Lookup_prefers_the_two_token_git_subcommand_over_git_itself()
    {
        var service = new CommandKnowledgeService();

        IReadOnlyList<CommandHelpItem> help = await service.GetHelpAsync(
            Query("git rebase --onto main"),
            CancellationToken.None);

        Assert.Equal("git rebase", Assert.Single(help).Title);
    }

    [Fact]
    public async Task Lookup_falls_back_to_git_when_the_second_token_is_an_option()
    {
        var service = new CommandKnowledgeService();

        IReadOnlyList<CommandHelpItem> help = await service.GetHelpAsync(
            Query("git --version"),
            CancellationToken.None);

        Assert.Equal("git", Assert.Single(help).Title);
    }

    [Fact]
    public async Task Lookup_falls_back_to_git_when_the_subcommand_is_not_in_the_catalogue()
    {
        var service = new CommandKnowledgeService();

        IReadOnlyList<CommandHelpItem> help = await service.GetHelpAsync(
            Query("git frobnicate --all"),
            CancellationToken.None);

        Assert.Equal("git", Assert.Single(help).Title);
    }

    /// <summary>
    /// PowerShell is case-insensitive and a user typing at speed will not shift-case a cmdlet.
    /// </summary>
    [Theory]
    [InlineData("get-childitem")]
    [InlineData("Get-ChildItem")]
    [InlineData("GET-CHILDITEM")]
    public async Task Lookup_is_case_insensitive_for_powershell_cmdlets(string typed)
    {
        var service = new CommandKnowledgeService();

        IReadOnlyList<CommandHelpItem> help = await service.GetHelpAsync(
            Query(typed, shellKind: "pwsh"),
            CancellationToken.None);

        CommandHelpItem item = Assert.Single(help);
        Assert.Equal("Get-ChildItem", item.Title);

        // The shell hint travels with the entry: this is where SeedRecipeProviderTests'
        // "prefers shell-specific recipes" intent lives now.
        Assert.Equal("pwsh", item.ShellKind);
    }

    [Theory]
    [InlineData("/usr/bin/ssh user@host", "ssh")]
    [InlineData(@".\git.exe status", "git status")]
    [InlineData("\"ssh\" user@host", "ssh")]
    [InlineData("sudo systemctl restart nginx", "systemctl")]
    [InlineData("sudo -u www tar -xzf archive.tgz", "tar")]
    public async Task Lookup_normalizes_paths_suffixes_quotes_and_wrappers(string typed, string expectedToken)
    {
        var service = new CommandKnowledgeService();

        IReadOnlyList<CommandHelpItem> help = await service.GetHelpAsync(
            Query(typed),
            CancellationToken.None);

        Assert.Equal(expectedToken, Assert.Single(help).Title);
    }

    [Fact]
    public async Task Lookup_uses_the_selection_when_there_is_no_query_text()
    {
        // The degraded-session contract: Help on a selection in a markless pane has no command line
        // to read, and the selection is the whole of what the user pointed at.
        var service = new CommandKnowledgeService();

        IReadOnlyList<CommandHelpItem> help = await service.GetHelpAsync(
            new CommandHelpQuery(
                RawInput: string.Empty,
                CommandToken: "git",
                ShellKind: "bash",
                WorkingDirectory: null,
                SelectedText: "git stash pop",
                SessionId: null),
            CancellationToken.None);

        Assert.Equal("git stash", Assert.Single(help).Title);
    }

    [Fact]
    public async Task Whitespace_between_the_two_tokens_does_not_change_the_lookup()
    {
        var service = new CommandKnowledgeService();

        IReadOnlyList<CommandHelpItem> help = await service.GetHelpAsync(
            Query("git    rebase"),
            CancellationToken.None);

        Assert.Equal("git rebase", Assert.Single(help).Title);
    }

    [Fact]
    public async Task Probe_row_is_appended_after_the_catalogue_rows()
    {
        var probe = new FakeProbe(new CommandHelpProbeResult("man ssh", "Open the manual page."));
        var service = new CommandKnowledgeService(probe);

        IReadOnlyList<CommandHelpItem> recipes = await service.GetRecipesAsync(
            Query("ssh"),
            CancellationToken.None);

        // Last, because a curated example is a better answer than a manual page when both exist.
        CommandHelpItem last = recipes[^1];
        Assert.Equal("Open full help: man ssh", last.Title);
        Assert.Equal("man ssh", last.Command);
        Assert.Contains("Help", last.Badges!);
        Assert.True(recipes.Count > 1);
    }

    [Fact]
    public async Task Probe_row_is_offered_even_when_the_catalogue_knows_nothing()
    {
        // The point of having a second source: a command the catalogue never heard of still has
        // --help on the machine in front of the user.
        var probe = new FakeProbe(new CommandHelpProbeResult("frobnicate --help", "Print help."));
        var service = new CommandKnowledgeService(probe);

        IReadOnlyList<CommandHelpItem> recipes = await service.GetRecipesAsync(
            Query("frobnicate --all"),
            CancellationToken.None);

        CommandHelpItem item = Assert.Single(recipes);
        Assert.Equal("frobnicate --help", item.Command);
        Assert.Equal("frobnicate", probe.LastToken);
    }

    [Fact]
    public async Task Probe_is_asked_about_the_catalogue_token_not_the_raw_line()
    {
        var probe = new FakeProbe(new CommandHelpProbeResult("man git-rebase", "Manual page."));
        var service = new CommandKnowledgeService(probe);

        await service.GetRecipesAsync(Query("git rebase --onto main"), CancellationToken.None);

        Assert.Equal("git rebase", probe.LastToken);
    }

    [Fact]
    public async Task No_probe_means_no_extra_row()
    {
        var service = new CommandKnowledgeService();

        IReadOnlyList<CommandHelpItem> recipes = await service.GetRecipesAsync(
            Query("ssh"),
            CancellationToken.None);

        Assert.DoesNotContain(recipes, item => item.Badges!.Contains("Help"));
    }

    [Fact]
    public async Task A_throwing_probe_does_not_cost_the_catalogue_rows()
    {
        var service = new CommandKnowledgeService(new ThrowingProbe());

        IReadOnlyList<CommandHelpItem> recipes = await service.GetRecipesAsync(
            Query("ssh"),
            CancellationToken.None);

        Assert.NotEmpty(recipes);
        Assert.DoesNotContain(recipes, item => item.Badges!.Contains("Help"));
    }

    [Fact]
    public async Task An_unreadable_catalogue_degrades_to_empty_rather_than_throwing()
    {
        var service = new CommandKnowledgeService(probe: null, catalogueFactory: () => null);

        Assert.Empty(await service.GetHelpAsync(Query("ssh"), CancellationToken.None));
        Assert.Empty(await service.GetRecipesAsync(Query("ssh"), CancellationToken.None));
        Assert.Null(service.Attribution);
    }

    [Fact]
    public async Task Attribution_is_available_once_the_catalogue_has_been_read()
    {
        var service = new CommandKnowledgeService();

        // Null before the first lookup: the line is the asset's own field, so it does not exist
        // until the asset has been parsed. The Help path always reads it after the lookups.
        Assert.Null(service.Attribution);

        await service.GetHelpAsync(Query("ssh"), CancellationToken.None);

        Assert.Contains("tldr-pages", service.Attribution!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Entries_without_a_token_are_ignored_rather_than_indexed()
    {
        var catalogue = new CommandKnowledgeCatalogue(
            Version: 1,
            License: null,
            LicenseUrl: null,
            Attribution: null,
            GeneratedFrom: null,
            Entries:
            [
                new CommandKnowledgeEntry("   ", "blank", null, null, [new CommandKnowledgeExample("x", "y")]),
                new CommandKnowledgeEntry("real", "A real one.", null, null, [new CommandKnowledgeExample("real -v", "Verbose")])
            ]);

        var service = new CommandKnowledgeService(probe: null, catalogueFactory: () => catalogue);

        Assert.Equal(1, service.Count);
        Assert.Equal("real", Assert.Single(await service.GetHelpAsync(Query("real"), CancellationToken.None)).Title);
    }

    private static CommandHelpQuery Query(string rawInput, string? shellKind = "bash")
    {
        // CommandToken is what RecognizedCommandParser would have produced: the first word. Supplied
        // as it is in production so the tests exercise the same precedence the controller feeds in.
        string? token = rawInput.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return new CommandHelpQuery(
            RawInput: rawInput,
            CommandToken: token,
            ShellKind: shellKind,
            WorkingDirectory: null,
            SelectedText: null,
            SessionId: null);
    }

    private sealed class FakeProbe : ICommandHelpProbe
    {
        private readonly CommandHelpProbeResult? _result;

        public FakeProbe(CommandHelpProbeResult? result)
        {
            _result = result;
        }

        public string? LastToken { get; private set; }

        public CommandHelpProbeResult? Probe(string commandToken, string? shellKind)
        {
            LastToken = commandToken;
            return _result;
        }
    }

    private sealed class ThrowingProbe : ICommandHelpProbe
    {
        public CommandHelpProbeResult? Probe(string commandToken, string? shellKind)
            => throw new InvalidOperationException("PATH is on fire.");
    }
}

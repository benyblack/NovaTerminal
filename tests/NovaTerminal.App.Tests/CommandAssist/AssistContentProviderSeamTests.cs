using NovaTerminal.CommandAssist.Application;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.Providers;
using NovaTerminal.CommandAssist.Providers.Local;

namespace NovaTerminal.Tests.CommandAssist;

/// <summary>
/// The V2 Phase 5 AI content-provider seam: the redaction guarantee, the registry's policy and
/// failure containment, the two local adapters, and the controller running Help and Fix through all
/// of it.
/// </summary>
public sealed class AssistContentProviderSeamTests
{
    // ---------------------------------------------------------------------------------------
    // The redaction-before-seam guarantee.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The mutation anchor for the whole phase. Make <c>AssistContentRequestFactory</c> skip the
    /// filter - or make <c>RedactedText.Redact</c> wrap the raw string instead of the filtered one -
    /// and this fails.
    /// </summary>
    [Fact]
    public void RequestFactory_RedactsEveryFreeTextFieldBeforeAProviderCanSeeIt()
    {
        var factory = new AssistContentRequestFactory(new SecretsFilter());

        AssistContentRequest request = factory.CreateFixRequest(
            new CommandFailureContext(
                CommandText: "mysql -u root --password hunter2 mydb",
                ExitCode: 1,
                ShellKind: "bash",
                WorkingDirectory: "/srv/app?token=cwdsecret",
                OutputTail: "curl -H 'Authorization: Bearer eyJhbGciOi.payload.sig' failed",
                IsRemote: false,
                SelectedText: "sshpass -p letmein ssh host"),
            sessionId: "session-1");

        Assert.DoesNotContain("hunter2", request.CommandText.Value, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", request.CommandText.Value, StringComparison.Ordinal);
        Assert.True(request.CommandText.WasRedacted);

        Assert.DoesNotContain("eyJhbGciOi.payload.sig", request.OutputTail!.Value, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", request.OutputTail!.Value, StringComparison.Ordinal);

        Assert.DoesNotContain("letmein", request.SelectedText!.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("cwdsecret", request.WorkingDirectory!.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// The pane already redacts the output tail at the VT boundary. The factory redacts again, so the
    /// guarantee does not depend on the caller having remembered - and redaction is idempotent, so
    /// the second pass changes nothing.
    /// </summary>
    [Fact]
    public void RequestFactory_WhenTextIsAlreadyRedacted_IsIdempotent()
    {
        var factory = new AssistContentRequestFactory(new SecretsFilter());
        const string alreadyRedacted = "mysql -u root --password [REDACTED] mydb";

        AssistContentRequest request = factory.CreateFixRequest(
            NewFailure(commandText: alreadyRedacted));

        Assert.Equal(alreadyRedacted, request.CommandText.Value);
    }

    [Fact]
    public void RequestFactory_WhenNothingMatched_ReportsTextAsUnredacted()
    {
        var factory = new AssistContentRequestFactory(new SecretsFilter());

        AssistContentRequest request = factory.CreateFixRequest(NewFailure(commandText: "git status"));

        Assert.Equal("git status", request.CommandText.Value);
        Assert.False(request.CommandText.WasRedacted);
    }

    [Fact]
    public void RequestFactory_WhenTextIsAbsent_LeavesTheFieldNullRatherThanEmpty()
    {
        var factory = new AssistContentRequestFactory(new SecretsFilter());

        AssistContentRequest request = factory.CreateHelpRequest(
            AssistCapabilities.EnrichDocs,
            commandText: "ssh",
            commandToken: "ssh",
            shellKind: "pwsh",
            workingDirectory: null,
            selectedText: null,
            sessionId: null);

        Assert.Null(request.WorkingDirectory);
        Assert.Null(request.SelectedText);
        Assert.Null(request.OutputTail);
        Assert.Equal("ssh", request.CommandToken!.Value);
    }

    /// <summary>A request asks one question, so a composite capability has no single answer.</summary>
    [Fact]
    public void RequestFactory_WhenCapabilityIsComposite_Throws()
    {
        var factory = new AssistContentRequestFactory(new SecretsFilter());

        Assert.Throws<ArgumentException>(() => factory.CreateHelpRequest(
            AssistCapabilities.EnrichDocs | AssistCapabilities.Explain,
            commandText: "ls",
            commandToken: "ls",
            shellKind: null,
            workingDirectory: null,
            selectedText: null,
            sessionId: null));
    }

    [Fact]
    public void RedactedText_HasNoPublicWayToWrapRawText()
    {
        Type type = typeof(RedactedText);

        // Every constructor is private, and the one factory takes an ISecretsFilter. There is no
        // path from string to RedactedText that does not run a filter.
        Assert.Empty(type.GetConstructors());
        Assert.Empty(type.GetMethods().Where(m =>
            m.IsStatic && m.IsPublic && !m.IsSpecialName && m.ReturnType == typeof(RedactedText)));
        Assert.Empty(type.GetMethods().Where(m => m.Name is "op_Implicit" or "op_Explicit"));
    }

    // ---------------------------------------------------------------------------------------
    // Registry: order, containment, policy, empty states.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Registry_QueriesEnabledProvidersInRegistrationOrder()
    {
        var first = new RecordingProvider("p.first", AssistCapabilities.EnrichDocs, docs: [Doc("first")]);
        var second = new RecordingProvider("p.second", AssistCapabilities.EnrichDocs, docs: [Doc("second")]);
        var registry = new AssistContentProviderRegistry([first, second]);

        IReadOnlyList<AssistContentResult> results = await registry.QueryAsync(HelpRequest());

        Assert.Equal(new[] { "p.first", "p.second" }, results.Select(r => r.ProviderId).ToArray());
    }

    [Fact]
    public async Task Registry_SkipsProvidersThatDoNotDeclareTheCapability()
    {
        var docs = new RecordingProvider("p.docs", AssistCapabilities.EnrichDocs, docs: [Doc("d")]);
        var fixer = new RecordingProvider("p.fix", AssistCapabilities.SuggestFix, fixes: [Fix("f", 0.9)]);
        var registry = new AssistContentProviderRegistry([docs, fixer]);

        await registry.QueryAsync(HelpRequest());

        Assert.Equal(1, docs.QueryCount);
        Assert.Equal(0, fixer.QueryCount);
    }

    /// <summary>
    /// A future provider's failure modes are a timeout, a 500 and a rate limit. None of them may take
    /// down a popup that is showing perfectly good local rows.
    /// </summary>
    [Fact]
    public async Task Registry_WhenAProviderThrows_TheOthersStillAnswer()
    {
        var thrower = new ThrowingProvider("p.broken", AssistCapabilities.EnrichDocs);
        var healthy = new RecordingProvider("p.ok", AssistCapabilities.EnrichDocs, docs: [Doc("still here")]);
        var registry = new AssistContentProviderRegistry([thrower, healthy]);

        IReadOnlyList<AssistContentResult> results = await registry.QueryAsync(HelpRequest());

        AssistContentResult only = Assert.Single(results);
        Assert.Equal("p.ok", only.ProviderId);
    }

    [Fact]
    public async Task Registry_WhenTheCallerCancels_TheCancellationIsNotSwallowed()
    {
        var registry = new AssistContentProviderRegistry(
            [new RecordingProvider("p.docs", AssistCapabilities.EnrichDocs, docs: [Doc("d")])]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => registry.QueryAsync(HelpRequest(), cts.Token));
    }

    /// <summary>
    /// Opt-in is an obligation the provider declares, not a settings key it can be registered around.
    /// </summary>
    [Fact]
    public async Task Registry_WhenAProviderRequiresOptIn_ItIsNotQueriedUntilThePolicyNamesIt()
    {
        var remote = new RecordingProvider(
            "acme.cloud",
            AssistCapabilities.SuggestFix,
            fixes: [Fix("cloud fix", 0.95)])
        {
            RequiresOptIn = true
        };

        var closed = new AssistContentProviderRegistry([remote]);
        Assert.False(closed.HasProviderFor(AssistCapabilities.SuggestFix));
        Assert.Empty(await closed.QueryAsync(FixRequest()));
        Assert.Equal(0, remote.QueryCount);

        var opened = new AssistContentProviderRegistry(
            [remote],
            new AssistProviderPolicy(new Dictionary<AssistCapabilities, IReadOnlyList<string>>
            {
                [AssistCapabilities.SuggestFix] = ["acme.cloud"]
            }));

        Assert.True(opened.HasProviderFor(AssistCapabilities.SuggestFix));
        Assert.Single(await opened.QueryAsync(FixRequest()));
        Assert.Equal(1, remote.QueryCount);
    }

    /// <summary>An opt-in for one capability is not an opt-in for another.</summary>
    [Fact]
    public void Policy_OptInIsPerCapability()
    {
        var remote = new RecordingProvider(
            "acme.cloud",
            AssistCapabilities.SuggestFix | AssistCapabilities.EnrichDocs)
        {
            RequiresOptIn = true
        };

        var policy = new AssistProviderPolicy(new Dictionary<AssistCapabilities, IReadOnlyList<string>>
        {
            [AssistCapabilities.SuggestFix] = ["acme.cloud"]
        });

        Assert.True(policy.IsEnabled(remote, AssistCapabilities.SuggestFix));
        Assert.False(policy.IsEnabled(remote, AssistCapabilities.EnrichDocs));
    }

    /// <summary>
    /// A local provider is the feature, not an add-on: the policy has no way to switch it off, which
    /// is the deliberate limit on the reserved settings shape.
    /// </summary>
    [Fact]
    public void Policy_CannotDisableALocalProvider()
    {
        var local = new RecordingProvider("local.thing", AssistCapabilities.EnrichDocs);
        var policy = new AssistProviderPolicy(new Dictionary<AssistCapabilities, IReadOnlyList<string>>
        {
            [AssistCapabilities.EnrichDocs] = []
        });

        Assert.True(policy.IsEnabled(local, AssistCapabilities.EnrichDocs));
    }

    [Fact]
    public void Registry_WithNoProviders_AnswersNothingForEveryCapability()
    {
        var registry = new AssistContentProviderRegistry();

        Assert.False(registry.HasProviderFor(AssistCapabilities.EnrichDocs));
        Assert.False(registry.HasProviderFor(AssistCapabilities.SuggestFix));
        Assert.False(registry.HasProviderFor(AssistCapabilities.NlToCommand));
        Assert.False(registry.HasProviderFor(AssistCapabilities.Explain));
    }

    [Theory]
    [InlineData(AssistCapabilities.EnrichDocs, AssistEmptyStates.NoHelpProvider)]
    [InlineData(AssistCapabilities.SuggestFix, AssistEmptyStates.NoFixProvider)]
    [InlineData(AssistCapabilities.NlToCommand, AssistEmptyStates.AiNotConfigured)]
    [InlineData(AssistCapabilities.Explain, AssistEmptyStates.AiNotConfigured)]
    public void EmptyStates_SayWhichProviderIsMissing(AssistCapabilities capability, string expected)
    {
        Assert.Equal(expected, AssistEmptyStates.ForMissingProvider(capability));
    }

    // ---------------------------------------------------------------------------------------
    // The two local adapters.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task LocalErrorInsightProvider_HandsTheHeuristicsTheRedactedTextAndNothingElse()
    {
        var service = new RecordingErrorInsightService();
        var provider = new LocalErrorInsightProvider(service);
        var factory = new AssistContentRequestFactory(new SecretsFilter());

        AssistContentRequest request = factory.CreateFixRequest(
            new CommandFailureContext(
                CommandText: "psql --password hunter2",
                ExitCode: 2,
                ShellKind: "zsh",
                WorkingDirectory: "/home/u",
                OutputTail: "authentication failed",
                IsRemote: true,
                SelectedText: null));

        await provider.QueryAsync(request);

        CommandFailureContext seen = Assert.Single(service.Seen);
        Assert.DoesNotContain("hunter2", seen.CommandText, StringComparison.Ordinal);
        Assert.Equal(2, seen.ExitCode);
        Assert.Equal("zsh", seen.ShellKind);
        Assert.True(seen.IsRemote);
        Assert.Equal("authentication failed", seen.OutputTail);
    }

    [Fact]
    public async Task LocalErrorInsightProvider_WhenAskedTheWrongQuestion_AnswersNothing()
    {
        var service = new RecordingErrorInsightService();
        var provider = new LocalErrorInsightProvider(service);

        AssistContentResult result = await provider.QueryAsync(HelpRequest());

        Assert.True(result.IsEmpty);
        Assert.Empty(service.Seen);
    }

    [Fact]
    public async Task LocalCommandKnowledgeProvider_ReturnsDocsRecipesAndAttribution()
    {
        var source = new FakeKnowledgeSource("CC BY-SA 4.0, tldr-pages");
        var provider = new LocalCommandKnowledgeProvider(source, source);

        AssistContentResult result = await provider.QueryAsync(HelpRequest("ssh user@host", "ssh"));

        Assert.Equal(LocalCommandKnowledgeProvider.ProviderId, result.ProviderId);
        Assert.Single(result.Docs);
        Assert.Single(result.Recipes);
        Assert.Equal("CC BY-SA 4.0, tldr-pages", result.Attribution);
        Assert.Equal("ssh", source.LastQuery!.CommandToken);
    }

    /// <summary>
    /// A knowledge provider with no sources can only ever return nothing, and the empty state should
    /// say "nothing is configured" rather than "we looked".
    /// </summary>
    [Fact]
    public void LocalCommandKnowledgeProvider_RefusesToBeConstructedWithNoSources()
    {
        Assert.Throws<ArgumentNullException>(() => new LocalCommandKnowledgeProvider(null, null));
    }

    [Fact]
    public void LocalProviders_DoNotRequireOptIn()
    {
        var source = new FakeKnowledgeSource(null);

        Assert.False(new LocalCommandKnowledgeProvider(source, source).RequiresExplicitOptIn);
        Assert.False(new LocalErrorInsightProvider(new RecordingErrorInsightService()).RequiresExplicitOptIn);
    }

    // ---------------------------------------------------------------------------------------
    // The controller running through the seam.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// End-to-end: a failing command reaches a provider carrying nothing the filter did not see.
    /// </summary>
    [Fact]
    public async Task Controller_WhenACommandFails_TheProviderNeverSeesRawText()
    {
        var recorder = new RecordingProvider(
            "p.fix",
            AssistCapabilities.SuggestFix,
            fixes: [Fix("Try again", 0.9)]);
        CommandAssistController controller = CreateController(new AssistContentProviderRegistry([recorder]));

        bool opened = await controller.HandleCommandFailureAsync(new CommandFailureContext(
            CommandText: "curl -H 'Authorization: Bearer abc.def.ghi' https://x",
            ExitCode: 22,
            ShellKind: "bash",
            WorkingDirectory: "/tmp",
            OutputTail: "sshpass -p swordfish",
            IsRemote: false,
            SelectedText: null));

        AssistContentRequest seen = Assert.Single(recorder.Requests);
        Assert.Equal(AssistCapabilities.SuggestFix, seen.Capability);
        Assert.DoesNotContain("abc.def.ghi", seen.CommandText.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("swordfish", seen.OutputTail!.Value, StringComparison.Ordinal);

        Assert.True(opened);
        Assert.Equal("Fix", controller.ViewModel.ModeLabel);
        Assert.Equal("Try again", controller.ViewModel.TopSuggestionText);
    }

    /// <summary>
    /// The caption above the Fix rows is still the raw command the user ran. The redaction guarantee
    /// is about what leaves for a provider, not about what the user is shown on their own screen -
    /// blanking their own command line would be a different (and worse) product.
    /// </summary>
    [Fact]
    public async Task Controller_WhenACommandFails_TheOnScreenCaptionIsStillTheUsersOwnCommand()
    {
        var recorder = new RecordingProvider("p.fix", AssistCapabilities.SuggestFix, fixes: [Fix("Try again", 0.9)]);
        CommandAssistController controller = CreateController(new AssistContentProviderRegistry([recorder]));

        await controller.HandleCommandFailureAsync(NewFailure(commandText: "mysql --password hunter2"));

        Assert.Equal("mysql --password hunter2", controller.ViewModel.QueryText);
    }

    [Fact]
    public async Task Controller_WhenHelpIsOpened_RowsAndAttributionComeThroughTheSeam()
    {
        var provider = new RecordingProvider(
            "p.docs",
            AssistCapabilities.EnrichDocs,
            docs: [Doc("ssh")],
            recipes: [Doc("ssh -p 2222 user@host")],
            attribution: "CC BY-SA 4.0");
        CommandAssistController controller = CreateController(new AssistContentProviderRegistry([provider]));

        await controller.OpenHelpAsync("ssh user@host");

        Assert.Equal(AssistCapabilities.EnrichDocs, Assert.Single(provider.Requests).Capability);
        Assert.Equal(2, controller.Suggestions.Count);
        Assert.Equal("CC BY-SA 4.0", controller.ViewModel.AttributionText);
        Assert.False(controller.ViewModel.ShowEmptyState);
    }

    /// <summary>Docs from every provider group ahead of recipes from any of them.</summary>
    [Fact]
    public async Task Controller_WhenTwoProvidersAnswerHelp_DocsGroupBeforeRecipes()
    {
        var first = new RecordingProvider("p.a", AssistCapabilities.EnrichDocs, docs: [Doc("doc-a")], recipes: [Doc("recipe-a")]);
        var second = new RecordingProvider("p.b", AssistCapabilities.EnrichDocs, docs: [Doc("doc-b")], recipes: [Doc("recipe-b")]);
        CommandAssistController controller = CreateController(new AssistContentProviderRegistry([first, second]));

        await controller.OpenHelpAsync("x");

        Assert.Equal(
            new[] { "doc-a", "doc-b", "recipe-a", "recipe-b" },
            controller.Suggestions.Select(s => s.DisplayText).ToArray());
    }

    [Fact]
    public async Task Controller_WhenAProviderAnswersHelpWithNothing_SaysWeLooked()
    {
        var provider = new RecordingProvider("p.docs", AssistCapabilities.EnrichDocs);
        CommandAssistController controller = CreateController(new AssistContentProviderRegistry([provider]));

        await controller.OpenHelpAsync("frobnicate");

        Assert.True(controller.ViewModel.ShowEmptyState);
        Assert.Equal(AssistEmptyStates.NoLocalHelp, controller.ViewModel.EmptyStateText);
    }

    /// <summary>
    /// "Nothing is configured to answer this" is a different sentence from "we looked and found
    /// nothing", and this is the case the three deleted <c>Empty*Provider</c> stubs used to hide.
    /// </summary>
    [Fact]
    public async Task Controller_WhenNothingIsConfiguredForHelp_SaysSo()
    {
        CommandAssistController controller = CreateController(new AssistContentProviderRegistry());

        await controller.OpenHelpAsync("ssh");

        Assert.False(controller.HasContentProviderFor(AssistCapabilities.EnrichDocs));
        Assert.True(controller.ViewModel.ShowEmptyState);
        Assert.Equal(AssistEmptyStates.NoHelpProvider, controller.ViewModel.EmptyStateText);
    }

    /// <summary>
    /// The default composition path: the controller wraps whichever legacy services it was handed,
    /// so the pane's constructor call keeps meaning what it meant.
    /// </summary>
    [Fact]
    public async Task Controller_WhenGivenLegacyServices_WrapsThemAsProviders()
    {
        var knowledge = new FakeKnowledgeSource("credit line");
        CommandAssistController controller = CreateController(
            contentProviders: null,
            docsProvider: knowledge,
            recipeProvider: knowledge,
            errorInsightService: new StubErrorInsightService([Fix("did you mean git?", 0.9)]));

        Assert.True(controller.HasContentProviderFor(AssistCapabilities.EnrichDocs));
        Assert.True(controller.HasContentProviderFor(AssistCapabilities.SuggestFix));
        Assert.False(controller.HasContentProviderFor(AssistCapabilities.NlToCommand));

        await controller.OpenHelpAsync("ssh");
        Assert.Equal(2, controller.Suggestions.Count);
        Assert.Equal("credit line", controller.ViewModel.AttributionText);

        Assert.True(await controller.HandleCommandFailureAsync(NewFailure(commandText: "gti status")));
        Assert.Equal("did you mean git?", controller.ViewModel.TopSuggestionText);
    }

    /// <summary>
    /// The shipped Fix path, unchanged: the real recogniser table reached through the seam still
    /// corrects the single most recognisable typo in the world.
    /// </summary>
    [Fact]
    public async Task Controller_WithTheRealHeuristics_StillFixesGtiThroughTheSeam()
    {
        CommandAssistController controller = CreateController(
            contentProviders: null,
            errorInsightService: new HeuristicErrorInsightService());

        bool opened = await controller.HandleCommandFailureAsync(new CommandFailureContext(
            CommandText: "gti status",
            ExitCode: 127,
            ShellKind: "bash",
            WorkingDirectory: "/repo",
            OutputTail: "command not found: gti",
            IsRemote: false,
            SelectedText: null));

        Assert.True(opened);
        Assert.Equal("Fix", controller.ViewModel.ModeLabel);
        Assert.Contains(controller.Suggestions, s => s.InsertText.StartsWith("git ", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------

    private static CommandAssistController CreateController(
        AssistContentProviderRegistry? contentProviders,
        ICommandDocsProvider? docsProvider = null,
        IRecipeProvider? recipeProvider = null,
        IErrorInsightService? errorInsightService = null)
    {
        return new CommandAssistController(
            new NullHistoryStore(),
            new SecretsFilter(),
            new NullSuggestionEngine(),
            snippetStore: null,
            commandDocsProvider: docsProvider,
            recipeProvider: recipeProvider,
            errorInsightService: errorInsightService,
            modeRouter: null,
            resultBuilder: null,
            contentProviders: contentProviders);
    }

    private static CommandFailureContext NewFailure(string commandText) => new(
        CommandText: commandText,
        ExitCode: 1,
        ShellKind: "bash",
        WorkingDirectory: null,
        OutputTail: null,
        IsRemote: false,
        SelectedText: null);

    private static AssistContentRequest HelpRequest(string commandText = "ssh", string? token = "ssh")
        => new AssistContentRequestFactory(new SecretsFilter()).CreateHelpRequest(
            AssistCapabilities.EnrichDocs,
            commandText: commandText,
            commandToken: token,
            shellKind: "bash",
            workingDirectory: null,
            selectedText: null,
            sessionId: null);

    private static AssistContentRequest FixRequest()
        => new AssistContentRequestFactory(new SecretsFilter())
            .CreateFixRequest(NewFailure("boom"));

    private static CommandHelpItem Doc(string title)
        => new(Title: title, Command: title, Description: "d", ShellKind: null, Badges: ["Doc"]);

    private static CommandFixSuggestion Fix(string title, double confidence)
        => new(Title: title, SuggestedCommand: "git status", Description: null, Confidence: confidence);

    private sealed class RecordingProvider : IAssistContentProvider
    {
        private readonly IReadOnlyList<CommandFixSuggestion> _fixes;
        private readonly IReadOnlyList<CommandHelpItem> _docs;
        private readonly IReadOnlyList<CommandHelpItem> _recipes;
        private readonly string? _attribution;

        public RecordingProvider(
            string id,
            AssistCapabilities capabilities,
            IReadOnlyList<CommandFixSuggestion>? fixes = null,
            IReadOnlyList<CommandHelpItem>? docs = null,
            IReadOnlyList<CommandHelpItem>? recipes = null,
            string? attribution = null)
        {
            Id = id;
            Capabilities = capabilities;
            _fixes = fixes ?? [];
            _docs = docs ?? [];
            _recipes = recipes ?? [];
            _attribution = attribution;
        }

        public List<AssistContentRequest> Requests { get; } = [];
        public int QueryCount => Requests.Count;
        public string Id { get; }
        public string DisplayName => Id;
        public AssistCapabilities Capabilities { get; }
        public bool RequiresOptIn { get; init; }
        public bool RequiresExplicitOptIn => RequiresOptIn;

        public Task<AssistContentResult> QueryAsync(AssistContentRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new AssistContentResult(
                Id,
                request.Capability,
                fixes: _fixes,
                docs: _docs,
                recipes: _recipes,
                attribution: _attribution));
        }
    }

    private sealed class ThrowingProvider(string id, AssistCapabilities capabilities) : IAssistContentProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public AssistCapabilities Capabilities => capabilities;
        public bool RequiresExplicitOptIn => false;

        public Task<AssistContentResult> QueryAsync(AssistContentRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("provider is having a bad day");
    }

    private sealed class RecordingErrorInsightService : IErrorInsightService
    {
        public List<CommandFailureContext> Seen { get; } = [];

        public Task<IReadOnlyList<CommandFixSuggestion>> AnalyzeAsync(CommandFailureContext context, CancellationToken cancellationToken = default)
        {
            Seen.Add(context);
            return Task.FromResult<IReadOnlyList<CommandFixSuggestion>>([]);
        }
    }

    private sealed class StubErrorInsightService(IReadOnlyList<CommandFixSuggestion> fixes) : IErrorInsightService
    {
        public Task<IReadOnlyList<CommandFixSuggestion>> AnalyzeAsync(CommandFailureContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(fixes);
    }

    private sealed class FakeKnowledgeSource(string? attribution)
        : ICommandDocsProvider, IRecipeProvider, ICommandKnowledgeAttributionSource
    {
        public CommandHelpQuery? LastQuery { get; private set; }

        public string? Attribution => attribution;

        public Task<IReadOnlyList<CommandHelpItem>> GetHelpAsync(CommandHelpQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult<IReadOnlyList<CommandHelpItem>>([Doc(query.CommandToken ?? "?")]);
        }

        public Task<IReadOnlyList<CommandHelpItem>> GetRecipesAsync(CommandHelpQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult<IReadOnlyList<CommandHelpItem>>([Doc((query.CommandToken ?? "?") + " --help")]);
        }
    }

    private sealed class NullHistoryStore : IHistoryStore
    {
        public Task AppendAsync(CommandHistoryEntry entry, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<CommandHistoryEntry>> SearchAsync(string query, int maxCandidates, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommandHistoryEntry>>([]);

        public Task<IReadOnlyList<CommandHistoryEntry>> GetRecentAsync(int maxResults, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommandHistoryEntry>>([]);

        public Task<bool> TryUpdateExecutionResultAsync(string entryId, int? exitCode, long? durationMs, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NullSuggestionEngine : ISuggestionEngine
    {
        public IReadOnlyList<AssistSuggestion> GetSuggestions(
            IReadOnlyList<CommandHistoryEntry> entries,
            CommandAssistQueryContext context,
            int maxResults) => [];
    }
}

using System;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.ShellIntegration.Bash;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using NovaTerminal.CommandAssist.ShellIntegration.Fish;
using NovaTerminal.CommandAssist.ShellIntegration.PowerShell;
using NovaTerminal.CommandAssist.ShellIntegration.Runtime;
using NovaTerminal.CommandAssist.ShellIntegration.Zsh;
using NovaTerminal.CommandAssist.Storage;

namespace NovaTerminal.Shell;

/// <summary>
/// The composed Command Assist dependency graph.
/// </summary>
/// <remarks>
/// <para>
/// Lives in the App (not in <c>NovaTerminal.CommandAssist</c>) because it is where the assist
/// assembly's dependencies are resolved from application state: <see cref="AppPaths"/> for storage
/// locations and <see cref="TerminalSettings"/> for limits.
/// </para>
/// <para>
/// One instance is built at the App composition root (<see cref="AppServices"/>), carried on
/// <see cref="AppServiceBundle"/>, and handed to every <c>TerminalPane</c>. This replaces the
/// static <c>CommandAssistInfrastructure</c> service locator (Phase 0 task 3 of
/// <c>docs/plans/2026-08-01-command-assist-v2-plan.md</c>). A pane that reaches Command Assist
/// initialization without an instance throws rather than quietly building its own.
/// </para>
/// <para>
/// Creation stays lazy per service, exactly as the static locator was: constructing this object
/// must not touch the filesystem, because it happens on the startup path before the first window
/// is shown.
/// </para>
/// </remarks>
public sealed class CommandAssistServices
{
    /// <summary>Matches <see cref="TerminalSettings.CommandAssistMaxHistoryEntries"/>' default.</summary>
    public const int DefaultMaxHistoryEntries = 5000;

    private readonly object _sync = new();
    private readonly string _historyFilePath;
    private readonly string? _legacyHistoryFilePath;
    private readonly string _snippetsFilePath;

    private IHistoryStore? _historyStore;
    private int _historyMaxEntries;
    private ISnippetStore? _snippetStore;
    private ICommandDocsProvider? _commandDocsProvider;
    private IRecipeProvider? _recipeProvider;
    private IErrorInsightService? _errorInsightService;

    /// <param name="bootstrapDirectoryFactory">
    /// Deferred on purpose: <see cref="AppPaths"/> reads environment state, so it must be evaluated
    /// inside <c>CreateLaunchPlan</c> (where the caller's try/catch can swallow a failure and fall
    /// back to a non-integrated launch) rather than here, where a throw would take down the whole
    /// composition root.
    /// </param>
    public CommandAssistServices(
        string historyFilePath,
        string? legacyHistoryFilePath,
        string snippetsFilePath,
        Func<string> bootstrapDirectoryFactory,
        int maxHistoryEntries = DefaultMaxHistoryEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(snippetsFilePath);
        ArgumentNullException.ThrowIfNull(bootstrapDirectoryFactory);

        _historyFilePath = historyFilePath;
        _legacyHistoryFilePath = legacyHistoryFilePath;
        _snippetsFilePath = snippetsFilePath;
        _historyMaxEntries = Math.Max(1, maxHistoryEntries);

        SecretsFilter = new SecretsFilter();
        SuggestionEngine = new CommandAssistSuggestionEngine();
        ShellIntegrationRegistry = new ShellIntegrationRegistry(new IShellIntegrationProvider[]
        {
            new PowerShellShellIntegrationProvider(bootstrapDirectoryFactory),
            new BashShellIntegrationProvider(bootstrapDirectoryFactory),
            new ZshShellIntegrationProvider(bootstrapDirectoryFactory),
            new FishShellIntegrationProvider(bootstrapDirectoryFactory)
        });
    }

    /// <summary>Builds the production graph against <see cref="AppPaths"/>.</summary>
    public static CommandAssistServices CreateDefault()
    {
        return new CommandAssistServices(
            AppPaths.CommandHistoryFilePath,
            AppPaths.LegacyCommandHistoryFilePath,
            AppPaths.CommandSnippetsFilePath,
            static () => AppPaths.CommandAssistDirectory);
    }

    public ISecretsFilter SecretsFilter { get; }

    public ISuggestionEngine SuggestionEngine { get; }

    public ShellIntegrationRegistry ShellIntegrationRegistry { get; }

    public IHistoryStore HistoryStore
    {
        get
        {
            lock (_sync)
            {
                return _historyStore ??= new JsonlHistoryStore(
                    _historyFilePath,
                    _historyMaxEntries,
                    _legacyHistoryFilePath);
            }
        }
    }

    public ISnippetStore SnippetStore
    {
        get
        {
            lock (_sync)
            {
                return _snippetStore ??= new JsonSnippetStore(_snippetsFilePath);
            }
        }
    }

    public ICommandDocsProvider CommandDocsProvider
    {
        get
        {
            lock (_sync)
            {
                return _commandDocsProvider ??= new LocalCommandDocsProvider();
            }
        }
    }

    public IRecipeProvider RecipeProvider
    {
        get
        {
            lock (_sync)
            {
                return _recipeProvider ??= new SeedRecipeProvider();
            }
        }
    }

    public IErrorInsightService ErrorInsightService
    {
        get
        {
            lock (_sync)
            {
                return _errorInsightService ??= new HeuristicErrorInsightService();
            }
        }
    }

    /// <summary>
    /// Applies <see cref="TerminalSettings.CommandAssistMaxHistoryEntries"/>, replacing the history
    /// store when the cap actually changed.
    /// </summary>
    /// <remarks>
    /// The retention cap is baked into the store at construction, so changing it means building a
    /// new one. The static locator did this as a side effect of its <c>GetHistoryStore(settings)</c>
    /// getter, which made "read a property" and "swap a store out from under existing panes" the
    /// same call. Here it is an explicit step the caller opts into. Controllers already holding the
    /// previous instance keep it, exactly as before: the cap only governs writes, and both stores
    /// point at the same file.
    /// </remarks>
    public void ApplyHistoryRetentionLimit(int maxHistoryEntries)
    {
        int clamped = Math.Max(1, maxHistoryEntries);

        lock (_sync)
        {
            if (_historyMaxEntries == clamped && _historyStore != null)
            {
                return;
            }

            _historyMaxEntries = clamped;
            _historyStore = null;
        }
    }
}

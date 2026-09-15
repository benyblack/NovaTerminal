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

    /// <summary>
    /// Concretely typed, not <c>IHistoryStore</c>: the retention cap is mutated in place on the one
    /// live instance (see <see cref="ApplyHistoryRetentionLimit"/>), which is not an
    /// <c>IHistoryStore</c> concern.
    /// </summary>
    private JsonlHistoryStore? _historyStore;

    private int _historyMaxEntries;
    private ISnippetStore? _snippetStore;
    private CommandKnowledgeService? _commandKnowledgeService;
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

    /// <summary>
    /// The bundled command-knowledge catalogue, serving both Help docs and Recipe rows (V2 Phase 4b).
    /// </summary>
    /// <remarks>
    /// <para>
    /// One instance behind both properties, not two. The catalogue is ~825 KB of JSON parsed into an
    /// index on first use; a Help request asks for docs and recipes in the same breath, so two
    /// instances would mean two parses and two copies of the index for one popup.
    /// </para>
    /// <para>
    /// It replaces <c>LocalCommandDocsProvider</c> and <c>SeedRecipeProvider</c>, which between them
    /// knew seven commands. Construction stays free - the parse is lazy and happens on a worker
    /// inside the service - so this still honors the "constructing this object must not touch the
    /// filesystem" rule in the type remarks.
    /// </para>
    /// </remarks>
    private CommandKnowledgeService CommandKnowledge
    {
        get
        {
            lock (_sync)
            {
                return _commandKnowledgeService ??= new CommandKnowledgeService(new LocalCommandHelpProbe());
            }
        }
    }

    public ICommandDocsProvider CommandDocsProvider => CommandKnowledge;

    public IRecipeProvider RecipeProvider => CommandKnowledge;

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
    /// Applies <see cref="TerminalSettings.CommandAssistMaxHistoryEntries"/> to the one live history
    /// store, without ever replacing it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The static locator re-created the store whenever the cap differed, as a side effect of its
    /// <c>GetHistoryStore(settings)</c> getter - so "read a property" and "swap a store out from
    /// under existing panes" were the same call. That was survivable only because
    /// <c>JsonHistoryStore</c> was stateless per operation.
    /// </para>
    /// <para>
    /// <see cref="JsonlHistoryStore"/> is not: it caches an index and a physical line count, and
    /// compaction rewrites the whole file from that cache. Two live instances over one file would
    /// each rewrite from their own stale view - the older instance resurrecting entries the newer
    /// one's cap deleted, and either one dropping the other's appends. So the cap is pushed into
    /// the existing instance instead, and no code path in this class ever nulls the field.
    /// </para>
    /// </remarks>
    public void ApplyHistoryRetentionLimit(int maxHistoryEntries)
    {
        int clamped = Math.Max(1, maxHistoryEntries);

        lock (_sync)
        {
            _historyMaxEntries = clamped;

            // Null only when nothing has read HistoryStore yet, in which case the constructor
            // below will pick the new cap up. Creating it here just to set a field it does not
            // have yet would defeat the lazy-construction contract in the type remarks.
            _historyStore?.SetMaxEntries(clamped);
        }
    }
}

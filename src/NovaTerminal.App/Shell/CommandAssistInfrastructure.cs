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
/// App-side composition root for Command Assist services.
/// </summary>
/// <remarks>
/// Lives in the App (not in <c>NovaTerminal.CommandAssist</c>) because it is where the assist
/// assembly's dependencies are resolved from application state: <see cref="AppPaths"/> for storage
/// locations and <see cref="TerminalSettings"/> for limits. Phase 0 task 3 of the Command Assist V2
/// plan replaces this static locator with an injected <c>CommandAssistServices</c>.
/// </remarks>
public static class CommandAssistInfrastructure
{
    private static readonly object Sync = new();
    private static IHistoryStore? _historyStore;
    private static ISnippetStore? _snippetStore;
    private static ICommandDocsProvider? _commandDocsProvider;
    private static IRecipeProvider? _recipeProvider;
    private static IErrorInsightService? _errorInsightService;
    private static int _historyMaxEntries = -1;
    private static readonly ISecretsFilter SecretsFilterInstance = new SecretsFilter();
    private static readonly ISuggestionEngine SuggestionEngineInstance = new CommandAssistSuggestionEngine();
    private static readonly ShellIntegrationRegistry ShellIntegrationRegistryInstance = new(new IShellIntegrationProvider[]
    {
        new PowerShellShellIntegrationProvider(AppPaths.CommandAssistDirectory),
        new BashShellIntegrationProvider(AppPaths.CommandAssistDirectory),
        new ZshShellIntegrationProvider(AppPaths.CommandAssistDirectory),
        new FishShellIntegrationProvider(AppPaths.CommandAssistDirectory)
    });

    public static IHistoryStore GetHistoryStore(TerminalSettings settings)
    {
        int maxEntries = Math.Max(1, settings.CommandAssistMaxHistoryEntries);

        lock (Sync)
        {
            if (_historyStore == null || _historyMaxEntries != maxEntries)
            {
                _historyStore = new JsonHistoryStore(AppPaths.CommandHistoryFilePath, maxEntries);
                _historyMaxEntries = maxEntries;
            }

            return _historyStore;
        }
    }

    public static ISnippetStore GetSnippetStore()
    {
        lock (Sync)
        {
            _snippetStore ??= new JsonSnippetStore(AppPaths.CommandSnippetsFilePath);
            return _snippetStore;
        }
    }

    public static ISecretsFilter GetSecretsFilter() => SecretsFilterInstance;

    public static ISuggestionEngine GetSuggestionEngine() => SuggestionEngineInstance;

    public static ICommandDocsProvider GetCommandDocsProvider()
    {
        lock (Sync)
        {
            _commandDocsProvider ??= new LocalCommandDocsProvider();
            return _commandDocsProvider;
        }
    }

    public static IRecipeProvider GetRecipeProvider()
    {
        lock (Sync)
        {
            _recipeProvider ??= new SeedRecipeProvider();
            return _recipeProvider;
        }
    }

    public static IErrorInsightService GetErrorInsightService()
    {
        lock (Sync)
        {
            _errorInsightService ??= new HeuristicErrorInsightService();
            return _errorInsightService;
        }
    }

    public static ShellIntegrationRegistry GetShellIntegrationRegistry() => ShellIntegrationRegistryInstance;
}

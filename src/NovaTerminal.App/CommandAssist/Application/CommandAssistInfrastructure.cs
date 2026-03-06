using System;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Storage;
using NovaTerminal.Core;

namespace NovaTerminal.CommandAssist.Application;

public static class CommandAssistInfrastructure
{
    private static readonly object Sync = new();
    private static IHistoryStore? _historyStore;
    private static int _historyMaxEntries = -1;
    private static readonly ISecretsFilter SecretsFilterInstance = new SecretsFilter();
    private static readonly ISuggestionEngine SuggestionEngineInstance = new HistorySuggestionEngine();

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

    public static ISecretsFilter GetSecretsFilter() => SecretsFilterInstance;

    public static ISuggestionEngine GetSuggestionEngine() => SuggestionEngineInstance;
}

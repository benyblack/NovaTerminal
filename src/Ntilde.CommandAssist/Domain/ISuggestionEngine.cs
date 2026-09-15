using System.Collections.Generic;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Domain;

public interface ISuggestionEngine
{
    IReadOnlyList<AssistSuggestion> GetSuggestions(
        IReadOnlyList<CommandHistoryEntry> entries,
        CommandAssistQueryContext context,
        int maxResults);

    IReadOnlyList<AssistSuggestion> GetSuggestions(
        IReadOnlyList<CommandHistoryEntry> entries,
        IReadOnlyList<CommandSnippet> snippets,
        CommandAssistQueryContext context,
        int maxResults)
    {
        return GetSuggestions(entries, context, maxResults);
    }
}

using System.Collections.Generic;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Domain;

public interface ISuggestionEngine
{
    IReadOnlyList<AssistSuggestion> GetSuggestions(
        IReadOnlyList<CommandHistoryEntry> entries,
        CommandAssistQueryContext context,
        int maxResults);
}

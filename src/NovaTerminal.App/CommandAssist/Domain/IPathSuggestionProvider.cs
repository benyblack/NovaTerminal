using System.Collections.Generic;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Domain;

public interface IPathSuggestionProvider
{
    IReadOnlyList<AssistSuggestion> GetSuggestions(CommandAssistQueryContext context, int maxResults);
}

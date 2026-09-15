using System.Collections.Generic;
using Ntilde.CommandAssist.Models;

namespace Ntilde.CommandAssist.Domain;

public interface IPathSuggestionProvider
{
    IReadOnlyList<AssistSuggestion> GetSuggestions(CommandAssistQueryContext context, int maxResults);
}

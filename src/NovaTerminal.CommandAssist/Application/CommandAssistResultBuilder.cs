using System;
using System.Collections.Generic;
using System.Linq;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Application;

public sealed class CommandAssistResultBuilder
{
    public IReadOnlyList<AssistSuggestion> BuildCombined(
        IReadOnlyList<AssistSuggestion> existingSuggestions,
        IReadOnlyList<CommandHelpItem> docs,
        IReadOnlyList<CommandHelpItem> recipes,
        IReadOnlyList<CommandFixSuggestion> fixes)
    {
        List<AssistSuggestion> results = new(existingSuggestions.Count + docs.Count + recipes.Count + fixes.Count);
        results.AddRange(existingSuggestions);
        results.AddRange(BuildHelpSuggestions(docs, AssistSuggestionType.Doc));
        results.AddRange(BuildHelpSuggestions(recipes, AssistSuggestionType.Recipe));
        results.AddRange(BuildFixSuggestions(fixes));
        return results;
    }

    public IReadOnlyList<AssistSuggestion> BuildHelpSuggestions(
        IReadOnlyList<CommandHelpItem> items,
        AssistSuggestionType type)
    {
        return items
            .Select((item, index) => new AssistSuggestion(
                Id: BuildDeterministicId(type, item.Command, index),
                Type: type,
                DisplayText: item.Title,
                InsertText: item.Command,
                Description: item.Description,
                Badges: item.Badges ?? Array.Empty<string>(),
                Score: 0,
                WorkingDirectory: null,
                LastUsedAt: null,
                ExitCode: null,
                CanExecuteDirectly: false))
            .ToArray();
    }

    public IReadOnlyList<AssistSuggestion> BuildFixSuggestions(IReadOnlyList<CommandFixSuggestion> items)
    {
        return items
            .Select((item, index) => new AssistSuggestion(
                Id: BuildDeterministicId(AssistSuggestionType.Fix, item.SuggestedCommand, index),
                Type: AssistSuggestionType.Fix,
                DisplayText: item.Title,
                InsertText: item.SuggestedCommand,
                Description: item.Description,
                Badges: item.Badges ?? Array.Empty<string>(),
                Score: item.Confidence,
                WorkingDirectory: null,
                LastUsedAt: null,
                ExitCode: null,
                CanExecuteDirectly: true))
            .ToArray();
    }

    private static string BuildDeterministicId(AssistSuggestionType type, string text, int index)
    {
        string normalized = text.Trim();
        return $"{type}:{index}:{normalized}";
    }
}

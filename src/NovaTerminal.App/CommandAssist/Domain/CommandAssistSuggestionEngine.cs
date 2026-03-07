using System;
using System.Collections.Generic;
using System.Linq;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Domain;

public sealed class CommandAssistSuggestionEngine : ISuggestionEngine
{
    public IReadOnlyList<AssistSuggestion> GetSuggestions(
        IReadOnlyList<CommandHistoryEntry> historyEntries,
        CommandAssistQueryContext context,
        int maxResults)
    {
        return GetSuggestions(historyEntries, Array.Empty<CommandSnippet>(), context, maxResults);
    }

    public IReadOnlyList<AssistSuggestion> GetSuggestions(
        IReadOnlyList<CommandHistoryEntry> historyEntries,
        IReadOnlyList<CommandSnippet> snippets,
        CommandAssistQueryContext context,
        int maxResults)
    {
        if (maxResults <= 0)
        {
            return Array.Empty<AssistSuggestion>();
        }

        string query = context.Input?.Trim() ?? string.Empty;
        List<AssistSuggestion> results = new();
        results.AddRange(BuildHistorySuggestions(historyEntries, context, query));
        results.AddRange(BuildSnippetSuggestions(snippets, context, query));

        return results
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.LastUsedAt ?? DateTimeOffset.MinValue)
            .ThenBy(x => x.DisplayText, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToList();
    }

    private static IEnumerable<AssistSuggestion> BuildHistorySuggestions(
        IReadOnlyList<CommandHistoryEntry> historyEntries,
        CommandAssistQueryContext context,
        string query)
    {
        return historyEntries
            .GroupBy(x => x.CommandText, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                CommandHistoryEntry latest = group.OrderByDescending(x => x.ExecutedAt).First();
                int frequency = group.Count();
                double score = ScoreText(
                    latest.CommandText,
                    query,
                    latest.WorkingDirectory,
                    latest.ShellKind,
                    latest.ProfileId,
                    latest.ExitCode,
                    context,
                    frequency,
                    isPinned: false);

                return new
                {
                    Latest = latest,
                    Frequency = frequency,
                    Score = score
                };
            })
            .Where(x => x.Score > 0)
            .Select(x => new AssistSuggestion(
                Id: x.Latest.Id,
                Type: AssistSuggestionType.History,
                DisplayText: x.Latest.CommandText,
                InsertText: x.Latest.CommandText,
                Badges: BuildHistoryBadges(x.Latest, context, x.Frequency),
                Score: x.Score,
                WorkingDirectory: x.Latest.WorkingDirectory,
                LastUsedAt: x.Latest.ExecutedAt,
                ExitCode: x.Latest.ExitCode,
                CanExecuteDirectly: false));
    }

    private static IEnumerable<AssistSuggestion> BuildSnippetSuggestions(
        IReadOnlyList<CommandSnippet> snippets,
        CommandAssistQueryContext context,
        string query)
    {
        return snippets
            .Select(snippet =>
            {
                double score = ScoreText(
                    snippet.CommandText,
                    query,
                    snippet.WorkingDirectory,
                    snippet.ShellKind,
                    profileId: null,
                    exitCode: 0,
                    context,
                    frequency: 1,
                    isPinned: snippet.IsPinned);

                // Snippets should remain discoverable even with empty query.
                if (string.IsNullOrWhiteSpace(query))
                {
                    score += 8;
                }

                return new
                {
                    Snippet = snippet,
                    Score = score
                };
            })
            .Where(x => x.Score > 0)
            .Select(x => new AssistSuggestion(
                Id: x.Snippet.Id,
                Type: AssistSuggestionType.Snippet,
                DisplayText: x.Snippet.Name,
                InsertText: x.Snippet.CommandText,
                Badges: BuildSnippetBadges(x.Snippet, context),
                Score: x.Score,
                WorkingDirectory: x.Snippet.WorkingDirectory,
                LastUsedAt: x.Snippet.LastUsedAt ?? x.Snippet.CreatedAt,
                ExitCode: null,
                CanExecuteDirectly: false));
    }

    private static double ScoreText(
        string text,
        string query,
        string? workingDirectory,
        string? shellKind,
        string? profileId,
        int? exitCode,
        CommandAssistQueryContext context,
        int frequency,
        bool isPinned)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return 1 + Math.Min(frequency, 5) + (isPinned ? 20 : 0);
        }

        string normalizedText = text.Trim();
        string normalizedQuery = query.Trim();
        string lowerText = normalizedText.ToLowerInvariant();
        string lowerQuery = normalizedQuery.ToLowerInvariant();

        double prefixScore = lowerText.StartsWith(lowerQuery, StringComparison.Ordinal) ? 120 : 0;
        double tokenPrefixScore = lowerText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.StartsWith(lowerQuery, StringComparison.Ordinal))
            ? 70
            : 0;
        double containsScore = lowerText.Contains(lowerQuery, StringComparison.Ordinal) ? 25 : 0;
        double subsequenceScore = IsSubsequence(lowerQuery, lowerText) ? 12 : 0;
        double frequencyScore = frequency * 4;
        double cwdScore = Matches(context.WorkingDirectory, workingDirectory) ? 12 : 0;
        double shellScore = Matches(context.ShellKind, shellKind) ? 4 : 0;
        double profileScore = Matches(context.ProfileId, profileId) ? 20 : 0;
        double successScore = exitCode == 0 ? 18 : exitCode.HasValue ? -8 : 0;
        double pinScore = isPinned ? 40 : 0;

        return prefixScore + tokenPrefixScore + containsScore + subsequenceScore + frequencyScore + cwdScore + shellScore + profileScore + successScore + pinScore;
    }

    private static IReadOnlyList<string> BuildHistoryBadges(CommandHistoryEntry entry, CommandAssistQueryContext context, int frequency)
    {
        List<string> badges = new();
        if (frequency > 1)
        {
            badges.Add("Frequent");
        }

        if (Matches(context.WorkingDirectory, entry.WorkingDirectory))
        {
            badges.Add("Same cwd");
        }

        if (entry.ExitCode == 0)
        {
            badges.Add("Worked");
        }

        return badges;
    }

    private static IReadOnlyList<string> BuildSnippetBadges(CommandSnippet snippet, CommandAssistQueryContext context)
    {
        List<string> badges = new() { "Snippet" };
        if (snippet.IsPinned)
        {
            badges.Add("Pinned");
        }

        if (Matches(context.WorkingDirectory, snippet.WorkingDirectory))
        {
            badges.Add("Same cwd");
        }

        return badges;
    }

    private static bool Matches(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSubsequence(string query, string text)
    {
        int index = 0;
        for (int i = 0; i < text.Length && index < query.Length; i++)
        {
            if (text[i] == query[index])
            {
                index++;
            }
        }

        return index == query.Length;
    }
}

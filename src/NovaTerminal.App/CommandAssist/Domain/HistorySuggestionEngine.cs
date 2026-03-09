using System;
using System.Collections.Generic;
using System.Linq;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Domain;

public sealed class HistorySuggestionEngine : ISuggestionEngine
{
    public IReadOnlyList<AssistSuggestion> GetSuggestions(
        IReadOnlyList<CommandHistoryEntry> entries,
        CommandAssistQueryContext context,
        int maxResults)
    {
        if (entries.Count == 0 || maxResults <= 0)
        {
            return Array.Empty<AssistSuggestion>();
        }

        string query = context.Input?.Trim() ?? string.Empty;

        var grouped = entries
            .GroupBy(x => x.CommandText, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                CommandHistoryEntry latest = group.OrderByDescending(x => x.ExecutedAt).First();
                int frequency = group.Count();
                double score = Score(latest.CommandText, query, latest, context, frequency);
                return new { Latest = latest, Frequency = frequency, Score = score };
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Latest.ExecutedAt)
            .Take(maxResults)
            .Select(x => new AssistSuggestion(
                Id: x.Latest.Id,
                Type: AssistSuggestionType.History,
                DisplayText: x.Latest.CommandText,
                InsertText: x.Latest.CommandText,
                Description: null,
                Badges: BuildBadges(x.Latest, context, x.Frequency),
                Score: x.Score,
                WorkingDirectory: x.Latest.WorkingDirectory,
                LastUsedAt: x.Latest.ExecutedAt,
                ExitCode: x.Latest.ExitCode,
                CanExecuteDirectly: false))
            .ToList();

        return grouped;
    }

    private static double Score(
        string commandText,
        string query,
        CommandHistoryEntry entry,
        CommandAssistQueryContext context,
        int frequency)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return 0;
        }

        string normalizedCommand = commandText.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return 1 + Math.Min(frequency, 5);
        }

        string normalizedQuery = query.Trim();
        string lowerCommand = normalizedCommand.ToLowerInvariant();
        string lowerQuery = normalizedQuery.ToLowerInvariant();

        double prefixScore = lowerCommand.StartsWith(lowerQuery, StringComparison.Ordinal) ? 100 : 0;
        double tokenPrefixScore = lowerCommand
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.StartsWith(lowerQuery, StringComparison.Ordinal))
            ? 50
            : 0;
        double containsScore = lowerCommand.Contains(lowerQuery, StringComparison.Ordinal) ? 20 : 0;
        double subsequenceScore = IsSubsequence(lowerQuery, lowerCommand) ? 10 : 0;

        double recencyScore = entry.ExecutedAt.ToUnixTimeSeconds() / 1_000_000_000d;
        double frequencyScore = frequency * 5;
        double cwdScore = !string.IsNullOrWhiteSpace(context.WorkingDirectory) &&
                          string.Equals(context.WorkingDirectory, entry.WorkingDirectory, StringComparison.OrdinalIgnoreCase)
            ? 15
            : 0;
        double shellScore = !string.IsNullOrWhiteSpace(context.ShellKind) &&
                            string.Equals(context.ShellKind, entry.ShellKind, StringComparison.OrdinalIgnoreCase)
            ? 2
            : 0;

        return prefixScore + tokenPrefixScore + containsScore + subsequenceScore + recencyScore + frequencyScore + cwdScore + shellScore;
    }

    private static IReadOnlyList<string> BuildBadges(CommandHistoryEntry entry, CommandAssistQueryContext context, int frequency)
    {
        var badges = new List<string>();
        if (frequency > 1)
        {
            badges.Add("Frequent");
        }

        if (!string.IsNullOrWhiteSpace(context.WorkingDirectory) &&
            string.Equals(context.WorkingDirectory, entry.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            badges.Add("Same cwd");
        }

        return badges;
    }

    private static bool IsSubsequence(string query, string command)
    {
        int queryIndex = 0;
        for (int i = 0; i < command.Length && queryIndex < query.Length; i++)
        {
            if (command[i] == query[queryIndex])
            {
                queryIndex++;
            }
        }

        return queryIndex == query.Length;
    }
}

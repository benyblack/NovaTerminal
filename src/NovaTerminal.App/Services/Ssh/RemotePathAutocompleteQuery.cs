using System;
using System.Collections.Generic;
using System.Linq;
using NovaTerminal.Models;

namespace NovaTerminal.Services.Ssh;

public sealed class RemotePathAutocompleteQuery
{
    private RemotePathAutocompleteQuery(string parentPath, string prefix)
    {
        ParentPath = parentPath;
        Prefix = prefix;
    }

    public string ParentPath { get; }
    public string Prefix { get; }

    public static RemotePathAutocompleteQuery Parse(string input)
    {
        string normalized = (input ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return new RemotePathAutocompleteQuery("~", string.Empty);
        }

        if (normalized == "~")
        {
            return new RemotePathAutocompleteQuery("~", string.Empty);
        }

        if (normalized.EndsWith("/", StringComparison.Ordinal))
        {
            string directoryPath = normalized.TrimEnd('/');
            if (string.IsNullOrEmpty(directoryPath))
            {
                return new RemotePathAutocompleteQuery("/", string.Empty);
            }

            if (directoryPath == "~")
            {
                return new RemotePathAutocompleteQuery("~", string.Empty);
            }

            return new RemotePathAutocompleteQuery(directoryPath, string.Empty);
        }

        string trimmed = normalized.TrimEnd('/');
        int lastSlashIndex = trimmed.LastIndexOf('/');
        if (lastSlashIndex < 0)
        {
            return new RemotePathAutocompleteQuery("~", trimmed);
        }

        if (lastSlashIndex == 0)
        {
            string prefix = trimmed.Length == 1 ? string.Empty : trimmed[1..];
            return new RemotePathAutocompleteQuery("/", prefix);
        }

        string parentPath = trimmed[..lastSlashIndex];
        string leafPrefix = trimmed[(lastSlashIndex + 1)..];
        return new RemotePathAutocompleteQuery(parentPath, leafPrefix);
    }

    public static IReadOnlyList<RemotePathSuggestion> Rank(
        IEnumerable<RemotePathSuggestion> suggestions,
        string prefix)
    {
        ArgumentNullException.ThrowIfNull(suggestions);

        string normalizedPrefix = prefix?.Trim() ?? string.Empty;

        return suggestions
            .Where(suggestion => string.IsNullOrWhiteSpace(normalizedPrefix) ||
                                 suggestion.DisplayName.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase) ||
                                 suggestion.DisplayName.Contains(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(suggestion => suggestion.DisplayName.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(suggestion => suggestion.IsDirectory)
            .ThenBy(suggestion => suggestion.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

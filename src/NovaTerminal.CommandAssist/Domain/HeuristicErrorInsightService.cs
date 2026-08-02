using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Domain;

public sealed class HeuristicErrorInsightService : IErrorInsightService
{
    private static readonly string[] KnownCommands =
    [
        "git",
        "docker",
        "ls",
        "cd",
        "grep",
        "Get-ChildItem",
        "Set-Location"
    ];

    public Task<IReadOnlyList<CommandFixSuggestion>> AnalyzeAsync(CommandFailureContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.CommandText))
        {
            return Task.FromResult<IReadOnlyList<CommandFixSuggestion>>(Array.Empty<CommandFixSuggestion>());
        }

        string commandToken = ExtractPrimaryToken(context.CommandText);
        if (string.IsNullOrWhiteSpace(commandToken))
        {
            return Task.FromResult<IReadOnlyList<CommandFixSuggestion>>(Array.Empty<CommandFixSuggestion>());
        }

        List<CommandFixSuggestion> suggestions = new();

        if (IsCommandNotFound(context.ErrorOutput))
        {
            CommandFixSuggestion? shellMismatchSuggestion = TryBuildShellMismatchSuggestion(context.ShellKind, commandToken, context.CommandText);
            if (shellMismatchSuggestion != null)
            {
                suggestions.Add(shellMismatchSuggestion);
            }

            CommandFixSuggestion? correctionSuggestion = TryBuildCorrectionSuggestion(commandToken, context.CommandText);
            if (correctionSuggestion != null)
            {
                suggestions.Add(correctionSuggestion);
            }
        }
        else if (context.ExitCode.HasValue && context.ExitCode.Value != 0)
        {
            CommandFixSuggestion? correctionSuggestion = TryBuildCorrectionSuggestion(commandToken, context.CommandText);
            if (correctionSuggestion != null)
            {
                suggestions.Add(correctionSuggestion with
                {
                    Confidence = Math.Min(correctionSuggestion.Confidence, 0.82),
                    Description = "Closest known local command match after a failed command."
                });
            }
        }

        if (IsPathNotFound(context.ErrorOutput))
        {
            CommandFixSuggestion? pathSuggestion = TryBuildPathInvocationSuggestion(context.ShellKind, commandToken);
            if (pathSuggestion != null)
            {
                suggestions.Add(pathSuggestion);
            }
        }

        IReadOnlyList<CommandFixSuggestion> ordered = suggestions
            .OrderByDescending(item => item.Confidence)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(ordered);
    }

    private static bool IsCommandNotFound(string? errorOutput)
    {
        if (string.IsNullOrWhiteSpace(errorOutput))
        {
            return false;
        }

        return errorOutput.Contains("command not found", StringComparison.OrdinalIgnoreCase) ||
               errorOutput.Contains("is not recognized as an internal or external command", StringComparison.OrdinalIgnoreCase) ||
               errorOutput.Contains("is not recognized", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathNotFound(string? errorOutput)
    {
        return !string.IsNullOrWhiteSpace(errorOutput) &&
               errorOutput.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractPrimaryToken(string commandText)
    {
        string trimmed = commandText.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        int firstWhitespace = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        return firstWhitespace >= 0 ? trimmed[..firstWhitespace] : trimmed;
    }

    private static CommandFixSuggestion? TryBuildCorrectionSuggestion(string commandToken, string commandText)
    {
        string? bestMatch = null;
        int bestDistance = int.MaxValue;

        foreach (string knownCommand in KnownCommands)
        {
            int distance = ComputeLevenshteinDistance(commandToken, knownCommand);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestMatch = knownCommand;
            }
        }

        if (bestMatch == null ||
            bestDistance == 0 ||
            bestDistance > GetMaxAllowedDistance(commandToken, bestMatch))
        {
            return null;
        }

        string suggestedCommand = ReplaceLeadingToken(commandText, commandToken, bestMatch);
        double confidence = bestDistance <= 1 ? 0.95 : 0.84;

        return new CommandFixSuggestion(
            Title: $"Did you mean {bestMatch}?",
            SuggestedCommand: suggestedCommand,
            Description: "Closest known local command match.",
            Confidence: confidence,
            Badges: ["Fix", "Typo"]);
    }

    private static CommandFixSuggestion? TryBuildPathInvocationSuggestion(string? shellKind, string commandToken)
    {
        if (commandToken.StartsWith("./", StringComparison.Ordinal) ||
            commandToken.StartsWith(".\\", StringComparison.Ordinal) ||
            commandToken.Contains('/') ||
            commandToken.Contains('\\'))
        {
            return null;
        }

        string prefix = string.Equals(shellKind, "pwsh", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(shellKind, "powershell", StringComparison.OrdinalIgnoreCase)
            ? ".\\"
            : "./";

        return new CommandFixSuggestion(
            Title: "Try running the file from the current directory",
            SuggestedCommand: prefix + commandToken,
            Description: "The shell may need an explicit current-directory path.",
            Confidence: 0.83,
            Badges: ["Fix", "Path"]);
    }

    private static CommandFixSuggestion? TryBuildShellMismatchSuggestion(string? shellKind, string commandToken, string commandText)
    {
        string? replacement = null;

        if (string.Equals(shellKind, "bash", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(shellKind, "zsh", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(commandToken, "Get-ChildItem", StringComparison.OrdinalIgnoreCase))
            {
                replacement = "ls";
            }
            else if (string.Equals(commandToken, "Set-Location", StringComparison.OrdinalIgnoreCase))
            {
                replacement = "cd";
            }
            else if (string.Equals(commandToken, "dir", StringComparison.OrdinalIgnoreCase))
            {
                replacement = "ls";
            }
        }
        else if (string.Equals(shellKind, "cmd", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(commandToken, "ls", StringComparison.OrdinalIgnoreCase))
        {
            replacement = "dir";
        }

        if (replacement == null)
        {
            return null;
        }

        return new CommandFixSuggestion(
            Title: "Try the shell-native command",
            SuggestedCommand: ReplaceLeadingToken(commandText, commandToken, replacement),
            Description: "The original command token looks like it belongs to a different shell.",
            Confidence: 0.82,
            Badges: ["Fix", "Shell"]);
    }

    private static string ReplaceLeadingToken(string commandText, string originalToken, string replacementToken)
    {
        string trimmed = commandText.TrimStart();
        if (!trimmed.StartsWith(originalToken, StringComparison.Ordinal))
        {
            return replacementToken;
        }

        return replacementToken + trimmed[originalToken.Length..];
    }

    private static int GetMaxAllowedDistance(string commandToken, string candidate)
    {
        int shortestLength = Math.Min(commandToken.Length, candidate.Length);
        return shortestLength <= 4 ? 2 : 2;
    }

    private static int ComputeLevenshteinDistance(string source, string target)
    {
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        string left = source.ToLowerInvariant();
        string right = target.ToLowerInvariant();
        int[,] distances = new int[left.Length + 1, right.Length + 1];

        for (int i = 0; i <= left.Length; i++)
        {
            distances[i, 0] = i;
        }

        for (int j = 0; j <= right.Length; j++)
        {
            distances[0, j] = j;
        }

        for (int i = 1; i <= left.Length; i++)
        {
            for (int j = 1; j <= right.Length; j++)
            {
                int substitutionCost = left[i - 1] == right[j - 1] ? 0 : 1;
                distances[i, j] = Math.Min(
                    Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1),
                    distances[i - 1, j - 1] + substitutionCost);
            }
        }

        return distances[left.Length, right.Length];
    }
}

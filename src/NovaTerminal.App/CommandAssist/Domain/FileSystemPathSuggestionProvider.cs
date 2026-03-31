using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Domain;

public sealed class FileSystemPathSuggestionProvider : IPathSuggestionProvider
{
    private static readonly HashSet<string> PathCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "cd",
        "ls",
        "cat",
        "mv",
        "cp",
        "rm",
        "mkdir",
        "rmdir",
        "touch",
        "code"
    };

    private readonly string? _homeDirectoryOverride;

    public FileSystemPathSuggestionProvider(string? homeDirectoryOverride = null)
    {
        _homeDirectoryOverride = homeDirectoryOverride;
    }

    public IReadOnlyList<AssistSuggestion> GetSuggestions(CommandAssistQueryContext context, int maxResults)
    {
        if (maxResults <= 0 || context.IsRemote || !context.IncludePathSuggestions)
        {
            return Array.Empty<AssistSuggestion>();
        }

        if (!TryExtractCommandAndToken(context.Input, out string commandToken, out string activeToken))
        {
            return Array.Empty<AssistSuggestion>();
        }
        bool isQuotedToken = activeToken.StartsWith('"') || activeToken.StartsWith('\'');

        if (!ShouldSuggestPath(commandToken, activeToken))
        {
            return Array.Empty<AssistSuggestion>();
        }

        if (!TryResolveSearchContext(
                activeToken,
                context.WorkingDirectory,
                out string searchDirectory,
                out string typedLeaf,
                out string baseTokenPrefix,
                out char preferredSeparator))
        {
            return Array.Empty<AssistSuggestion>();
        }

        List<(string Name, bool IsDirectory)> entries = EnumerateEntries(searchDirectory, typedLeaf);
        if (entries.Count == 0)
        {
            return Array.Empty<AssistSuggestion>();
        }

        string queryPrefix = context.Input ?? string.Empty;
        List<AssistSuggestion> suggestions = new(Math.Min(maxResults, entries.Count));

        for (int i = 0; i < entries.Count && suggestions.Count < maxResults; i++)
        {
            (string entryName, bool isDirectory) = entries[i];
            string completionToken = baseTokenPrefix + entryName + (isDirectory ? preferredSeparator : string.Empty);
            if (!completionToken.StartsWith(activeToken, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string appendText = completionToken[activeToken.Length..];
            appendText = EscapeInsertionSuffix(appendText, context.ShellKind, isQuotedToken);
            if (appendText.Length == 0)
            {
                continue;
            }

            suggestions.Add(new AssistSuggestion(
                Id: $"path:{searchDirectory}:{entryName}:{isDirectory}",
                Type: AssistSuggestionType.Path,
                DisplayText: isDirectory ? $"{entryName}{preferredSeparator}" : entryName,
                InsertText: queryPrefix + appendText,
                Description: isDirectory ? "Directory" : "File",
                Badges: isDirectory ? new[] { "Path", "Directory" } : new[] { "Path", "File" },
                Score: (isDirectory ? 260 : 240) - i,
                WorkingDirectory: context.WorkingDirectory,
                LastUsedAt: null,
                ExitCode: null,
                CanExecuteDirectly: false));
        }

        return suggestions;
    }

    private static bool TryExtractCommandAndToken(string? input, out string commandToken, out string activeToken)
    {
        commandToken = string.Empty;
        activeToken = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        List<string> tokens = ParseTokens(input!);
        if (tokens.Count == 0)
        {
            return false;
        }

        commandToken = tokens[0];
        if (EndsWithTokenSeparatorOutsideQuotes(input!))
        {
            activeToken = string.Empty;
            return true;
        }

        activeToken = tokens[^1];
        return true;
    }

    private static bool ShouldSuggestPath(string commandToken, string activeToken)
    {
        if (PathCommands.Contains(commandToken))
        {
            return true;
        }

        return activeToken.StartsWith("./", StringComparison.Ordinal) ||
               activeToken.StartsWith("../", StringComparison.Ordinal) ||
               activeToken.StartsWith("~/", StringComparison.Ordinal) ||
               activeToken.StartsWith(".\\", StringComparison.Ordinal) ||
               activeToken.StartsWith("..\\", StringComparison.Ordinal) ||
               activeToken.StartsWith("~\\", StringComparison.Ordinal) ||
               activeToken.Contains('/') ||
               activeToken.Contains('\\');
    }

    private bool TryResolveSearchContext(
        string activeToken,
        string? workingDirectory,
        out string searchDirectory,
        out string typedLeaf,
        out string baseTokenPrefix,
        out char preferredSeparator)
    {
        searchDirectory = string.Empty;
        typedLeaf = string.Empty;
        baseTokenPrefix = string.Empty;

        string tokenForFilesystem = StripLeadingQuote(activeToken, out string quotePrefix);

        preferredSeparator = tokenForFilesystem.Contains('/')
            ? '/'
            : Path.DirectorySeparatorChar;

        string? homeDirectory = ResolveHomeDirectory();
        string expandedToken = ExpandTilde(tokenForFilesystem, homeDirectory);

        string absoluteCandidate;
        if (expandedToken.Length == 0)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                return false;
            }

            absoluteCandidate = workingDirectory!;
        }
        else if (Path.IsPathRooted(expandedToken))
        {
            absoluteCandidate = expandedToken;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                return false;
            }

            absoluteCandidate = Path.Combine(workingDirectory!, expandedToken);
        }

        bool tokenEndsWithSeparator = tokenForFilesystem.EndsWith('/') || tokenForFilesystem.EndsWith('\\');
        int leafSeparatorIndex = tokenForFilesystem.LastIndexOfAny(['/', '\\']);
        string prefixWithoutLeaf = leafSeparatorIndex >= 0
            ? tokenForFilesystem[..(leafSeparatorIndex + 1)]
            : string.Empty;
        baseTokenPrefix = quotePrefix + prefixWithoutLeaf;
        typedLeaf = tokenEndsWithSeparator
            ? string.Empty
            : leafSeparatorIndex >= 0
                ? tokenForFilesystem[(leafSeparatorIndex + 1)..]
                : tokenForFilesystem;

        if (tokenEndsWithSeparator || tokenForFilesystem.Length == 0)
        {
            searchDirectory = absoluteCandidate;
        }
        else
        {
            searchDirectory = Path.GetDirectoryName(absoluteCandidate) ?? string.Empty;
            if (searchDirectory.Length == 0)
            {
                searchDirectory = workingDirectory ?? string.Empty;
            }
        }

        return searchDirectory.Length > 0 && Directory.Exists(searchDirectory);
    }

    private string? ResolveHomeDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_homeDirectoryOverride))
        {
            return _homeDirectoryOverride;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : home;
    }

    private static string ExpandTilde(string token, string? homeDirectory)
    {
        if (string.IsNullOrWhiteSpace(homeDirectory) || !token.StartsWith('~'))
        {
            return token;
        }

        if (token.Length == 1)
        {
            return homeDirectory;
        }

        char next = token[1];
        if (next is not ('/' or '\\'))
        {
            return token;
        }

        string relative = token[2..]
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        return relative.Length == 0
            ? homeDirectory
            : Path.Combine(homeDirectory, relative);
    }

    private static string StripLeadingQuote(string token, out string quotePrefix)
    {
        quotePrefix = string.Empty;
        if (token.Length == 0)
        {
            return token;
        }

        if (token[0] is '"' or '\'')
        {
            quotePrefix = token[0].ToString();
            return token[1..];
        }

        return token;
    }

    private static List<string> ParseTokens(string input)
    {
        List<string> tokens = new();
        int tokenStart = -1;
        bool inSingleQuote = false;
        bool inDoubleQuote = false;

        for (int i = 0; i < input.Length; i++)
        {
            char ch = input[i];
            bool isWhitespace = char.IsWhiteSpace(ch);

            if (tokenStart < 0)
            {
                if (isWhitespace)
                {
                    continue;
                }

                tokenStart = i;
            }

            if (ch == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (ch == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
            }

            if (isWhitespace && !inSingleQuote && !inDoubleQuote)
            {
                tokens.Add(input[tokenStart..i]);
                tokenStart = -1;
            }
        }

        if (tokenStart >= 0)
        {
            tokens.Add(input[tokenStart..]);
        }

        return tokens;
    }

    private static bool EndsWithTokenSeparatorOutsideQuotes(string input)
    {
        bool inSingleQuote = false;
        bool inDoubleQuote = false;

        for (int i = 0; i < input.Length; i++)
        {
            char ch = input[i];
            if (ch == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (ch == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
            }
        }

        return !inSingleQuote &&
               !inDoubleQuote &&
               input.Length > 0 &&
               char.IsWhiteSpace(input[^1]);
    }

    private static string EscapeInsertionSuffix(string appendText, string? shellKind, bool isQuotedToken)
    {
        if (appendText.Length == 0 || isQuotedToken || !IsPowerShellShell(shellKind))
        {
            return appendText;
        }

        var escaped = new StringBuilder(appendText.Length + 8);
        foreach (char ch in appendText)
        {
            if (NeedsPowerShellEscape(ch))
            {
                escaped.Append('`');
            }

            if (ch == '`')
            {
                escaped.Append('`');
            }

            escaped.Append(ch);
        }

        return escaped.ToString();
    }

    private static bool IsPowerShellShell(string? shellKind)
    {
        if (string.IsNullOrWhiteSpace(shellKind))
        {
            return false;
        }

        return shellKind.Contains("pwsh", StringComparison.OrdinalIgnoreCase) ||
               shellKind.Contains("powershell", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NeedsPowerShellEscape(char ch)
    {
        return ch is ' ' or '\t' or '&' or '(' or ')' or '[' or ']' or '{' or '}' or ';' or ',' or '\'' or '"';
    }

    private static List<(string Name, bool IsDirectory)> EnumerateEntries(string directoryPath, string typedLeaf)
    {
        try
        {
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            IEnumerable<string> directories = Directory.EnumerateDirectories(directoryPath)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name) && name.StartsWith(typedLeaf, comparison))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)!;

            IEnumerable<string> files = Directory.EnumerateFiles(directoryPath)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name) && name.StartsWith(typedLeaf, comparison))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)!;

            List<(string Name, bool IsDirectory)> results = new();
            results.AddRange(directories.Select(name => (name!, true)));
            results.AddRange(files.Select(name => (name!, false)));
            return results;
        }
        catch
        {
            return new List<(string Name, bool IsDirectory)>();
        }
    }
}

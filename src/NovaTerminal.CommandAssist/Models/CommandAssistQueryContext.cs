namespace NovaTerminal.CommandAssist.Models;

public sealed record CommandAssistQueryContext(
    string Input,
    string? WorkingDirectory,
    string? ShellKind,
    string? ProfileId,
    bool IsRemote = false,
    bool IncludeHistorySuggestions = true,
    bool IncludeSnippetSuggestions = true,
    bool IncludePathSuggestions = true);

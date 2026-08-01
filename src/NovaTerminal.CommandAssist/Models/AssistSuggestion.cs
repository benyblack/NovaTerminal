using System;
using System.Collections.Generic;

namespace NovaTerminal.CommandAssist.Models;

public sealed record AssistSuggestion(
    string Id,
    AssistSuggestionType Type,
    string DisplayText,
    string InsertText,
    string? Description,
    IReadOnlyList<string> Badges,
    double Score,
    string? WorkingDirectory,
    DateTimeOffset? LastUsedAt,
    int? ExitCode,
    bool CanExecuteDirectly);

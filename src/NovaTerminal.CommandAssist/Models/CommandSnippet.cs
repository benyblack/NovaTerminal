using System;

namespace NovaTerminal.CommandAssist.Models;

public sealed record CommandSnippet(
    string Id,
    string Name,
    string CommandText,
    string? Description,
    string? ShellKind,
    string? WorkingDirectory,
    bool IsPinned,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt);

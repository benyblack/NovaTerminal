using System;
using System.Collections.Generic;

namespace NovaTerminal.CommandAssist.Models;

/// <remarks>
/// There is deliberately no "can execute directly" flag: V2 has no execute-from-assist action
/// (see <c>docs/plans/2026-08-01-command-assist-v2-design.md</c>, "Keyboard and gating decisions"),
/// so the property this record used to carry was set by one producer and read by nobody.
/// </remarks>
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
    int? ExitCode);

using System;
using System.Collections.Generic;

namespace NovaTerminal.CommandAssist.Models;

/// <remarks>
/// There is deliberately no "can execute directly" flag: V2 has no execute-from-assist action
/// (see <c>docs/plans/2026-08-01-command-assist-v2-design.md</c>, "Keyboard and gating decisions"),
/// so the property this record used to carry was set by one producer and read by nobody.
/// </remarks>
/// <param name="Frequency">
/// How many history entries collapsed into this row. Carried on the suggestion rather than folded
/// into <paramref name="Score"/> because of what the UX-polish round changed about the empty-query
/// list: frequency is now a <em>tiebreak applied after recency</em>, and a term inside the score is
/// necessarily applied before it. See
/// <c>CommandAssistSuggestionEngine.EmptyQuerySameDirectoryRecencyBonus</c>.
/// Declared last, with a default, so no existing call site had to move.
/// </param>
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
    int Frequency = 1);

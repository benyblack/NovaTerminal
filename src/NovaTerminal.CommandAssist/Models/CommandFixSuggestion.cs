using System.Collections.Generic;

namespace NovaTerminal.CommandAssist.Models;

public sealed record CommandFixSuggestion(
    string Title,
    string SuggestedCommand,
    string? Description,
    double Confidence,
    IReadOnlyList<string>? Badges = null);

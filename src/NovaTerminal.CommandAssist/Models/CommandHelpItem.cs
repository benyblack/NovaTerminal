using System.Collections.Generic;

namespace NovaTerminal.CommandAssist.Models;

public sealed record CommandHelpItem(
    string Title,
    string Command,
    string? Description,
    string? ShellKind,
    IReadOnlyList<string>? Badges = null);

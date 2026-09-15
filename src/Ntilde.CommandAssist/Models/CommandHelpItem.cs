using System.Collections.Generic;

namespace Ntilde.CommandAssist.Models;

public sealed record CommandHelpItem(
    string Title,
    string Command,
    string? Description,
    string? ShellKind,
    IReadOnlyList<string>? Badges = null);

namespace NovaTerminal.CommandAssist.Models;

public sealed record CommandHelpQuery(
    string RawInput,
    string? CommandToken,
    string? ShellKind,
    string? WorkingDirectory,
    string? SelectedText,
    string? SessionId);

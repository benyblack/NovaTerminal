namespace NovaTerminal.CommandAssist.Models;

public sealed record CommandFailureContext(
    string CommandText,
    int? ExitCode,
    string? ShellKind,
    string? WorkingDirectory,
    string? ErrorOutput,
    bool IsRemote,
    string? SelectedText);

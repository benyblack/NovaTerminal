namespace NovaTerminal.CommandAssist.Models;

public sealed record CommandAssistQueryContext(
    string Input,
    string? WorkingDirectory,
    string? ShellKind);

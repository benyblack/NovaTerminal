namespace NovaTerminal.CommandAssist.Models;

public sealed record CommandAssistContextSnapshot(
    string QueryText,
    string? RecognizedCommand,
    string? ShellKind,
    string? WorkingDirectory,
    string? ProfileId,
    string? SessionId,
    string? HostId,
    bool IsRemote,
    string? SelectedText);

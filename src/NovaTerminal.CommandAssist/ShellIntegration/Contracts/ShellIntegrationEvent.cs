using System;

namespace NovaTerminal.CommandAssist.ShellIntegration.Contracts;

public sealed record ShellIntegrationEvent(
    ShellIntegrationEventType Type,
    DateTimeOffset Timestamp,
    string? CommandText,
    string? WorkingDirectory,
    int? ExitCode,
    TimeSpan? Duration);

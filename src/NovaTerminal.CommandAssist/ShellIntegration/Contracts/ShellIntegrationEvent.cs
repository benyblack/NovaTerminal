using System;

namespace NovaTerminal.CommandAssist.ShellIntegration.Contracts;

/// <param name="MarkPosition">
/// Where the originating OSC 133 mark landed in the terminal buffer. Only
/// <see cref="ShellIntegrationEventType.CommandStarted"/> (OSC 133;B) carries one today; every
/// other event type leaves it null. Additive by design — consumers that don't read grid state
/// ignore it.
/// </param>
public sealed record ShellIntegrationEvent(
    ShellIntegrationEventType Type,
    DateTimeOffset Timestamp,
    string? CommandText,
    string? WorkingDirectory,
    int? ExitCode,
    TimeSpan? Duration,
    ShellMarkPosition? MarkPosition = null);

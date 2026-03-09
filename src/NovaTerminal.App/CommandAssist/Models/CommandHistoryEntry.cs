using System;

namespace NovaTerminal.CommandAssist.Models;

public sealed record CommandHistoryEntry(
    string Id,
    string CommandText,
    DateTimeOffset ExecutedAt,
    string ShellKind,
    string? WorkingDirectory,
    string? ProfileId,
    string? SessionId,
    string? HostId,
    int? ExitCode,
    bool IsRemote,
    bool IsRedacted,
    CommandCaptureSource Source,
    long? DurationMs);

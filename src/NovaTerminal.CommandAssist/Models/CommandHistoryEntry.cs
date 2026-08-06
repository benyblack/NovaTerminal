using System;

namespace NovaTerminal.CommandAssist.Models;

/// <param name="IsInvalidCommand">
/// Whether this command failed because the shell could not resolve its name - a typo, in practice.
/// Such an entry is never offered as a suggestion by
/// <c>CommandAssistSuggestionEngine</c>, on any path.
/// <para>
/// <strong>Why the entry is kept at all.</strong> The owner ran <c>gti status</c>, and the next time
/// he typed <c>gt</c> the terminal helpfully offered <c>gti status</c> back. Capture is not the bug -
/// the entry is a true record of what was run, and deleting it would make history disagree with the
/// scrollback - so the flag suppresses the <em>offer</em> and leaves the record.
/// </para>
/// <para>
/// Two signals set it, and both are needed. Exit code 127 is the POSIX convention and covers
/// bash, zsh and fish; PowerShell reports 1 for an unresolved name and is indistinguishable from any
/// other failure by exit code alone, so the Fix-time recogniser's classification of the output tail
/// is threaded back through <c>CapturePipeline.MarkLastCommandInvalidAsync</c> to cover it.
/// </para>
/// <para>
/// Declared last with a default so the JSONL format is unchanged for existing lines: a record
/// written before this round simply has no such property and deserialises to <see langword="false"/>.
/// </para>
/// </param>
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
    long? DurationMs,
    bool IsInvalidCommand = false);

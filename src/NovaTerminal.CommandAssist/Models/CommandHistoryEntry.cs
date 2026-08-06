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
/// <para>
/// <strong>Two ways it reaches entries written before the flag existed (dogfood round 4, item 4).</strong>
/// The flag shipped with no way to look backwards, so the owner's history - full of the very
/// <c>gti status</c> lines that motivated it - kept being offered, and the only cure was to run each
/// typo again. Now <c>JsonlHistoryStore.TryMarkInvalidCommandsByFirstTokenAsync</c> propagates a fresh
/// classification to every older entry that starts with the same word, and the loader backfills the
/// exit-code signal - see <c>CommandNotFoundExitCode</c>.
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
    bool IsInvalidCommand = false)
{
    /// <summary>
    /// The exit code every POSIX shell uses for "I could not find that program".
    /// </summary>
    /// <remarks>
    /// bash, zsh and fish all report it; PowerShell does not, and reports 1 for an unresolved name
    /// exactly as it does for a command that ran and failed. That gap is why the flag has a second
    /// source. Lives on the model rather than on <c>CapturePipeline</c> because the storage layer
    /// backfills against it too, and two copies of a magic number is how they drift.
    /// </remarks>
    public const int CommandNotFoundExitCode = 127;

    /// <summary>
    /// The first whitespace-delimited word of a command line - the name the shell tried to resolve -
    /// or an empty string when there is not one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately naive: no quoting, no environment-variable prefixes, no <c>sudo</c> unwrapping. It
    /// is used only to answer "is this the same mistyped program name", where the mistyped name is a
    /// bare word by construction - if it had been quoted or assigned the shell would have resolved
    /// something. A cleverer parse would widen what the retroactive flag reaches, and this is the one
    /// operation in the feature that writes to entries the user is not currently looking at.
    /// </para>
    /// </remarks>
    public static string FirstToken(string? commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return string.Empty;
        }

        string trimmed = commandText.TrimStart();
        int end = 0;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]))
        {
            end++;
        }

        return trimmed[..end];
    }
}

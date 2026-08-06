using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Domain;

public interface IHistoryStore
{
    Task AppendAsync(CommandHistoryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns entries that could plausibly match <paramref name="query"/>, most recent first,
    /// capped at <paramref name="maxCandidates"/>.
    /// </summary>
    /// <remarks>
    /// A recall gate, not a ranking. Relevance ordering belongs to
    /// <c>CommandAssistSuggestionEngine</c> and lives nowhere else; implementations must not score.
    /// </remarks>
    Task<IReadOnlyList<CommandHistoryEntry>> SearchAsync(string query, int maxCandidates, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CommandHistoryEntry>> GetRecentAsync(int maxResults, CancellationToken cancellationToken = default);
    /// <summary>
    /// Patches an entry with how the command turned out.
    /// </summary>
    /// <param name="isInvalidCommand">
    /// Whether the exit code proved the shell could not resolve the name (127). Only ever <em>sets</em>
    /// the flag: a later patch that does not know about the classification must not clear one an
    /// earlier one established, which is what makes the two signals in
    /// <c>CommandHistoryEntry.IsInvalidCommand</c> safe to apply in either order.
    /// </param>
    Task<bool> TryUpdateExecutionResultAsync(
        string entryId,
        int? exitCode,
        long? durationMs,
        CancellationToken cancellationToken = default,
        bool isInvalidCommand = false);

    /// <summary>
    /// Marks an entry as a command the shell could not resolve, so it is never suggested again.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="TryUpdateExecutionResultAsync"/> because it arrives separately: the
    /// classification that identifies a PowerShell typo comes from the Fix recogniser, after the exit
    /// code has already been written. See <c>CapturePipeline.MarkLastCommandInvalidAsync</c>.
    /// </remarks>
    Task<bool> TryMarkInvalidCommandAsync(string entryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks every stored entry whose command line starts with <paramref name="firstToken"/> as a
    /// command the shell could not resolve, and reports how many were newly flagged.
    /// </summary>
    /// <param name="firstToken">
    /// The unresolved program name, compared against <c>CommandHistoryEntry.FirstToken</c> of each
    /// entry. Blank does nothing.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>Dogfood round 4, item 4a.</strong> The per-entry flag only ever described the entry the
    /// classification arrived with, so a history written before the flag existed - which is every
    /// user's history, and was certainly the owner's - kept every one of its typos eligible. Learning
    /// that <c>gti</c> is not a program is a fact about the name, not about one execution of it, so it
    /// applies to every line that starts with that name.
    /// </para>
    /// <para>
    /// <strong>The caller must have established that the unresolved name is the command's own first
    /// token.</strong> A <c>command not found</c> raised from inside <c>npm run build</c> names a token
    /// that is not <c>npm</c>, and flagging by first token there would suppress every <c>npm</c> line
    /// in the user's history. See <c>CommandFixSuggestion.UnresolvedCommandToken</c>, which is only
    /// populated when the recogniser found the missing name in the command position.
    /// </para>
    /// </remarks>
    Task<int> TryMarkInvalidCommandsByFirstTokenAsync(string firstToken, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

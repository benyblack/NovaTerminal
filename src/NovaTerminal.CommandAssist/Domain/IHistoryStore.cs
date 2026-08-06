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

    Task ClearAsync(CancellationToken cancellationToken = default);
}

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
    Task<bool> TryUpdateExecutionResultAsync(string entryId, int? exitCode, long? durationMs, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

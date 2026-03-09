using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Domain;

public interface IHistoryStore
{
    Task AppendAsync(CommandHistoryEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CommandHistoryEntry>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CommandHistoryEntry>> GetRecentAsync(int maxResults, CancellationToken cancellationToken = default);
    Task<bool> TryUpdateExecutionResultAsync(string entryId, int? exitCode, long? durationMs, CancellationToken cancellationToken = default);
    Task<bool> TryUpdateExitCodeAsync(string entryId, int? exitCode, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

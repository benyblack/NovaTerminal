using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Domain;

public interface ISnippetStore
{
    Task<IReadOnlyList<CommandSnippet>> GetAllAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(CommandSnippet snippet, CancellationToken cancellationToken = default);
    Task RemoveAsync(string snippetId, CancellationToken cancellationToken = default);
}

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Domain;

public interface ICommandDocsProvider
{
    Task<IReadOnlyList<CommandHelpItem>> GetHelpAsync(CommandHelpQuery query, CancellationToken cancellationToken = default);
}

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.CommandAssist.Models;

namespace Ntilde.CommandAssist.Domain;

public interface ICommandDocsProvider
{
    Task<IReadOnlyList<CommandHelpItem>> GetHelpAsync(CommandHelpQuery query, CancellationToken cancellationToken = default);
}

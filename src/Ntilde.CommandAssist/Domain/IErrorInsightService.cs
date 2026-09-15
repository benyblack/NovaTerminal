using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.CommandAssist.Models;

namespace Ntilde.CommandAssist.Domain;

public interface IErrorInsightService
{
    Task<IReadOnlyList<CommandFixSuggestion>> AnalyzeAsync(CommandFailureContext context, CancellationToken cancellationToken = default);
}

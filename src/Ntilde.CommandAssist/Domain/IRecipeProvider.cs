using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.CommandAssist.Models;

namespace Ntilde.CommandAssist.Domain;

public interface IRecipeProvider
{
    Task<IReadOnlyList<CommandHelpItem>> GetRecipesAsync(CommandHelpQuery query, CancellationToken cancellationToken = default);
}

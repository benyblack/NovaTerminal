using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Domain;

public interface IRecipeProvider
{
    Task<IReadOnlyList<CommandHelpItem>> GetRecipesAsync(CommandHelpQuery query, CancellationToken cancellationToken = default);
}

using System.Collections.Generic;
using System.Linq;
using Ntilde.CommandAssist.ShellIntegration.Contracts;

namespace Ntilde.CommandAssist.ShellIntegration.Runtime;

public sealed class ShellIntegrationRegistry
{
    private readonly IReadOnlyList<IShellIntegrationProvider> _providers;

    public ShellIntegrationRegistry(IEnumerable<IShellIntegrationProvider> providers)
    {
        _providers = providers.ToList();
    }

    public IShellIntegrationProvider? GetProvider(string? shellKind, string? shellCommand)
    {
        return _providers.FirstOrDefault(provider => provider.CanIntegrate(shellKind, shellCommand));
    }
}

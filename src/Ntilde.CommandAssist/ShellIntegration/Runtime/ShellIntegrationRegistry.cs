using System.Collections.Generic;
using System.Linq;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;

namespace NovaTerminal.CommandAssist.ShellIntegration.Runtime;

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

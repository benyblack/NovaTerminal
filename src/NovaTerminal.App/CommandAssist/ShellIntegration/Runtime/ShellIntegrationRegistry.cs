using System.Collections.Generic;
using System.Linq;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using NovaTerminal.Core;

namespace NovaTerminal.CommandAssist.ShellIntegration.Runtime;

public sealed class ShellIntegrationRegistry
{
    private readonly IReadOnlyList<IShellIntegrationProvider> _providers;

    public ShellIntegrationRegistry(IEnumerable<IShellIntegrationProvider> providers)
    {
        _providers = providers.ToList();
    }

    public IShellIntegrationProvider? GetProvider(string? shellKind, TerminalProfile? profile)
    {
        return _providers.FirstOrDefault(provider => provider.CanIntegrate(shellKind, profile));
    }
}

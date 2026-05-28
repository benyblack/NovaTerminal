using NovaTerminal.Core;
using NovaTerminal.VT;

namespace NovaTerminal.CommandAssist.ShellIntegration.Contracts;

public interface IShellIntegrationProvider
{
    bool CanIntegrate(string? shellKind, TerminalProfile? profile);

    ShellIntegrationLaunchPlan CreateLaunchPlan(
        string shellCommand,
        string? shellArguments,
        string? workingDirectory);
}

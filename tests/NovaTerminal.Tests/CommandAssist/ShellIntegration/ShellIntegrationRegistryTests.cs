using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using NovaTerminal.CommandAssist.ShellIntegration.PowerShell;
using NovaTerminal.CommandAssist.ShellIntegration.Runtime;
using NovaTerminal.Core;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

public sealed class ShellIntegrationRegistryTests
{
    [Fact]
    public void GetProvider_ForPwshProfile_ReturnsPowerShellProvider()
    {
        var registry = new ShellIntegrationRegistry(new IShellIntegrationProvider[]
        {
            new PowerShellShellIntegrationProvider()
        });

        IShellIntegrationProvider? provider = registry.GetProvider(
            shellKind: "pwsh",
            profile: new TerminalProfile { Command = "pwsh.exe" });

        Assert.NotNull(provider);
        Assert.IsType<PowerShellShellIntegrationProvider>(provider);
    }

    [Fact]
    public void GetProvider_ForUnsupportedShell_ReturnsNull()
    {
        var registry = new ShellIntegrationRegistry(new IShellIntegrationProvider[]
        {
            new PowerShellShellIntegrationProvider()
        });

        IShellIntegrationProvider? provider = registry.GetProvider(
            shellKind: "posix",
            profile: new TerminalProfile { Command = "/bin/bash" });

        Assert.Null(provider);
    }
}

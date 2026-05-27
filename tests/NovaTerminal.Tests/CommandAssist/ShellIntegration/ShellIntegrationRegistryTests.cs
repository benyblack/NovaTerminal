using NovaTerminal.CommandAssist.Application;
using NovaTerminal.CommandAssist.ShellIntegration.Bash;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using NovaTerminal.CommandAssist.ShellIntegration.Fish;
using NovaTerminal.CommandAssist.ShellIntegration.PowerShell;
using NovaTerminal.CommandAssist.ShellIntegration.Runtime;
using NovaTerminal.CommandAssist.ShellIntegration.Zsh;
using NovaTerminal.Controls;
using NovaTerminal.Core;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

public sealed class ShellIntegrationRegistryTests
{
    [Theory]
    [InlineData("pwsh.exe", "pwsh")]
    [InlineData("powershell.exe", "pwsh")]
    [InlineData("cmd.exe", "cmd")]
    [InlineData("/bin/bash", "bash")]
    [InlineData("bash.exe", "bash")]
    [InlineData("/usr/bin/zsh", "zsh")]
    [InlineData("zsh", "zsh")]
    [InlineData("/usr/local/bin/fish", "fish")]
    [InlineData("fish", "fish")]
    [InlineData("/bin/sh", "sh")]
    [InlineData("sh", "sh")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void DetermineShellKind_ReturnsSpecificShellKinds(string? shellCommand, string expected)
    {
        Assert.Equal(expected, TerminalPane.DetermineShellKind(shellCommand));
    }

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

    [Fact]
    public void GetProvider_ForBashProfile_ReturnsBashProvider()
    {
        ShellIntegrationRegistry registry = CommandAssistInfrastructure.GetShellIntegrationRegistry();

        IShellIntegrationProvider? provider = registry.GetProvider(
            shellKind: "bash",
            profile: new TerminalProfile { Command = "/bin/bash" });

        Assert.IsType<BashShellIntegrationProvider>(provider);
    }

    [Fact]
    public void GetProvider_ForZshProfile_ReturnsZshProvider()
    {
        ShellIntegrationRegistry registry = CommandAssistInfrastructure.GetShellIntegrationRegistry();

        IShellIntegrationProvider? provider = registry.GetProvider(
            shellKind: "zsh",
            profile: new TerminalProfile { Command = "/bin/zsh" });

        Assert.IsType<ZshShellIntegrationProvider>(provider);
    }

    [Fact]
    public void GetProvider_ForFishProfile_ReturnsFishProvider()
    {
        ShellIntegrationRegistry registry = CommandAssistInfrastructure.GetShellIntegrationRegistry();

        IShellIntegrationProvider? provider = registry.GetProvider(
            shellKind: "fish",
            profile: new TerminalProfile { Command = "/usr/local/bin/fish" });

        Assert.IsType<FishShellIntegrationProvider>(provider);
    }
}

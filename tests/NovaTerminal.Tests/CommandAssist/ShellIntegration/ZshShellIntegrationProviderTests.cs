using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using NovaTerminal.CommandAssist.ShellIntegration.Zsh;
using NovaTerminal.Core;
using NovaTerminal.VT;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

public sealed class ZshShellIntegrationProviderTests
{
    [Theory]
    [InlineData("zsh", "/bin/zsh")]
    [InlineData("zsh", "zsh")]
    [InlineData("ZSH", "/usr/local/bin/zsh")]
    public void CanIntegrate_ForZshShells_ReturnsTrue(string shellKind, string command)
    {
        var provider = new ZshShellIntegrationProvider();

        Assert.True(provider.CanIntegrate(shellKind, new TerminalProfile { Command = command }));
    }

    [Theory]
    [InlineData("bash", "/bin/bash")]
    [InlineData("pwsh", "pwsh.exe")]
    [InlineData("fish", "/usr/local/bin/fish")]
    public void CanIntegrate_ForOtherShells_ReturnsFalse(string shellKind, string command)
    {
        var provider = new ZshShellIntegrationProvider();

        Assert.False(provider.CanIntegrate(shellKind, new TerminalProfile { Command = command }));
    }

    [Fact]
    public void CreateLaunchPlan_ForVanillaZsh_InjectsBootstrapViaZdotdirEnvOverride()
    {
        var provider = new ZshShellIntegrationProvider();

        ShellIntegrationLaunchPlan plan = provider.CreateLaunchPlan("/bin/zsh", shellArguments: null, workingDirectory: null);

        Assert.True(plan.IsIntegrated);
        Assert.NotNull(plan.BootstrapScriptPath);
        Assert.NotNull(plan.EnvironmentOverrides);
        Assert.True(plan.EnvironmentOverrides!.ContainsKey("ZDOTDIR"));
        // ZDOTDIR points to the directory containing our .zshrc, which is
        // also where BootstrapScriptPath was written.
        Assert.Equal(
            Path.GetDirectoryName(plan.BootstrapScriptPath!),
            plan.EnvironmentOverrides["ZDOTDIR"]);
    }

    [Fact]
    public void CreateLaunchPlan_WithExistingUserArguments_PreservesThem()
    {
        var provider = new ZshShellIntegrationProvider();

        ShellIntegrationLaunchPlan plan = provider.CreateLaunchPlan("/bin/zsh", "--login", null);

        Assert.True(plan.IsIntegrated);
        Assert.Contains("--login", plan.ShellArguments!);
    }

    [Theory]
    [InlineData("-c \"echo hi\"")]
    [InlineData("--no-rcs")]
    public void CreateLaunchPlan_WhenUserForcesIncompatibleStartupMode_DisablesIntegration(string userArgs)
    {
        var provider = new ZshShellIntegrationProvider();

        ShellIntegrationLaunchPlan plan = provider.CreateLaunchPlan("/bin/zsh", userArgs, null);

        Assert.False(plan.IsIntegrated);
        Assert.Null(plan.BootstrapScriptPath);
        Assert.Equal(userArgs, plan.ShellArguments);
    }
}

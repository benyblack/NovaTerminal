using NovaTerminal.CommandAssist.ShellIntegration.Bash;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using NovaTerminal.Core;
using NovaTerminal.VT;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

public sealed class BashShellIntegrationProviderTests
{
    [Theory]
    [InlineData("bash", "/bin/bash")]
    [InlineData("bash", "bash.exe")]
    [InlineData("BASH", "/usr/local/bin/bash")]
    public void CanIntegrate_ForBashShells_ReturnsTrue(string shellKind, string command)
    {
        var provider = new BashShellIntegrationProvider();

        Assert.True(provider.CanIntegrate(shellKind, new TerminalProfile { Command = command }));
    }

    [Theory]
    [InlineData("pwsh", "pwsh.exe")]
    [InlineData("zsh", "/bin/zsh")]
    [InlineData("cmd", "cmd.exe")]
    public void CanIntegrate_ForOtherShells_ReturnsFalse(string shellKind, string command)
    {
        var provider = new BashShellIntegrationProvider();

        Assert.False(provider.CanIntegrate(shellKind, new TerminalProfile { Command = command }));
    }

    [Fact]
    public void CreateLaunchPlan_ForVanillaBash_InjectsBootstrapViaRcfileAndMarksIntegrated()
    {
        var provider = new BashShellIntegrationProvider();

        ShellIntegrationLaunchPlan plan = provider.CreateLaunchPlan("/bin/bash", shellArguments: null, workingDirectory: null);

        Assert.True(plan.IsIntegrated);
        Assert.NotNull(plan.BootstrapScriptPath);
        Assert.NotNull(plan.ShellArguments);
        Assert.Contains("--rcfile", plan.ShellArguments!);
        Assert.Contains(plan.BootstrapScriptPath!, plan.ShellArguments!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-i", plan.ShellArguments!);
    }

    [Fact]
    public void CreateLaunchPlan_WithExistingUserArguments_PreservesThem()
    {
        var provider = new BashShellIntegrationProvider();

        ShellIntegrationLaunchPlan plan = provider.CreateLaunchPlan("/bin/bash", "--login", null);

        Assert.True(plan.IsIntegrated);
        Assert.Contains("--login", plan.ShellArguments!);
        Assert.Contains("--rcfile", plan.ShellArguments!);
    }

    [Theory]
    [InlineData("-c \"echo hi\"")]
    [InlineData("--rcfile /my/custom/rc")]
    [InlineData("--init-file /my/init")]
    public void CreateLaunchPlan_WhenUserForcesIncompatibleStartupMode_DisablesIntegration(string userArgs)
    {
        var provider = new BashShellIntegrationProvider();

        ShellIntegrationLaunchPlan plan = provider.CreateLaunchPlan("/bin/bash", userArgs, null);

        Assert.False(plan.IsIntegrated);
        Assert.Null(plan.BootstrapScriptPath);
        Assert.Equal(userArgs, plan.ShellArguments);
    }
}

using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using NovaTerminal.CommandAssist.ShellIntegration.PowerShell;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

public sealed class PowerShellShellIntegrationProviderTests
{
    [Fact]
    public void CreateLaunchPlan_WhenPowerShellEnabled_ReturnsIntegratedPlan()
    {
        var provider = new PowerShellShellIntegrationProvider();

        ShellIntegrationLaunchPlan plan = provider.CreateLaunchPlan(
            shellCommand: "pwsh.exe",
            shellArguments: "-NoLogo",
            workingDirectory: @"C:\repo");

        Assert.True(plan.IsIntegrated);
        Assert.Equal("pwsh.exe", plan.ShellCommand);
        Assert.Contains("-NoLogo", plan.ShellArguments);
        Assert.Contains("-NoExit", plan.ShellArguments);
        Assert.Contains("-File", plan.ShellArguments);
        Assert.Contains(plan.BootstrapScriptPath!, plan.ShellArguments, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(plan.BootstrapScriptPath);
    }

    [Fact]
    public void CanIntegrate_WhenShellKindIsPwsh_ReturnsTrue()
    {
        var provider = new PowerShellShellIntegrationProvider();

        bool supported = provider.CanIntegrate("pwsh", null);

        Assert.True(supported);
    }

    [Fact]
    public void CreateLaunchPlan_WhenUserAlreadySuppliesFileScript_DoesNotClaimIntegration()
    {
        var provider = new PowerShellShellIntegrationProvider();

        ShellIntegrationLaunchPlan plan = provider.CreateLaunchPlan(
            shellCommand: "pwsh.exe",
            shellArguments: "-File .\\user-script.ps1",
            workingDirectory: @"C:\repo");

        Assert.False(plan.IsIntegrated);
        Assert.Equal("pwsh.exe", plan.ShellCommand);
        Assert.Equal("-File .\\user-script.ps1", plan.ShellArguments);
        Assert.Null(plan.BootstrapScriptPath);
    }
}

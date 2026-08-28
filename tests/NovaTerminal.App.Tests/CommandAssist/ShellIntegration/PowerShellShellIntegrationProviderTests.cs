using NovaTerminal.Shell;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using NovaTerminal.CommandAssist.ShellIntegration.PowerShell;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

public sealed class PowerShellShellIntegrationProviderTests
{
    [Fact]
    public void CreateLaunchPlan_WhenPowerShellEnabled_ReturnsIntegratedPlan()
    {
        var provider = new PowerShellShellIntegrationProvider(() => BootstrapTestDirectory.ForCaller());

        ShellIntegrationLaunchPlan plan = provider.CreateLaunchPlan(
            shellCommand: "pwsh.exe",
            shellArguments: "-NoLogo",
            workingDirectory: @"C:\repo");

        Assert.True(plan.IsIntegrated);
        Assert.Equal("pwsh.exe", plan.ShellCommand);
        Assert.Contains("-NoLogo", plan.ShellArguments);
        Assert.Contains("-NoExit", plan.ShellArguments);
        Assert.Contains("-EncodedCommand", plan.ShellArguments!, StringComparison.Ordinal);
        Assert.NotNull(plan.BootstrapScriptPath);

        // The bootstrap path is deliberately NOT on the command line any more. It used to be
        // passed to -File, which the execution policy blocks on a stock Windows machine; the
        // script now travels as an encoded command instead. That also retired the whole -File
        // quoting hazard (unquoted path, 8.3 short-name fallback for usernames with spaces)
        // this test previously guarded — there is no path left to quote.
        // See PowerShellBootstrapExecutionPolicyTests for the encoding contract.
        Assert.DoesNotContain("-File", plan.ShellArguments!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            plan.BootstrapScriptPath!,
            plan.ShellArguments!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanIntegrate_WhenShellKindIsPwsh_ReturnsTrue()
    {
        var provider = new PowerShellShellIntegrationProvider(() => BootstrapTestDirectory.ForCaller());

        bool supported = provider.CanIntegrate("pwsh", null);

        Assert.True(supported);
    }

    [Fact]
    public void CreateLaunchPlan_WhenUserAlreadySuppliesFileScript_DoesNotClaimIntegration()
    {
        var provider = new PowerShellShellIntegrationProvider(() => BootstrapTestDirectory.ForCaller());

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

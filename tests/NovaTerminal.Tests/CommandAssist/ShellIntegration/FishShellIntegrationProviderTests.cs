using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using NovaTerminal.CommandAssist.ShellIntegration.Fish;
using NovaTerminal.Core;
using NovaTerminal.VT;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

public sealed class FishShellIntegrationProviderTests
{
    [Theory]
    [InlineData("fish", "/usr/local/bin/fish")]
    [InlineData("fish", "fish")]
    [InlineData("FISH", "/opt/homebrew/bin/fish")]
    public void CanIntegrate_ForFishShells_ReturnsTrue(string shellKind, string command)
    {
        var provider = new FishShellIntegrationProvider();

        Assert.True(provider.CanIntegrate(shellKind, new TerminalProfile { Command = command }));
    }

    [Theory]
    [InlineData("bash", "/bin/bash")]
    [InlineData("pwsh", "pwsh.exe")]
    [InlineData("zsh", "/bin/zsh")]
    public void CanIntegrate_ForOtherShells_ReturnsFalse(string shellKind, string command)
    {
        var provider = new FishShellIntegrationProvider();

        Assert.False(provider.CanIntegrate(shellKind, new TerminalProfile { Command = command }));
    }

    [Fact]
    public void CreateLaunchPlan_ForVanillaFish_InjectsBootstrapViaXdgConfigHomeOverride()
    {
        var provider = new FishShellIntegrationProvider();

        ShellIntegrationLaunchPlan plan = provider.CreateLaunchPlan("/usr/local/bin/fish", shellArguments: null, workingDirectory: null);

        Assert.True(plan.IsIntegrated);
        Assert.NotNull(plan.BootstrapScriptPath);
        Assert.NotNull(plan.EnvironmentOverrides);
        Assert.True(plan.EnvironmentOverrides!.ContainsKey("XDG_CONFIG_HOME"));
        // XDG_CONFIG_HOME points at the parent of the fish/ directory
        // that contains config.fish.
        string fishDir = Path.GetDirectoryName(plan.BootstrapScriptPath!)!;
        string expectedXdg = Path.GetDirectoryName(fishDir)!;
        Assert.Equal(expectedXdg, plan.EnvironmentOverrides["XDG_CONFIG_HOME"]);
    }

    [Fact]
    public void CreateLaunchPlan_WithExistingUserArguments_PreservesThem()
    {
        var provider = new FishShellIntegrationProvider();

        ShellIntegrationLaunchPlan plan = provider.CreateLaunchPlan("/usr/local/bin/fish", "--login", null);

        Assert.True(plan.IsIntegrated);
        Assert.Contains("--login", plan.ShellArguments!);
    }

    [Theory]
    [InlineData("-c \"echo hi\"")]
    [InlineData("--no-config")]
    [InlineData("-N")]
    public void CreateLaunchPlan_WhenUserForcesIncompatibleStartupMode_DisablesIntegration(string userArgs)
    {
        var provider = new FishShellIntegrationProvider();

        ShellIntegrationLaunchPlan plan = provider.CreateLaunchPlan("/usr/local/bin/fish", userArgs, null);

        Assert.False(plan.IsIntegrated);
        Assert.Null(plan.BootstrapScriptPath);
        Assert.Equal(userArgs, plan.ShellArguments);
    }
}

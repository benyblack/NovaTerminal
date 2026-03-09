using NovaTerminal.Core;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

public sealed class ShellIntegrationSettingsTests
{
    [Fact]
    public void TerminalSettings_DefaultsEnableShellIntegration()
    {
        var settings = new TerminalSettings();

        Assert.True(settings.CommandAssistShellIntegrationEnabled);
        Assert.True(settings.CommandAssistPowerShellIntegrationEnabled);
    }
}

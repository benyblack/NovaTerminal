using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class CommandAssistDefaultDisabledTests
{
    // Command Assist ships disabled by default for 0.3 — the feature isn't ready yet.
    // The master CommandAssistEnabled flag gates IsCommandAssistFeatureEnabled(); users
    // can still opt in via Settings. Sub-feature defaults are intentionally left on so
    // that opting in gives the full experience.
    [Fact]
    public void CommandAssist_IsDisabledByDefault()
    {
        var settings = new TerminalSettings();

        Assert.False(settings.CommandAssistEnabled);
    }
}

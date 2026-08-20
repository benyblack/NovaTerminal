using NovaTerminal.Shell;

namespace NovaTerminal.Tests.Core;

public sealed class TerminalSettingsDefaultsTests
{
    [Fact]
    public void ExperimentalNativeSshEnabled_DefaultsOn()
    {
        // The default-flip contract: fresh installs (and settings files predating the field) get
        // the native backend enabled, which is what lets new profiles default to it. A stored
        // explicit false still wins at load time — this only pins the constructed default.
        Assert.True(new TerminalSettings().ExperimentalNativeSshEnabled);
    }
}

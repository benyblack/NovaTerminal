using System.Text.Json;
using NovaTerminal.Shell;

namespace NovaTerminal.Architecture.Tests;

public sealed class TabStripSettingsTests
{
    [Fact]
    public void Defaults_AreHorizontalWith220Width()
    {
        var settings = new TerminalSettings();
        Assert.Equal("Horizontal", settings.TabStripOrientation);
        Assert.Equal(220, settings.VerticalTabStripWidth);
    }

    [Fact]
    public void RoundTrip_PreservesTabStripSettings()
    {
        var settings = new TerminalSettings { TabStripOrientation = "Vertical", VerticalTabStripWidth = 300 };
        string json = JsonSerializer.Serialize(settings, AppJsonContext.Default.TerminalSettings);
        var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalSettings);
        Assert.NotNull(back);
        Assert.Equal("Vertical", back!.TabStripOrientation);
        Assert.Equal(300, back.VerticalTabStripWidth);
    }

    [Fact]
    public void EmptyJson_UpgradesToDefaults()
    {
        var settings = JsonSerializer.Deserialize("{}", AppJsonContext.Default.TerminalSettings);
        Assert.NotNull(settings);
        Assert.Equal("Horizontal", settings!.TabStripOrientation);
        Assert.Equal(220, settings.VerticalTabStripWidth);
    }
}

using NovaTerminal.Core;

namespace NovaTerminal.Tests.Core;

public sealed class SettingsWindowFontChoicesTests
{
    [Fact]
    public void BuildFontFamilyChoices_AddsBundledDefaultWhenMissingFromSystemFonts()
    {
        string[] systemFonts = ["Consolas", "JetBrains Mono"];

        List<string> choices = SettingsWindow.BuildFontFamilyChoices(systemFonts, BundledFontCatalog.DefaultTerminalFontFamily);

        Assert.Contains(BundledFontCatalog.DefaultTerminalFontFamily, choices);
    }

    [Fact]
    public void BuildFontFamilyChoices_KeepsConfiguredFontVisible()
    {
        string[] systemFonts = ["Consolas", "JetBrains Mono"];

        List<string> choices = SettingsWindow.BuildFontFamilyChoices(systemFonts, "Fira Code");

        Assert.Contains("Fira Code", choices);
    }
}

using NovaTerminal.Shell;
using NovaTerminal.Platform;
using NovaTerminal.VT;

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
    public void BuildFontFamilyChoices_OffersEverySelectableBundledFamily()
    {
        // Bundled fonts other than the default are usually not installed
        // system-wide, so without this they would ship in the binary and be
        // unpickable - which is exactly how Cascadia Mono PL would have vanished
        // from this list when it stopped being the default.
        string[] systemFonts = ["Consolas"];

        List<string> choices = SettingsWindow.BuildFontFamilyChoices(systemFonts, BundledFontCatalog.DefaultTerminalFontFamily);

        foreach (string bundled in BundledFontCatalog.SelectableFamilies)
        {
            Assert.Contains(bundled, choices);
        }
    }

    [Fact]
    public void BuildFontFamilyChoices_NeverOffersTheSymbolsOnlyFont()
    {
        string[] systemFonts = ["Consolas"];

        List<string> choices = SettingsWindow.BuildFontFamilyChoices(systemFonts, BundledFontCatalog.DefaultTerminalFontFamily);

        Assert.DoesNotContain(BundledFontCatalog.SymbolsFontFamily, choices);
    }

    [Fact]
    public void BuildFontFamilyChoices_KeepsConfiguredFontVisible()
    {
        string[] systemFonts = ["Consolas", "JetBrains Mono"];

        List<string> choices = SettingsWindow.BuildFontFamilyChoices(systemFonts, "Fira Code");

        Assert.Contains("Fira Code", choices);
    }
}

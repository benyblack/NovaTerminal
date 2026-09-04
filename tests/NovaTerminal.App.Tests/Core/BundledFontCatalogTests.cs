using NovaTerminal.Shell;
using Avalonia.Media;
using NovaTerminal.Platform;
using NovaTerminal.VT;

namespace NovaTerminal.Tests.Core;

public sealed class BundledFontCatalogTests
{
    [Fact]
    public void FontFamilyMappings_MapEverySelectableBundledFamilyToItsAsset()
    {
        IReadOnlyDictionary<string, FontFamily> mappings = BundledFontCatalog.CreateFontFamilyMappings();

        Assert.True(mappings.TryGetValue(BundledFontCatalog.DefaultTerminalFontFamily, out FontFamily? defaultFamily));
        Assert.Contains("JetBrainsMonoNL-Regular.ttf", defaultFamily!.ToString(), StringComparison.Ordinal);
        Assert.Contains("#JetBrains Mono NL", defaultFamily.ToString(), StringComparison.Ordinal);

        // Still mapped after ceasing to be the default: every settings.json written
        // before the change names it, and dropping it would move those users off it.
        Assert.True(mappings.TryGetValue(BundledFontCatalog.CascadiaFontFamily, out FontFamily? cascadia));
        Assert.Contains("CascadiaMonoPL-Regular.otf", cascadia!.ToString(), StringComparison.Ordinal);
        Assert.Contains("#Cascadia Mono PL", cascadia.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FontFamilyMappings_ExcludeTheSymbolsOnlyFont()
    {
        // It has no ASCII. Reachable by Avalonia's own font fallback it could render
        // UI text as blanks, so it is deliberately not a mapped family - the glyph
        // fallback chain loads it through TryCreateSkTypeface instead.
        IReadOnlyDictionary<string, FontFamily> mappings = BundledFontCatalog.CreateFontFamilyMappings();

        Assert.False(mappings.ContainsKey(BundledFontCatalog.SymbolsFontFamily));
        Assert.DoesNotContain(BundledFontCatalog.SymbolsFontFamily, BundledFontCatalog.SelectableFamilies);
    }

    [Theory]
    [InlineData("JetBrains Mono NL")]
    [InlineData("Cascadia Mono PL")]
    [InlineData("Symbols Nerd Font Mono")]
    public void TryCreateSkTypeface_LoadsEachBundledFamilyUnderItsOwnName(string family)
    {
        using var typeface = BundledFontCatalog.TryCreateSkTypeface(family);

        Assert.NotNull(typeface);
        Assert.Equal(family, typeface!.FamilyName);
        Assert.True(BundledFontCatalog.IsBundledFamily(family));
    }

    [Fact]
    public void TryCreateSkTypeface_ReturnsNullForAFamilyThatIsNotBundled()
    {
        Assert.Null(BundledFontCatalog.TryCreateSkTypeface("Comic Sans MS"));
        Assert.False(BundledFontCatalog.IsBundledFamily("Comic Sans MS"));
    }

    [Fact]
    public void TheBundledFontsCoverTextAndIconsBetweenThem()
    {
        // The whole point of bundling a symbols font next to a text font: neither
        // covers both, and the fallback chain composes them. If a future font swap
        // breaks this pairing, icon glyphs silently become notdef boxes.
        using var text = BundledFontCatalog.TryCreateSkTypeface(BundledFontCatalog.DefaultTerminalFontFamily);
        using var symbols = BundledFontCatalog.TryCreateSkTypeface(BundledFontCatalog.SymbolsFontFamily);

        Assert.NotNull(text);
        Assert.NotNull(symbols);

        Assert.True(text!.ContainsGlyph('A'), "the default face must have ASCII");
        Assert.True(text.ContainsGlyph(0x2502), "the default face must have box drawing");
        Assert.False(text.ContainsGlyph(0xF09B), "a plain monospace face has no icon glyphs - that is why the symbols font is bundled");

        Assert.True(symbols!.ContainsGlyph(0xF09B), "the symbols font must carry icon glyphs");
        Assert.True(symbols.ContainsGlyph(0xE5FF), "the symbols font must carry file-type icons");
        Assert.False(symbols.ContainsGlyph('A'), "the symbols font must stay symbols-only, or it could shadow a real face");
    }

    [Fact]
    public void GetBundledFontData_CachesLoadedFontData()
    {
        var first = BundledFontCatalog.GetBundledFontData();
        var second = BundledFontCatalog.GetBundledFontData();

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void TryCreateSkTypeface_ReturnsIndependentInstances()
    {
        using var first = BundledFontCatalog.TryCreateSkTypeface(BundledFontCatalog.DefaultTerminalFontFamily);
        using var second = BundledFontCatalog.TryCreateSkTypeface(BundledFontCatalog.DefaultTerminalFontFamily);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }
}

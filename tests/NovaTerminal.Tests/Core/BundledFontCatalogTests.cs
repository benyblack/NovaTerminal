using Avalonia.Media;
using NovaTerminal.Core;

namespace NovaTerminal.Tests.Core;

public sealed class BundledFontCatalogTests
{
    [Fact]
    public void FontFamilyMappings_IncludeBundledCascadiaMonoPl()
    {
        IReadOnlyDictionary<string, FontFamily> mappings = BundledFontCatalog.CreateFontFamilyMappings();

        Assert.True(mappings.TryGetValue(BundledFontCatalog.DefaultTerminalFontFamily, out FontFamily? mappedFamily));
        Assert.NotNull(mappedFamily);
        Assert.Contains("CascadiaMonoPL-Regular.otf", mappedFamily!.ToString(), StringComparison.Ordinal);
        Assert.Contains("#Cascadia Mono PL", mappedFamily.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreateSkTypeface_LoadsBundledCascadiaMonoPl()
    {
        using var typeface = BundledFontCatalog.TryCreateSkTypeface(BundledFontCatalog.DefaultTerminalFontFamily);

        Assert.NotNull(typeface);
        Assert.Equal(BundledFontCatalog.DefaultTerminalFontFamily, typeface!.FamilyName);
    }
}

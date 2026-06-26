using NovaTerminal.Rendering;
using SkiaSharp;

namespace NovaTerminal.Rendering.Tests;

// Regression tests for #125: an atlas overflow must not wipe the entire glyph cache (which forced
// the whole visible glyph set to be re-rasterized on the next frame). Overflow now keeps the
// most-recently-used working set and is surfaced via RendererStatistics.GlyphAtlasResets.
public class GlyphCacheTests
{
    [Fact]
    public void NormalGlyph_IsCachedAndReused_WithoutAtlasReset()
    {
        long resetsBefore = RendererStatistics.GlyphAtlasResets;

        using var cache = new GlyphCache();
        using var typeface = SKTypeface.Default;
        using var font = new SKFont(typeface, 16);

        var first = cache.GetOrAdd("A", font, 1.0f);
        var second = cache.GetOrAdd("A", font, 1.0f); // cache hit

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(1, cache.EntryCount);
        Assert.Equal(resetsBefore, RendererStatistics.GlyphAtlasResets); // no overflow for one glyph
    }

    [Fact]
    public void AtlasOverflow_RetainsHotWorkingSet_InsteadOfFullWipe()
    {
        long resetsBefore = RendererStatistics.GlyphAtlasResets;

        using var cache = new GlyphCache();
        using var typeface = SKTypeface.Default;

        // Add many large, distinct glyphs (size is part of the cache key) to overflow the
        // 1024x1024 atlas. Stop as soon as the first overflow/reset is recorded so EntryCount
        // reflects the state immediately after the rebuild.
        int added = 0;
        for (int i = 0; i < 2000 && RendererStatistics.GlyphAtlasResets == resetsBefore; i++)
        {
            float size = 100 + (i % 80); // large glyphs so the atlas fills quickly
            char c = (char)('A' + (i % 26));
            using var font = new SKFont(typeface, size);
            cache.GetOrAdd(c.ToString(), font, 1.0f);
            added++;
        }

        // The atlas overflowed at least once ...
        Assert.True(RendererStatistics.GlyphAtlasResets > resetsBefore, "expected an atlas overflow to be recorded");
        // ... and the hot working set survived rather than being wiped to a single entry (the old
        // ClearInternal behaviour would have left ~1 entry right after the reset).
        Assert.True(cache.EntryCount > 1, $"hot working set not retained after overflow: {cache.EntryCount} entries");
        Assert.True(cache.EntryCount < added, "some cold glyphs should have been dropped on overflow");
    }
}

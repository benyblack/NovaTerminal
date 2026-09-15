using System;
using System.Collections.Generic;
using System.Globalization;
using NovaTerminal.Rendering;
using SkiaSharp;

namespace NovaTerminal.Rendering.Tests;

/// <summary>
/// #172 item 2: the atlas packed each glyph into its <em>advance</em> box — <c>ceil(MeasureText)</c>
/// wide by <c>ceil(descent - ascent)</c> tall, drawn at <c>y = round(-ascent)</c> — and anything whose
/// ink reached outside that box was cropped by the atlas clip.
///
/// The test is written to be font-agnostic, because CI runs on both Windows and Ubuntu and they share
/// no fonts. Rather than naming glyphs known to overhang, it rasterizes each sample twice: once into a
/// deliberately oversized bitmap where nothing can possibly be clipped, and once through the cache,
/// then compares the two ink bounding boxes. Any disagreement is the atlas losing pixels.
/// </summary>
public class GlyphInkBoundsTests
{
    private static readonly bool SkiaAvailable = CheckSkiaAvailable();

    private static bool CheckSkiaAvailable()
    {
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(1, 1, SKColorType.Rgba8888));
            return surface != null;
        }
        catch
        {
            return false;
        }
    }

    // Latin Extended-A capitals with tall diacritics and a few glyphs with side bearings that reach
    // past the advance. Whichever of these the runner's default font actually has is enough; the test
    // asserts that at least one really does overhang, so it cannot pass vacuously.
    private static readonly string[] Samples =
    {
        "A", "j", "f", "W", "g", "@", "_", "|", "/",
        "Ã", "Å", "Ñ", "Õ", "Æ",
        "ď", "ĥ", "Ĩ", "Ũ", "ĺ", "ŀ",
        "á", "Á",
    };

    [Theory]
    [InlineData(1.0f)]
    [InlineData(1.5f)]
    [InlineData(2.0f)]
    public void EveryGlyphKeepsAllOfItsInk(float scale)
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        using var typeface = SKTypeface.Default;
        using var font = new SKFont(typeface, 14f);
        using var cache = new GlyphCache();

        int overhangingSamples = 0;
        var failures = new List<string>();

        foreach (string text in Samples)
        {
            SKSizeI reference = ReferenceInkSize(typeface, 14f * scale, text);
            if (reference.Width == 0 || reference.Height == 0) continue; // no ink; nothing to lose

            if (ExceedsLegacyAdvanceBox(typeface, 14f * scale, text)) overhangingSamples++;

            var sprite = cache.GetOrAdd(text, font, scale);
            Assert.NotNull(sprite);

            SKSizeI packed = PackedInkSize(cache, sprite!.Value);

            // One pixel of slack each way: the reference and the atlas rasterize through separate
            // Skia surfaces, and a faint antialiased edge can land on either side of the alpha
            // threshold. Cropping loses whole rows or columns, well outside that.
            if (Math.Abs(packed.Width - reference.Width) > 1 || Math.Abs(packed.Height - reference.Height) > 1)
            {
                failures.Add($"{Describe(text)}: atlas ink {packed.Width}x{packed.Height}, "
                    + $"unclipped ink {reference.Width}x{reference.Height}");
            }
        }

        Assert.True(
            overhangingSamples > 0,
            "no sampled glyph overhangs the legacy advance box in this font, so this test would pass "
            + "whether or not the atlas crops. Add a sample that overhangs.");

        Assert.True(
            failures.Count == 0,
            $"the atlas cropped {failures.Count} of {Samples.Length} glyphs (#172 item 2):\n  "
            + string.Join("\n  ", failures));
    }

    [Fact]
    public void SpriteOffsetsPlaceTheInkWhereTheGlyphWouldHaveDrawnIt()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        // Packing the ink correctly but blitting it in the old place is the obvious way to get this
        // wrong, and the size comparison above would not notice. This pins the bearings: the sprite's
        // offsets must reproduce the ink's position relative to the pen origin.
        using var typeface = SKTypeface.Default;
        using var font = new SKFont(typeface, 14f);
        using var cache = new GlyphCache();

        foreach (string text in Samples)
        {
            using var physFont = new SKFont(typeface, 14f)
            {
                Edging = SKFontEdging.Antialias,
                Hinting = SKFontHinting.Full,
                Subpixel = true
            };
            physFont.MeasureText(text, out SKRect bounds);
            if (bounds.IsEmpty) continue;

            var sprite = cache.GetOrAdd(text, font, 1.0f);
            Assert.NotNull(sprite);

            Assert.Equal((int)Math.Floor(bounds.Left), sprite!.Value.OffsetX);
            Assert.Equal((int)Math.Floor(bounds.Top), sprite.Value.OffsetY);
        }
    }

    [Fact]
    public void AGlyphLargerThanTheAtlasIsDeclinedWithoutResettingIt()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        // Ink boxes can exceed advance boxes, so a huge font size can now demand more than a whole
        // atlas surface. Evicting cannot help, and resetting on every frame forever is much worse
        // than simply not caching the glyph - so the cache must decline it and leave itself alone.
        using var typeface = SKTypeface.Default;
        using var font = new SKFont(typeface, GlyphAtlas.AtlasSize * 4f);
        using var cache = new GlyphCache();

        // Populate with something ordinary first, so a reset would be observable as a lost entry.
        using var smallFont = new SKFont(typeface, 14f);
        Assert.NotNull(cache.GetOrAdd("A", smallFont, 1.0f));
        long resetsBefore = RendererStatistics.GlyphAtlasResets;

        Assert.Null(cache.GetOrAdd("W", font, 1.0f));

        Assert.Equal(resetsBefore, RendererStatistics.GlyphAtlasResets);
        Assert.Equal(1, cache.EntryCount);
        Assert.NotNull(cache.GetOrAdd("A", smallFont, 1.0f));
    }

    /// <summary>Ink size from a bitmap big enough that clipping is impossible.</summary>
    private static SKSizeI ReferenceInkSize(SKTypeface typeface, float physicalSize, string text)
    {
        SKRectI ink = ReferenceInkRelativeToPen(typeface, physicalSize, text);
        return ink.IsEmpty ? new SKSizeI(0, 0) : new SKSizeI(ink.Width, ink.Height);
    }

    /// <summary>
    /// Rasterized ink bounds relative to the pen origin, from a bitmap big enough that nothing can be
    /// clipped. Rasterized rather than taken from <c>MeasureText</c> on purpose: hinted glyph bounds
    /// are rounded outward, so they report up to a pixel of overhang that has no ink in it. Measuring
    /// pixels keeps the comparison honest in both directions.
    /// </summary>
    private static SKRectI ReferenceInkRelativeToPen(SKTypeface typeface, float physicalSize, string text)
    {
        int extent = (int)Math.Ceiling(physicalSize * 6) + 16;
        int penX = extent / 3;
        int penY = extent / 2;

        using var bitmap = new SKBitmap(extent, extent, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        using var physFont = new SKFont(typeface, physicalSize)
        {
            Edging = SKFontEdging.Antialias,
            Hinting = SKFontHinting.Full,
            Subpixel = true
        };
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White };
        canvas.DrawText(text, penX, penY, physFont, paint);

        SKRectI ink = InkRect(bitmap, 0, 0, extent, extent);
        return ink.IsEmpty
            ? SKRectI.Empty
            : new SKRectI(ink.Left - penX, ink.Top - penY, ink.Right - penX, ink.Bottom - penY);
    }

    /// <summary>Ink size within a sprite's rect in the atlas.</summary>
    private static SKSizeI PackedInkSize(GlyphCache cache, GlyphSprite sprite)
    {
        var (alpha, color) = cache.GetAtlasImages();
        using (alpha)
        using (color)
        {
            using SKBitmap bitmap = SKBitmap.FromImage(sprite.Type == AtlasType.Alpha8 ? alpha : color);
            return InkSize(
                bitmap,
                (int)sprite.Rect.Left,
                (int)sprite.Rect.Top,
                (int)sprite.Rect.Width,
                (int)sprite.Rect.Height);
        }
    }

    private static SKSizeI InkSize(SKBitmap bitmap, int x0, int y0, int w, int h)
    {
        SKRectI ink = InkRect(bitmap, x0, y0, w, h);
        return ink.IsEmpty ? new SKSizeI(0, 0) : new SKSizeI(ink.Width, ink.Height);
    }

    /// <summary>Half-open bounds of the pixels with meaningful alpha in the given region.</summary>
    private static SKRectI InkRect(SKBitmap bitmap, int x0, int y0, int w, int h)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int y = y0; y < y0 + h && y < bitmap.Height; y++)
        {
            for (int x = x0; x < x0 + w && x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha > 8)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        return maxX < minX ? SKRectI.Empty : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>Whether this glyph really loses pixels to the box the atlas used to pack it into.</summary>
    private static bool ExceedsLegacyAdvanceBox(SKTypeface typeface, float physicalSize, string text)
    {
        using var physFont = new SKFont(typeface, physicalSize)
        {
            Edging = SKFontEdging.Antialias,
            Hinting = SKFontHinting.Full,
            Subpixel = true
        };
        var metrics = physFont.Metrics;
        int boxW = Math.Max(1, (int)Math.Ceiling(physFont.MeasureText(text)));
        int boxH = (int)Math.Ceiling(metrics.Descent - metrics.Ascent);
        int drawY = (int)Math.Round(-metrics.Ascent);

        SKRectI ink = ReferenceInkRelativeToPen(typeface, physicalSize, text);
        return !ink.IsEmpty
            && (ink.Left < 0 || ink.Right > boxW || ink.Top < -drawY || ink.Bottom > boxH - drawY);
    }

    private static string Describe(string s)
        => string.Join("+", System.Linq.Enumerable.Select(s.EnumerateRunes(), r => "U+" + r.Value.ToString("X4", CultureInfo.InvariantCulture)));
}

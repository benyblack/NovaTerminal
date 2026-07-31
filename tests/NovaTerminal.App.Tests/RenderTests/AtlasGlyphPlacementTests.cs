using System;
using System.Collections.Generic;
using System.Globalization;
using NovaTerminal.Shell;
using Avalonia.Headless.XUnit;
using NovaTerminal.Platform;
using NovaTerminal.Rendering;
using NovaTerminal.Tests.Infra;
using NovaTerminal.VT;
using SkiaSharp;
using Xunit;

namespace NovaTerminal.Tests.RenderTests
{
    /// <summary>
    /// #172 item 2, end to end: the glyph atlas is a cache, so a frame drawn through it must look like
    /// the same frame drawn without it. It did not — the atlas packed each glyph into its advance box
    /// and cropped any ink outside, most visibly the top row of the diacritic on capitals like Ã Å Ñ Õ.
    ///
    /// `GlyphInkBoundsTests` pins the packing inside the cache; this pins that the draw path puts the
    /// sprite where the glyph would have gone. Packing the ink correctly and then blitting it at the
    /// old anchor is the obvious way to half-fix this, and only a render-level check catches it.
    /// </summary>
    [Trait("Category", "RenderMetrics")]
    public sealed class AtlasGlyphPlacementTests
    {
        private static readonly CellMetrics Metrics = new()
        {
            CellWidth = 8.4f,
            CellHeight = 18.0f,
            Baseline = 14.0f,
            Ascent = 14.0f,
            Descent = 4.0f
        };

        private const int Cols = 24;
        private const int Rows = 2;

        /// <remarks>
        /// One glyph per frame, deliberately. Across a multi-glyph run the two paths legitimately
        /// disagree: without a glyph cache the run is drawn with a single <c>DrawText</c> that
        /// accumulates the font's own advances, while the cached path places each glyph on the terminal
        /// cell grid. That is a different (pre-existing) difference, and folding it in would make this
        /// test measure the wrong thing.
        /// </remarks>
        [AvaloniaTheory]
        [InlineData("A")]
        [InlineData("W")]
        [InlineData("j")] // negative left side bearing in many faces
        [InlineData("g")]
        [InlineData("_")] // sits below the baseline
        [InlineData("@")]
        [InlineData("Ã")] // tall diacritics - the ones actually cropped in Cascadia Code at 14px
        [InlineData("Å")]
        [InlineData("Ñ")]
        [InlineData("Õ")]
        [InlineData("Ĩ")]
        [InlineData("Ũ")]
        [InlineData("ď")] // ink past the advance on the right
        [InlineData("ĥ")] // ink past the origin on the left
        [InlineData("Æ")]
        [InlineData("á")]
        public void GlyphDrawnThroughTheAtlasLandsWhereTheUncachedGlyphDoes(string glyph)
        {
            using var glyphCache = new GlyphCache();
            using var cached = Render(glyph, glyphCache);
            using var direct = Render(glyph, glyphCache: null);

            SKRectI a = InkBounds(cached);
            SKRectI b = InkBounds(direct);

            Assert.False(a.IsEmpty, $"{Describe(glyph)} drew nothing through the atlas");
            Assert.False(b.IsEmpty, $"{Describe(glyph)} drew nothing without the atlas");

            // Exact, not approximate. Both paths rasterize the same outline at the same size onto the
            // same pixel grid, and measurement confirms every edge agrees to the pixel - so a
            // tolerance here would only be hiding something.
            Assert.True(
                a == b,
                $"{Describe(glyph)} ink through the atlas is [{a.Left},{a.Top},{a.Right},{a.Bottom}] "
                + $"but [{b.Left},{b.Top},{b.Right},{b.Bottom}] without it. The atlas is either cropping "
                + "the glyph or blitting it in the wrong place (#172 item 2).");
        }

        /// <summary>Bounding box of everything that differs from the background colour.</summary>
        private static SKRectI InkBounds(SKBitmap bitmap)
        {
            SKColor background = bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y) != background)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            return maxX < minX ? SKRectI.Empty : new SKRectI(minX, minY, maxX, maxY);
        }

        private static SKBitmap Render(string content, GlyphCache? glyphCache)
        {
            var buffer = new TerminalBuffer(Cols, Rows);
            var parser = new AnsiParser(buffer);
            parser.Process(content);

            return SnapshotService.Capture(
                buffer,
                Metrics,
                (int)(Cols * Metrics.CellWidth),
                (int)(Rows * Metrics.CellHeight),
                new SnapshotCaptureOptions { HideCursor = true, GlyphCache = glyphCache });
        }

        private static string Describe(string s)
            => string.Join("+", System.Linq.Enumerable.Select(s.EnumerateRunes(), r => "U+" + r.Value.ToString("X4", CultureInfo.InvariantCulture)));
    }
}

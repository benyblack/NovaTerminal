using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using NovaTerminal.Core;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace NovaTerminal.Tests.RenderTests
{
    public class BlockSeamRegressionTests
    {
        private static readonly CellMetrics Metrics = new()
        {
            CellWidth = 8.4f,
            CellHeight = 18.0f,
            Baseline = 14.0f,
            Ascent = 14.0f,
            Descent = 4.0f
        };

        [AvaloniaFact]
        public void GlyphCache_DoesNotIntroduceAdditionalBlockSeams_AtFractionalScaling()
        {
            const int cols = 80;
            const int rows = 3;
            const int blockCells = 32;
            const string blocks = "\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588";
            const double renderScaling = 1.5;

            using var noCache = RenderRow(blocks, cols, rows, renderScaling, useGlyphCache: false);
            using var withCache = RenderRow(blocks, cols, rows, renderScaling, useGlyphCache: true);

            int x1 = (int)Math.Round(4.0, MidpointRounding.AwayFromZero);
            int x2 = (int)Math.Round(4.0 + (blockCells * Metrics.CellWidth), MidpointRounding.AwayFromZero);
            int y = (int)Math.Round(Metrics.CellHeight * 0.5, MidpointRounding.AwayFromZero);

            SKColor fg = new SKColor(255, 255, 255, 255);
            SKColor bg = new SKColor(0, 0, 0, 255);
            int noCacheGapCols = CountBackgroundLikeColumns(noCache, x1, x2, y, fg, bg);
            int withCacheGapCols = CountBackgroundLikeColumns(withCache, x1, x2, y, fg, bg);

            Assert.True(
                withCacheGapCols <= noCacheGapCols,
                $"Glyph cache introduced more seams: no-cache={noCacheGapCols}, with-cache={withCacheGapCols}, range=[{x1},{x2}), y={y}");
        }

        [AvaloniaFact]
        public void FullBlockRun_CellBoundaries_AreNotBackgroundLike_WithGlyphCache()
        {
            const int cols = 80;
            const int rows = 3;
            const int blockCells = 24;
            const string blocks = "\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588";
            const double renderScaling = 1.5;

            using var withCache = RenderRow(blocks, cols, rows, renderScaling, useGlyphCache: true);

            int y = (int)Math.Round(Metrics.CellHeight * 0.5, MidpointRounding.AwayFromZero);
            SKColor fg = new SKColor(255, 255, 255, 255);
            SKColor bg = new SKColor(0, 0, 0, 255);
            int boundaryBgLike = CountBoundaryBackgroundLikePixels(withCache, blockCells, Metrics.CellWidth, y, fg, bg);

            Assert.Equal(0, boundaryBgLike);
        }

        [AvaloniaFact]
        public void LowerBlockRun_CellBoundaries_AreNotBackgroundLike_WithGlyphCache()
        {
            const int cols = 80;
            const int rows = 3;
            const int blockCells = 24;
            const string blocks = "\u2581\u2581\u2581\u2581\u2581\u2581\u2581\u2581\u2581\u2581\u2581\u2581\u2581\u2581\u2581\u2581\u2581\u2581\u2581\u2581\u2581\u2581\u2581\u2581";
            const double renderScaling = 1.5;

            using var withCache = RenderRow(blocks, cols, rows, renderScaling, useGlyphCache: true);

            int y = Math.Clamp((int)Math.Round((Metrics.CellHeight * 0.96), MidpointRounding.AwayFromZero), 0, withCache.Height - 1);
            SKColor fg = new SKColor(255, 255, 255, 255);
            SKColor bg = new SKColor(0, 0, 0, 255);
            int boundaryBgLike = CountBoundaryBackgroundLikePixels(withCache, blockCells, Metrics.CellWidth, y, fg, bg);

            Assert.Equal(0, boundaryBgLike);
        }

        [AvaloniaFact]
        public void ShadeRun_CellBoundaries_AreNotBlack_WithGlyphCache()
        {
            const int cols = 80;
            const int rows = 3;
            const int blockCells = 24;
            const string shade = "\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592";
            const double renderScaling = 1.5;

            using var withCache = RenderRow(shade, cols, rows, renderScaling, useGlyphCache: true);

            int y = (int)Math.Round(Metrics.CellHeight * 0.5, MidpointRounding.AwayFromZero);
            int blackBoundaryHits = CountBoundaryPixelsNearBlack(withCache, blockCells, Metrics.CellWidth, y, maxLuma: 8);

            Assert.Equal(0, blackBoundaryHits);
        }

        [AvaloniaFact]
        public void BlackSquareGlyph_IsMappedAsCenteredFourEighthsHeightBlock()
        {
            MethodInfo? mapMethod = typeof(TerminalDrawOperation).GetMethod(
                "TryGetBlockFillRect",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(mapMethod);
            object[] args = { "\u25A0", 0, 0, 0, 0 };
            bool mapped = (bool)(mapMethod!.Invoke(null, args) ?? false);

            Assert.True(mapped);
            Assert.Equal(0, (int)args[1]);
            Assert.Equal(8, (int)args[2]);
            Assert.Equal(2, (int)args[3]);
            Assert.Equal(6, (int)args[4]);
        }

        private static SKBitmap RenderRow(string text, int cols, int rows, double renderScaling, bool useGlyphCache)
        {
            int width = (int)Math.Ceiling((cols * Metrics.CellWidth) + 8);
            int height = (int)Math.Ceiling(rows * Metrics.CellHeight);

            var buffer = new TerminalBuffer(cols, rows)
            {
                Theme = new TerminalTheme
                {
                    Foreground = TermColor.White,
                    Background = TermColor.Black,
                    CursorColor = TermColor.White
                }
            };
            buffer.Write(text);

            var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);

            var typeface = new Typeface("Cascadia Code, Consolas, Monospace");
            var glyphTypeface = typeface.GlyphTypeface;
            var skTypeface = new SharedSKTypeface(SKTypeface.FromFamilyName(typeface.FontFamily.Name));
            var skFont = new SharedSKFont(new SKFont(skTypeface.Typeface, 14));
            using var glyphCache = useGlyphCache ? new GlyphCache() : null;

            var op = new TerminalDrawOperation(
                new Rect(0, 0, width, height),
                buffer,
                scrollOffset: 0,
                selection: new SelectionState(),
                searchMatches: null,
                activeSearchIndex: -1,
                metrics: Metrics,
                typeface: typeface,
                fontSize: 14,
                glyphTypeface: glyphTypeface,
                skTypeface: skTypeface,
                skFont: skFont,
                enableLigatures: false,
                fallbackCache: new ConcurrentDictionary<string, SKTypeface?>(),
                fallbackChain: Array.Empty<SKTypeface>(),
                opacity: 1.0,
                transparentBackground: false,
                hideCursor: true,
                renderScaling: renderScaling,
                snapshotRows: buffer.Rows,
                snapshotCols: buffer.Cols,
                totalLines: buffer.TotalLines,
                cursorRow: buffer.CursorRow,
                cursorCol: buffer.CursorCol,
                rowCache: null,
                enableComplexShaping: true,
                glyphCache: glyphCache);

            try
            {
                MethodInfo? drawMethod = typeof(TerminalDrawOperation).GetMethod("DrawTerminal", BindingFlags.NonPublic | BindingFlags.Instance);
                drawMethod?.Invoke(op, new object[] { canvas });
                return bitmap;
            }
            finally
            {
                op.Dispose();
                skFont.Dispose();
                skTypeface.Dispose();
            }
        }

        private static int CountBackgroundLikeColumns(SKBitmap bitmap, int x1, int x2, int y, SKColor fg, SKColor bg)
        {
            int gapCols = 0;
            int left = Math.Max(0, x1);
            int right = Math.Min(bitmap.Width, x2);
            int row = Math.Clamp(y, 0, bitmap.Height - 1);

            for (int x = left; x < right; x++)
            {
                SKColor px = bitmap.GetPixel(x, row);
                int dFg = ColorDistanceSquared(px, fg);
                int dBg = ColorDistanceSquared(px, bg);
                if (dBg < dFg)
                {
                    gapCols++;
                }
            }

            return gapCols;
        }

        private static int CountBoundaryBackgroundLikePixels(SKBitmap bitmap, int cells, float cellWidth, int y, SKColor fg, SKColor bg)
        {
            int hits = 0;
            int row = Math.Clamp(y, 0, bitmap.Height - 1);
            for (int i = 1; i < cells; i++)
            {
                int x = (int)Math.Round(4.0 + (i * cellWidth), MidpointRounding.AwayFromZero);
                if (x < 0 || x >= bitmap.Width) continue;

                SKColor px = bitmap.GetPixel(x, row);
                int dFg = ColorDistanceSquared(px, fg);
                int dBg = ColorDistanceSquared(px, bg);
                if (dBg < dFg)
                {
                    hits++;
                }
            }

            return hits;
        }

        private static int ColorDistanceSquared(SKColor a, SKColor b)
        {
            int dr = a.Red - b.Red;
            int dg = a.Green - b.Green;
            int db = a.Blue - b.Blue;
            return (dr * dr) + (dg * dg) + (db * db);
        }

        private static int CountBoundaryPixelsNearBlack(SKBitmap bitmap, int cells, float cellWidth, int y, int maxLuma)
        {
            int hits = 0;
            int row = Math.Clamp(y, 0, bitmap.Height - 1);
            for (int i = 1; i < cells; i++)
            {
                int x = (int)Math.Round(4.0 + (i * cellWidth), MidpointRounding.AwayFromZero);
                if (x < 0 || x >= bitmap.Width) continue;

                SKColor px = bitmap.GetPixel(x, row);
                int luma = ((px.Red * 299) + (px.Green * 587) + (px.Blue * 114)) / 1000;
                if (luma <= maxLuma)
                {
                    hits++;
                }
            }

            return hits;
        }
    }
}

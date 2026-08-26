using Avalonia.Headless.XUnit;
using NovaTerminal.Platform;
using NovaTerminal.Rendering;
using NovaTerminal.Shell;
using NovaTerminal.Tests.Infra;
using NovaTerminal.VT;
using SkiaSharp;
using Xunit;

namespace NovaTerminal.Tests.RenderTests
{
    /// <summary>
    /// Box-drawing borders must survive fractional render scaling.
    /// </summary>
    /// <remarks>
    /// At RenderScaling 1.5 they did not: TerminalSnapshotRenderer passed the scaling down while
    /// handing the draw operation an unscaled DIP-sized canvas, so a stroke picked as a whole
    /// number of device pixels became a 0.67px rect, antialiasing was off, and it rasterised to
    /// nothing - a border that simply was not drawn.
    ///
    /// Deliberately not a golden-PNG test. GoldenSharedPng is filtered out of every other job and
    /// runs Windows-only, so a baseline is the one thing that would not have caught this on the
    /// machine it was found on. This asserts the pixels directly and runs everywhere.
    /// </remarks>
    public sealed class BoxDrawingRenderScalingTests
    {
        private const int BorderCells = 32;

        private static readonly CellMetrics Metrics = new()
        {
            CellWidth = 8.4f,
            CellHeight = 18.0f,
            Baseline = 14.0f,
            Ascent = 14.0f,
            Descent = 4.0f
        };

        [AvaloniaTheory]
        [InlineData(1.0)]
        [InlineData(1.25)]
        [InlineData(1.5)]
        [InlineData(1.75)]
        [InlineData(2.0)]
        [InlineData(3.0)]
        public void AHorizontalBorder_IsDrawnAtEveryRenderScaling(double renderScaling)
        {
            const int cols = 40;
            const int rows = 2;

            var buffer = new TerminalBuffer(cols, rows)
            {
                Theme = new TerminalTheme { Foreground = TermColor.White, Background = TermColor.Black }
            };
            new AnsiParser(buffer).Process(new string('\u2500', BorderCells));

            using var glyphCache = new GlyphCache();
            byte[] png = SnapshotService.CapturePng(
                buffer,
                Metrics,
                (int)(cols * Metrics.CellWidth),
                (int)(rows * Metrics.CellHeight),
                new SnapshotCaptureOptions
                {
                    ForceBoxDrawingPrimitives = true,
                    ForceBlockElementPrimitives = true,
                    GlyphCache = glyphCache,
                    RenderScaling = renderScaling,
                    HideCursor = true
                });

            using var bitmap = SKBitmap.Decode(png);
            Assert.NotNull(bitmap);

            // The capture is in device pixels, so it grows with the scaling.
            int expectedWidth = (int)System.Math.Round(cols * Metrics.CellWidth * renderScaling);
            Assert.InRange(bitmap.Width, expectedWidth - 2, expectedWidth + 2);

            int longestRun = LongestLitHorizontalRun(bitmap, out int litScanlines);
            int expectedRun = (int)(BorderCells * Metrics.CellWidth * renderScaling);

            // At least one scanline carries the border across essentially its whole width. A
            // sub-pixel stroke that rounds away produces zero here, which is the regression.
            Assert.True(
                longestRun >= expectedRun * 0.8,
                $"at scaling {renderScaling} the longest lit run was {longestRun}px, expected about {expectedRun}px "
                + $"({litScanlines} scanlines had any lit pixel at all) - the border did not render.");
        }

        private static int LongestLitHorizontalRun(SKBitmap bitmap, out int litScanlines)
        {
            int longest = 0;
            litScanlines = 0;

            for (int y = 0; y < bitmap.Height; y++)
            {
                int run = 0;
                bool anyLit = false;

                for (int x = 0; x < bitmap.Width; x++)
                {
                    SKColor pixel = bitmap.GetPixel(x, y);
                    // Foreground is white on a black/transparent ground, so anything bright is ink.
                    bool lit = pixel.Alpha > 0 && (pixel.Red + pixel.Green + pixel.Blue) / 3 > 32;
                    if (lit)
                    {
                        anyLit = true;
                        run++;
                        if (run > longest) longest = run;
                    }
                    else
                    {
                        run = 0;
                    }
                }

                if (anyLit) litScanlines++;
            }

            return longest;
        }
    }
}

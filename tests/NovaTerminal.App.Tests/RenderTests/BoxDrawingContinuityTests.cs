using Avalonia.Headless.XUnit;
using NovaTerminal.Platform;
using NovaTerminal.Rendering;
using NovaTerminal.Shell;
using NovaTerminal.Tests.Infra;
using NovaTerminal.VT;
using SkiaSharp;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace NovaTerminal.Tests.RenderTests
{
    /// <summary>
    /// Font-independent guards for box-drawing rendering.
    /// </summary>
    /// <remarks>
    /// The regression these exist for: font-supplied box glyphs do not span the full cell height on
    /// most installed fonts, so a stack of U+2502 rendered from the font leaves a gap at every row
    /// boundary and TUI borders (zellij, mc, lazygit) appear as dashed ladders. The primitive path
    /// fills cell-edge to cell-edge, so continuity is a structural property we can assert on pixels
    /// without depending on which fonts the machine has.
    ///
    /// Note the <see cref="GlyphCache"/> in every capture below. The box-primitive branch lives inside
    /// the per-cell loop that <c>DrawRowFromSnapshot</c> only enters when a glyph cache is present; the
    /// uncached branch is a single <c>canvas.DrawText(runText, ...)</c> with no primitive handling. A
    /// capture without a cache therefore renders font glyphs no matter what the primitive flag says.
    /// </remarks>
    [Collection("GoldenPng")]
    public sealed class BoxDrawingContinuityTests
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
        public void VerticalBorder_WithPrimitives_HasNoGapAtRowBoundaries()
        {
            const int cols = 4;
            const int rows = 4;

            var buffer = CreateThemedBuffer(cols, rows);
            var parser = new AnsiParser(buffer);
            parser.Process("│\r\n│\r\n│\r\n│");

            int width = (int)System.Math.Ceiling(cols * Metrics.CellWidth);
            int height = (int)System.Math.Ceiling(rows * Metrics.CellHeight);

            using var glyphCache = new GlyphCache();
            using SKBitmap bitmap = SnapshotService.Capture(buffer, Metrics, width, height, new SnapshotCaptureOptions
            {
                ForceBoxDrawingPrimitives = true,
                GlyphCache = glyphCache,
                HideCursor = true
            });

            // Scan the first cell column: each scanline is lit if any pixel in that cell's x range is.
            int cellRight = (int)System.Math.Ceiling(Metrics.CellWidth);
            bool[] lit = new bool[height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < cellRight && x < width; x++)
                {
                    if (!IsBackground(bitmap.GetPixel(x, y)))
                    {
                        lit[y] = true;
                        break;
                    }
                }
            }

            AssertContiguous(lit, rows * Metrics.CellHeight, "Vertical border", "scanline");
        }

        [AvaloniaFact]
        public void HorizontalBorder_WithPrimitives_HasNoGapBetweenCells()
        {
            const int cols = 8;
            const int rows = 2;

            var buffer = CreateThemedBuffer(cols, rows);
            var parser = new AnsiParser(buffer);
            parser.Process(new string('─', cols));

            int width = (int)System.Math.Ceiling(cols * Metrics.CellWidth);
            int height = (int)System.Math.Ceiling(rows * Metrics.CellHeight);

            using var glyphCache = new GlyphCache();
            using SKBitmap bitmap = SnapshotService.Capture(buffer, Metrics, width, height, new SnapshotCaptureOptions
            {
                ForceBoxDrawingPrimitives = true,
                GlyphCache = glyphCache,
                HideCursor = true
            });

            int firstRowBottom = (int)System.Math.Ceiling(Metrics.CellHeight);
            bool[] lit = new bool[width];
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < firstRowBottom && y < height; y++)
                {
                    if (!IsBackground(bitmap.GetPixel(x, y)))
                    {
                        lit[x] = true;
                        break;
                    }
                }
            }

            AssertContiguous(lit, cols * Metrics.CellWidth, "Horizontal border", "column");
        }

        /// <summary>
        /// Asserts the lit pixels form one unbroken span covering most of <paramref name="expectedExtent"/>.
        /// </summary>
        /// <remarks>
        /// Deliberately scoped to the interior of the drawn span rather than the whole bitmap: the pixel
        /// grid insets the cell origin by a few pixels of padding, so leading and trailing blank pixels
        /// are expected. The regression this guards is a break *between* cells. The extent floor stops
        /// the test passing trivially on a single lit pixel.
        /// </remarks>
        private static void AssertContiguous(bool[] lit, float expectedExtent, string what, string unit)
        {
            int first = System.Array.IndexOf(lit, true);
            Assert.True(first >= 0, $"{what} did not render at all.");

            int last = lit.Length - 1;
            while (!lit[last]) last--;

            List<int> gaps = Enumerable.Range(first, last - first + 1).Where(i => !lit[i]).ToList();
            Assert.True(
                gaps.Count == 0,
                $"{what} broke at {unit}(s) {string.Join(",", gaps.Take(12))} within span {first}..{last}; " +
                "expected one unbroken run.");

            int span = last - first + 1;
            int floor = (int)(expectedExtent * 0.9f);
            Assert.True(
                span >= floor,
                $"{what} spanned only {span} {unit}s; expected at least {floor} of {expectedExtent:F1}.");
        }

        [Fact]
        public void HandledCodepoints_CoverTheLightHeavyDoubleAndArcSets()
        {
            // Light, heavy, double, rounded arcs.
            int[] expectHandled =
            {
                0x2500, 0x2502, 0x250C, 0x2510, 0x2514, 0x2518, 0x251C, 0x2524, 0x252C, 0x2534, 0x253C,
                0x2501, 0x2503, 0x250F, 0x2513, 0x2517, 0x251B, 0x2523, 0x252B, 0x2533, 0x253B, 0x254B,
                0x2550, 0x2551, 0x2554, 0x2557, 0x255A, 0x255D, 0x2560, 0x2563, 0x2566, 0x2569, 0x256C,
                0x256D, 0x256E, 0x256F, 0x2570
            };

            foreach (int cp in expectHandled)
            {
                Assert.True(
                    TerminalDrawOperation.IsHandledBoxDrawingCodepoint(cp),
                    $"U+{cp:X4} should have a segment definition.");
            }
        }

        [Theory]
        // Dashed and dotted variants.
        [InlineData(0x2504)]
        [InlineData(0x2508)]
        [InlineData(0x254C)]
        // Mixed-weight joins.
        [InlineData(0x251D)]
        [InlineData(0x2541)]
        // Diagonals.
        [InlineData(0x2571)]
        [InlineData(0x2573)]
        // Outside the block entirely.
        [InlineData(0x2588)]
        [InlineData('A')]
        public void UnhandledCodepoints_AreReportedAsUnhandled_SoTheTextBatchIsNotFlushed(int cp)
        {
            // These fall through to font rendering. Reporting them as handled would cost a batch
            // flush per cell for no benefit, which is what the original guard did by testing the
            // whole 0x2500-0x257F range instead of the segment table.
            Assert.False(TerminalDrawOperation.IsHandledBoxDrawingCodepoint(cp));
        }

        private static bool IsBackground(SKColor c)
            => c.Red < 24 && c.Green < 24 && c.Blue < 24;

        private static TerminalBuffer CreateThemedBuffer(int cols, int rows)
        {
            return new TerminalBuffer(cols, rows)
            {
                Theme = new TerminalTheme
                {
                    Foreground = TermColor.White,
                    Background = TermColor.Black,
                    CursorColor = TermColor.White
                }
            };
        }
    }
}

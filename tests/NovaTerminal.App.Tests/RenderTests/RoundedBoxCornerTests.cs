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
    /// Shape guards for the rounded-corner arcs (U+256D-U+2570) the primitive renderer draws for
    /// Rich/Typer/lazygit style panels.
    /// </summary>
    /// <remarks>
    /// The regression these exist for: the arc was drawn as a quadratic whose control point sat on the
    /// arc <em>centre</em> instead of the corner vertex, with a radius of a whole half-cell and
    /// antialiasing off. The turn therefore started at the cell edge and swept the entire quadrant as a
    /// hard-edged diagonal — a chamfered box with a stepped notch where the border should meet, instead
    /// of the tight rounded corner every other terminal draws.
    ///
    /// Both guards read features the renderer chose (the border centreline row/column) rather than
    /// hard-coded pixel offsets, so they do not encode the pixel grid's origin inset.
    /// </remarks>
    [Collection("GoldenPng")]
    [Trait("Lane", "PlatformBoot")]
    public sealed class RoundedBoxCornerTests
    {
        // A cell about the size of a 14pt terminal font at 150% scaling: big enough that a
        // half-cell-radius turn and a tight one are far apart in pixels.
        private static readonly CellMetrics Metrics = new()
        {
            CellWidth = 16.0f,
            CellHeight = 36.0f,
            Baseline = 28.0f,
            Ascent = 28.0f,
            Descent = 8.0f
        };

        private const int Cols = 4;
        private const int Rows = 3;

        [AvaloniaFact]
        public void Arcs_LeaveAStraightRunFromTheCellEdge_SoTheBorderIsNotChamfered()
        {
            using SKBitmap bmp = RenderRoundedBox();
            var g = Geometry.Measure(bmp);

            float halfW = Metrics.CellWidth / 2f;
            float halfH = Metrics.CellHeight / 2f;
            float minRunX = halfW / 3f;
            float minRunY = halfH / 3f;

            // Top-left: the horizontal border must still be drawn from the corner cell's right edge
            // inward, and the vertical border from its bottom edge upward.
            AssertRun((g.LeftCol + halfW) - g.FirstLitXOnRow(bmp, g.TopRow), minRunX, "top-left horizontal");
            AssertRun((g.TopRow + halfH) - g.FirstLitYOnCol(bmp, g.LeftCol), minRunY, "top-left vertical");

            AssertRun(g.LastLitXOnRow(bmp, g.TopRow) - (g.RightCol - halfW), minRunX, "top-right horizontal");
            AssertRun((g.TopRow + halfH) - g.FirstLitYOnCol(bmp, g.RightCol), minRunY, "top-right vertical");

            AssertRun((g.LeftCol + halfW) - g.FirstLitXOnRow(bmp, g.BottomRow), minRunX, "bottom-left horizontal");
            AssertRun(g.LastLitYOnCol(bmp, g.LeftCol) - (g.BottomRow - halfH), minRunY, "bottom-left vertical");

            AssertRun(g.LastLitXOnRow(bmp, g.BottomRow) - (g.RightCol - halfW), minRunX, "bottom-right horizontal");
            AssertRun(g.LastLitYOnCol(bmp, g.RightCol) - (g.BottomRow - halfH), minRunY, "bottom-right vertical");
        }

        [AvaloniaFact]
        public void Arcs_AreAntialiased_SoTheyReadAsCurvesRatherThanStaircases()
        {
            using SKBitmap bmp = RenderRoundedBox();
            var g = Geometry.Measure(bmp);

            // A hard-edged (aliased) arc paints every pixel either background or full foreground. A
            // real curve lands partially on the pixels it crosses.
            int partial = 0;
            int cw = (int)Metrics.CellWidth;
            int chh = (int)Metrics.CellHeight;
            for (int y = System.Math.Max(0, g.TopRow - chh / 2); y < System.Math.Min(bmp.Height, g.TopRow + chh / 2); y++)
            {
                for (int x = System.Math.Max(0, g.LeftCol - cw / 2); x < System.Math.Min(bmp.Width, g.LeftCol + cw); x++)
                {
                    int lum = Lum(bmp.GetPixel(x, y));
                    if (lum > 24 && lum < 200) partial++;
                }
            }

            Assert.True(partial > 0, "The top-left arc painted no partially covered pixels; it is an aliased staircase, not a curve.");
        }

        private static void AssertRun(float actual, float required, string what)
            => Assert.True(
                actual >= required,
                $"{what} straight run was {actual:F1}px; expected at least {required:F1}px before the turn starts. " +
                "A shorter run means the arc ate the whole half-cell and the corner reads as a diagonal chamfer.");

        private static SKBitmap RenderRoundedBox()
        {
            var buffer = new TerminalBuffer(Cols, Rows)
            {
                Theme = new TerminalTheme { Foreground = TermColor.White, Background = TermColor.Black }
            };
            new AnsiParser(buffer).Process("╭──╮\r\n│  │\r\n╰──╯");

            int width = (int)System.Math.Ceiling(Cols * Metrics.CellWidth);
            int height = (int)System.Math.Ceiling(Rows * Metrics.CellHeight);

            using var glyphCache = new GlyphCache();
            return SnapshotService.Capture(buffer, Metrics, width, height, new SnapshotCaptureOptions
            {
                ForceBoxDrawingPrimitives = true,
                GlyphCache = glyphCache,
                HideCursor = true
            });
        }

        private static int Lum(SKColor c) => (c.Red + c.Green + c.Blue) / 3;

        private static bool Lit(SKColor c) => Lum(c) > 24;

        /// <summary>The four border centrelines the renderer actually chose, found by ink density.</summary>
        private readonly struct Geometry
        {
            public int TopRow { get; init; }
            public int BottomRow { get; init; }
            public int LeftCol { get; init; }
            public int RightCol { get; init; }

            public static Geometry Measure(SKBitmap bmp)
            {
                int halfH = bmp.Height / 2;
                int halfW = bmp.Width / 2;
                return new Geometry
                {
                    TopRow = DensestRow(bmp, 0, halfH),
                    BottomRow = DensestRow(bmp, halfH, bmp.Height),
                    LeftCol = DensestCol(bmp, 0, halfW),
                    RightCol = DensestCol(bmp, halfW, bmp.Width)
                };
            }

            private static int DensestRow(SKBitmap bmp, int from, int to)
            {
                int best = from, bestCount = -1;
                for (int y = from; y < to; y++)
                {
                    int count = 0;
                    for (int x = 0; x < bmp.Width; x++) if (Lit(bmp.GetPixel(x, y))) count++;
                    if (count > bestCount) { bestCount = count; best = y; }
                }
                return best;
            }

            private static int DensestCol(SKBitmap bmp, int from, int to)
            {
                int best = from, bestCount = -1;
                for (int x = from; x < to; x++)
                {
                    int count = 0;
                    for (int y = 0; y < bmp.Height; y++) if (Lit(bmp.GetPixel(x, y))) count++;
                    if (count > bestCount) { bestCount = count; best = x; }
                }
                return best;
            }

            public int FirstLitXOnRow(SKBitmap bmp, int y)
            {
                for (int x = 0; x < bmp.Width; x++) if (Lit(bmp.GetPixel(x, y))) return x;
                return bmp.Width;
            }

            public int LastLitXOnRow(SKBitmap bmp, int y)
            {
                for (int x = bmp.Width - 1; x >= 0; x--) if (Lit(bmp.GetPixel(x, y))) return x;
                return -1;
            }

            public int FirstLitYOnCol(SKBitmap bmp, int x)
            {
                for (int y = 0; y < bmp.Height; y++) if (Lit(bmp.GetPixel(x, y))) return y;
                return bmp.Height;
            }

            public int LastLitYOnCol(SKBitmap bmp, int x)
            {
                for (int y = bmp.Height - 1; y >= 0; y--) if (Lit(bmp.GetPixel(x, y))) return y;
                return -1;
            }
        }
    }
}

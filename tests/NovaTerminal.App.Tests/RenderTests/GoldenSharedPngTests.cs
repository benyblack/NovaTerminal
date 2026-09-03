using NovaTerminal.Shell;
using Avalonia.Headless.XUnit;
using NovaTerminal.Platform;
using NovaTerminal.Rendering;
using NovaTerminal.VT;
using NovaTerminal.Tests.Infra;
using System;
using Xunit;

namespace NovaTerminal.Tests.RenderTests
{
    /// <summary>
    /// CI-safe shared golden PNG contracts.
    ///
    /// Update shared baselines:
    ///   PowerShell: $env:UPDATE_SNAPSHOTS=1; dotnet test --filter GoldenSharedPng
    ///   Bash: UPDATE_SNAPSHOTS=1 dotnet test --filter GoldenSharedPng
    /// </summary>
    /// <remarks>
    /// "Shared" means the bytes must not depend on the machine, and for the box-drawing and
    /// block-element captures that holds only because they render through the geometric
    /// primitive painter instead of font glyphs. Reaching that painter takes a glyph cache:
    /// it lives behind <c>if (_glyphCache != null)</c> in
    /// <c>TerminalDrawOperation.DrawRowTextFromSnapshot</c>, so a cacheless capture silently
    /// falls through to plain font rendering and <c>Force*Primitives</c> does nothing at all.
    ///
    /// These three baselines were captured that way, which made them accidentally
    /// machine-specific. <see cref="SKTypeface.FromFamilyName"/> returns null for an
    /// uninstalled family on Windows, so they recorded notdef boxes; fontconfig never fails a
    /// lookup and substitutes its best match instead, so on Linux the same test rendered real
    /// glyphs out of whatever font happened to be installed and the comparison could not pass.
    /// <c>PrimitiveCaptures_AreFontIndependent</c> pins the invariant down directly.
    /// </remarks>
    [Trait("Category", "GoldenSharedPng")]
    [Collection("GoldenPng")]
    [Trait("Lane", "PlatformBoot")]
    public sealed class GoldenSharedPngTests
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
        public void GoldenSharedPng_BlockAndShadePrimitives_MatchBaseline()
        {
            const int cols = 40;
            const int rows = 6;
            var buffer = CreateThemedBuffer(cols, rows);
            var parser = new AnsiParser(buffer);

            parser.Process("\u2580\u2581\u2582\u2583\u2584\u2585\u2586\u2587\u2588\u2589\u258A\u258B\u258C\u258D\u258E\u258F\r\n");
            parser.Process("\u2591\u2592\u2593\u2588 \u2596\u2597\u2598\u2599\u259A\u259B\u259C\u259D\u259E\u259F\r\n");
            parser.Process("\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\r\n");

            // GlyphCache is not an optimisation here - it is what puts the cells on the
            // primitive path at all. See the class remarks.
            using var glyphCache = new GlyphCache();
            byte[] pngBytes = SnapshotService.CapturePng(buffer, Metrics, WidthFor(cols), HeightFor(rows), new SnapshotCaptureOptions
            {
                ForceBoxDrawingPrimitives = true,
                ForceBlockElementPrimitives = true,
                GlyphCache = glyphCache,
                HideCursor = true
            });

            SnapshotService.CompareToBaseline(BaselineScope.Shared, "shared/BlockAndShadePrimitives", pngBytes);
        }

        [AvaloniaFact]
        public void GoldenSharedPng_BoxDrawingGridPrimitives_MatchBaseline()
        {
            const int cols = 32;
            const int rows = 7;
            var buffer = CreateThemedBuffer(cols, rows);
            var parser = new AnsiParser(buffer);

            parser.Process("┌────────┬────────┐\r\n");
            parser.Process("│        │        │\r\n");
            parser.Process("├────────┼────────┤\r\n");
            parser.Process("│        │        │\r\n");
            parser.Process("└────────┴────────┘\r\n");
            parser.Process("╔══════╦══════╗\r\n");
            parser.Process("╚══════╩══════╝");

            using var glyphCache = new GlyphCache();
            byte[] pngBytes = SnapshotService.CapturePng(buffer, Metrics, WidthFor(cols), HeightFor(rows), new SnapshotCaptureOptions
            {
                ForceBoxDrawingPrimitives = true,
                ForceBlockElementPrimitives = true,
                GlyphCache = glyphCache,
                HideCursor = true
            });

            SnapshotService.CompareToBaseline(BaselineScope.Shared, "shared/BoxDrawingGridPrimitives", pngBytes);
        }

        [AvaloniaFact]
        public void GoldenSharedPng_CursorAndSelectionOverlay_MatchBaseline()
        {
            const int cols = 24;
            const int rows = 4;
            var buffer = CreateThemedBuffer(cols, rows);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[48;2;25;25;25m                        \x1b[0m\r\n");
            parser.Process("\x1b[48;2;10;70;140m                        \x1b[0m\r\n");
            parser.Process("\x1b[48;2;120;40;40m                        \x1b[0m\r\n");
            parser.Process("\x1b[48;2;35;95;40m                        \x1b[0m");

            var selection = new SelectionState
            {
                IsActive = true,
                Start = (1, 3),
                End = (2, 18)
            };
            buffer.CursorRow = 1;
            buffer.CursorCol = 12;

            byte[] pngBytes = SnapshotService.CapturePng(buffer, Metrics, WidthFor(cols), HeightFor(rows), new SnapshotCaptureOptions
            {
                Selection = selection,
                HideCursor = false
            });

            SnapshotService.CompareToBaseline(BaselineScope.Shared, "shared/CursorSelectionOverlay", pngBytes);
        }

        [AvaloniaFact]
        public void GoldenSharedPng_SgrBackgroundAndInverseRegions_MatchBaseline()
        {
            const int cols = 30;
            const int rows = 5;
            var buffer = CreateThemedBuffer(cols, rows);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[48;2;200;40;40m          \x1b[0m\x1b[48;2;40;160;40m          \x1b[0m\r\n");
            parser.Process("\x1b[48;2;40;40;200m          \x1b[0m\x1b[48;2;160;120;30m          \x1b[0m\r\n");
            parser.Process("\x1b[7m\x1b[48;2;80;80;80m          \x1b[0m\x1b[7m\x1b[48;2;120;20;100m          \x1b[0m\r\n");
            parser.Process("\x1b[48;2;20;120;120m                    \x1b[0m");

            byte[] pngBytes = SnapshotService.CapturePng(buffer, Metrics, WidthFor(cols), HeightFor(rows), new SnapshotCaptureOptions
            {
                HideCursor = true
            });

            SnapshotService.CompareToBaseline(BaselineScope.Shared, "shared/SgrBackgroundInverseRegions", pngBytes);
        }

        [AvaloniaFact]
        public void GoldenSharedPng_SeamRegressionSurface_MatchBaseline()
        {
            const int cols = 80;
            const int rows = 6;
            var buffer = CreateThemedBuffer(cols, rows);
            var parser = new AnsiParser(buffer);

            parser.Process(new string('\u2588', 32) + "\r\n");
            parser.Process(new string('\u2581', 32) + "\r\n");
            parser.Process(new string('\u2592', 32) + "\r\n");
            parser.Process("┌──────────────────────────────┐\r\n");
            parser.Process("└──────────────────────────────┘");

            // Renders at 1.5 on purpose: this is the fractional-scaling case where the box borders
            // below used to disappear outright. TerminalSnapshotRenderer passed the scaling down
            // while handing the draw operation an unscaled DIP-sized canvas, so a box stroke -
            // picked as a whole number of *device* pixels - was emitted as a 0.67px rect with
            // antialiasing off and rasterised to nothing. Mean luma across the two box rows was
            // 0.0007 here against 0.0219 at scaling 1.0. The block and shade rows never noticed,
            // because they fill whole cells instead of converting a device-pixel count back to
            // DIPs, which is why the test looked like it was passing on something.
            //
            // Two consequences for this baseline. It is 1.5x the DIP size, because the capture now
            // returns the device-pixel image those DIPs describe. And the border is one crisp
            // device pixel at every scaling, so its share of the image shrinks as the image grows -
            // the luma here is lower than at scaling 1.0 rather than equal to it, by design.
            using var glyphCache = new GlyphCache();
            byte[] pngBytes = SnapshotService.CapturePng(buffer, Metrics, WidthFor(cols), HeightFor(rows), new SnapshotCaptureOptions
            {
                ForceBoxDrawingPrimitives = true,
                ForceBlockElementPrimitives = true,
                GlyphCache = glyphCache,
                RenderScaling = 1.5,
                HideCursor = true
            });

            SnapshotService.CompareToBaseline(BaselineScope.Shared, "shared/SeamRegressionSurface", pngBytes);
        }

        /// <summary>
        /// The guard that makes "shared" mean what it says: a primitive capture must not care
        /// which font is installed, so the same buffer rendered against wildly different
        /// families - including one that does not exist - must come out byte-identical.
        /// </summary>
        /// <remarks>
        /// Without this, the only thing standing between the suite and a machine-specific
        /// baseline is whether someone remembered to pass a glyph cache. Drop the cache from
        /// the captures above and this test fails, rather than three baselines quietly becoming
        /// portraits of one developer's font list.
        /// </remarks>
        [AvaloniaTheory]
        [InlineData("Liberation Sans")]
        [InlineData("Noto Sans Mono")]
        [InlineData("This Font Is Not Installed Anywhere")]
        public void PrimitiveCaptures_AreFontIndependent(string family)
        {
            const int cols = 24;
            const int rows = 4;

            byte[] Capture(string typefaceFamily)
            {
                var buffer = CreateThemedBuffer(cols, rows);
                var parser = new AnsiParser(buffer);
                parser.Process("█░▒▓▄▀\r\n");
                parser.Process("┌─┬─┐\r\n");
                parser.Process("└─┴─┘");

                using var glyphCache = new GlyphCache();
                return SnapshotService.CapturePng(buffer, Metrics, WidthFor(cols), HeightFor(rows), new SnapshotCaptureOptions
                {
                    ForceBoxDrawingPrimitives = true,
                    ForceBlockElementPrimitives = true,
                    GlyphCache = glyphCache,
                    HideCursor = true,
                    TypefaceFamily = typefaceFamily
                });
            }

            // Compared against the family the shared baselines are captured with, so a
            // divergence points at the capture the baselines actually use.
            Assert.Equal(
                Capture(TerminalSnapshotOptions.DefaultTypefaceFamily),
                Capture(family));
        }

        private static TerminalBuffer CreateThemedBuffer(int cols, int rows)
        {
            return new TerminalBuffer(cols, rows)
            {
                Theme = new TerminalTheme
                {
                    Foreground = TermColor.White,
                    Background = TermColor.Black,
                    CursorColor = TermColor.FromRgb(0xFF, 0x66, 0x00)
                }
            };
        }

        private static int WidthFor(int cols)
            => (int)Math.Ceiling((cols * Metrics.CellWidth) + 8);

        private static int HeightFor(int rows)
            => (int)Math.Ceiling(rows * Metrics.CellHeight);
    }
}

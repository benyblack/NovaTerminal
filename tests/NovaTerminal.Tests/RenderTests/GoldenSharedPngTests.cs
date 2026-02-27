using Avalonia.Headless.XUnit;
using NovaTerminal.Core;
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
    [Trait("Category", "GoldenSharedPng")]
    [Collection("GoldenPng")]
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

            byte[] pngBytes = SnapshotService.CapturePng(buffer, Metrics, WidthFor(cols), HeightFor(rows), new SnapshotCaptureOptions
            {
                ForceBoxDrawingPrimitives = true,
                ForceBlockElementPrimitives = true,
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

            byte[] pngBytes = SnapshotService.CapturePng(buffer, Metrics, WidthFor(cols), HeightFor(rows), new SnapshotCaptureOptions
            {
                ForceBoxDrawingPrimitives = true,
                ForceBlockElementPrimitives = true,
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

            byte[] pngBytes = SnapshotService.CapturePng(buffer, Metrics, WidthFor(cols), HeightFor(rows), new SnapshotCaptureOptions
            {
                ForceBoxDrawingPrimitives = true,
                ForceBlockElementPrimitives = true,
                RenderScaling = 1.5,
                HideCursor = true
            });

            SnapshotService.CompareToBaseline(BaselineScope.Shared, "shared/SeamRegressionSurface", pngBytes);
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

using NovaTerminal.Core;
using NovaTerminal.Tests.Infra;
using System;
using Xunit;

namespace NovaTerminal.Tests.RenderTests
{
    /// <summary>
    /// Optional, font-dependent golden PNG contracts.
    ///
    /// Update font baselines:
    ///   PowerShell: $env:ENABLE_FONT_GOLDENS=1; $env:UPDATE_SNAPSHOTS=1; dotnet test --filter GoldenFontPng
    ///   Bash: ENABLE_FONT_GOLDENS=1 UPDATE_SNAPSHOTS=1 dotnet test --filter GoldenFontPng
    /// </summary>
    [Trait("Category", "GoldenFontPng")]
    [Collection("GoldenPng")]
    public sealed class GoldenFontPngTests
    {
        private static readonly CellMetrics Metrics = new()
        {
            CellWidth = 8.4f,
            CellHeight = 18.0f,
            Baseline = 14.0f,
            Ascent = 14.0f,
            Descent = 4.0f
        };

        [FontGoldenFact("CaskaydiaCove Nerd Font", "Cascadia Code PL", "MesloLGS NF")]
        public void GoldenFontPng_PowerlinePrompt_MatchBaseline()
        {
            const int cols = 44;
            const int rows = 4;
            var buffer = CreateThemedBuffer(cols, rows);
            var parser = new AnsiParser(buffer);
            parser.Process("\x1b[48;2;40;90;180m\x1b[38;2;255;255;255m user@host \x1b[0m\r\n");
            parser.Process("\x1b[48;2;80;40;150m\x1b[38;2;255;255;255m /workspace/project \x1b[0m\r\n");
            parser.Process("\x1b[38;2;100;220;120m main\x1b[0m");

            byte[] pngBytes = SnapshotService.CapturePng(buffer, Metrics, WidthFor(cols), HeightFor(rows), new SnapshotCaptureOptions
            {
                HideCursor = true
            });

            SnapshotService.CompareToBaseline(BaselineScope.OS, "font/PowerlinePrompt", pngBytes);
        }

        [FontGoldenFact("Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji", "Noto Emoji")]
        public void GoldenFontPng_EmojiClusters_MatchBaseline()
        {
            const int cols = 32;
            const int rows = 4;
            var buffer = CreateThemedBuffer(cols, rows);
            var parser = new AnsiParser(buffer);
            parser.Process("👨‍👩‍👧‍👦  👩🏽‍💻  🧪\r\n");
            parser.Process("🚀✨  🧵🔧  🐧🍎🪟\r\n");
            parser.Process("Flags: 🇺🇸 🇯🇵 🇧🇷");

            byte[] pngBytes = SnapshotService.CapturePng(buffer, Metrics, WidthFor(cols), HeightFor(rows), new SnapshotCaptureOptions
            {
                HideCursor = true
            });

            SnapshotService.CompareToBaseline(BaselineScope.OS, "font/EmojiClusters", pngBytes);
        }

        [FontGoldenFact("Segoe UI", "Noto Sans", "Helvetica Neue", "Arial")]
        public void GoldenFontPng_ComplexShaping_MatchBaseline()
        {
            const int cols = 40;
            const int rows = 5;
            var buffer = CreateThemedBuffer(cols, rows);
            var parser = new AnsiParser(buffer);
            parser.Process("Arabic: مرحبا بالعالم\r\n");
            parser.Process("Devanagari: नमस्ते दुनिया\r\n");
            parser.Process("Ligatures: office affine waffle ffi");

            byte[] pngBytes = SnapshotService.CapturePng(buffer, Metrics, WidthFor(cols), HeightFor(rows), new SnapshotCaptureOptions
            {
                EnableLigatures = true,
                HideCursor = true
            });

            SnapshotService.CompareToBaseline(BaselineScope.OS, "font/ComplexShaping", pngBytes);
        }

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

        private static int WidthFor(int cols)
            => (int)Math.Ceiling((cols * Metrics.CellWidth) + 8);

        private static int HeightFor(int rows)
            => (int)Math.Ceiling(rows * Metrics.CellHeight);
    }
}

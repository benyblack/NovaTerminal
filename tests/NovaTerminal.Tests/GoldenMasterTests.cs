using Xunit;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using NovaTerminal.Core;
using NovaTerminal.Tests.Infra;
using SkiaSharp;
using System;

namespace NovaTerminal.Tests
{
    /// <summary>
    /// Golden Master Tests
    /// 
    /// These tests capture the terminal rendered state and compare it pixel-by-pixel 
    /// against known good baselines stored in tests/NovaTerminal.Tests/Baselines/Golden/.
    ///
    /// To regenerate baselines (e.g., after intentionally changing rendering algorithms):
    ///   UPDATE_SNAPSHOTS=1 dotnet test --filter Category=Replay
    ///   (or run from your IDE with the UPDATE_SNAPSHOTS=1 environment variable)
    ///
    /// If comparison fails, diff artifacts (expected, actual, diff) are written to:
    ///   tests/NovaTerminal.Tests/TestOutput/Diffs/
    /// </summary>
    public class GoldenMasterTests
    {
        private readonly CellMetrics _standardMetrics = new CellMetrics
        {
            CellWidth = 8.4f,
            CellHeight = 18.0f,
            Baseline = 14.0f,
            Ascent = 14.0f,
            Descent = 4.0f
        };

        [AvaloniaFact]
        public void RenderComplexEmojiCluster_MatchBaseline()
        {
            var buffer = new TerminalBuffer(20, 5);
            var parser = new AnsiParser(buffer);
            // Family: Man, Woman, Girl, Boy
            parser.Process("\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466");

            var snapshot = SnapshotService.Capture(buffer, _standardMetrics, 168, 90);
            SnapshotService.CompareToBaseline("Golden/FamilyEmoji", snapshot);
        }

        [AvaloniaFact]
        public void RenderPowerlineSymbols_MatchBaseline()
        {
            var buffer = new TerminalBuffer(40, 3);
            var parser = new AnsiParser(buffer);
            parser.Process("\ue0b0 \ue0b2 \ue0b1 \ue0b3"); // Powerline triangles and separators

            var snapshot = SnapshotService.Capture(buffer, _standardMetrics, 336, 54);
            SnapshotService.CompareToBaseline("Golden/PowerlineSymbols", snapshot);
        }

        [AvaloniaFact]
        public void RenderStandardAlphanumericGrid_MatchBaseline()
        {
            var buffer = new TerminalBuffer(20, 5);
            var parser = new AnsiParser(buffer);
            parser.Process("Line 1: ABCD 1234\r\n");
            parser.Process("Line 2: EFGH 5678\r\n");
            parser.Process("Line 3: WXYZ 90\r\n");
            parser.Process("Line 4: !@#$ %&*()\r\n");
            parser.Process("Line 5: _+-= []{}|");

            var snapshot = SnapshotService.Capture(buffer, _standardMetrics, 168, 90);
            SnapshotService.CompareToBaseline("Golden/AlphaNumericGrid", snapshot);
        }

        [AvaloniaFact]
        public void RenderWideCharacters_MatchBaseline()
        {
            var buffer = new TerminalBuffer(20, 3);
            var parser = new AnsiParser(buffer);
            parser.Process("Japanese: こんにちは\r\n");
            parser.Process("Chinese: 你好\r\n");
            parser.Process("Mixed: a字b\r\n");

            var snapshot = SnapshotService.Capture(buffer, _standardMetrics, 168, 54);
            SnapshotService.CompareToBaseline("Golden/WideCharacters", snapshot);
        }

        [AvaloniaFact]
        public void RenderTrueColorBlocks_MatchBaseline()
        {
            var buffer = new TerminalBuffer(20, 2);
            var parser = new AnsiParser(buffer);
            // Red FG, Blue BG
            parser.Process("\x1b[38;2;255;0;0m\x1b[48;2;0;0;255mRed on Blue\x1b[0m\r\n");
            // Highlight blocks
            parser.Process("\x1b[38;2;0;255;0m\x1b[48;2;255;255;0mGreen on Yellow\x1b[0m");

            var snapshot = SnapshotService.Capture(buffer, _standardMetrics, 168, 36);
            SnapshotService.CompareToBaseline("Golden/TrueColorBlocks", snapshot);
        }
    }
}

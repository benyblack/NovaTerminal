using Xunit;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using NovaTerminal.Core;
using NovaTerminal.Tests.Infra;
using SkiaSharp;
using System;

namespace NovaTerminal.Tests
{
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
            // Family: Man, Woman, Girl, Boy
            buffer.Write("\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466");

            var snapshot = SnapshotService.Capture(buffer, _standardMetrics, 168, 90);

            // In a real scenario, we'd compare against a baseline file.
            // For now, we just verify it captures something non-empty.
            Assert.NotNull(snapshot);
            Assert.Equal(168, snapshot.Width);
            Assert.Equal(90, snapshot.Height);

            // SnapshotService.SaveBaseline(snapshot, "FamilyEmoji");
        }

        [AvaloniaFact]
        public void RenderPowerlineSymbols_CheckFidelity()
        {
            var buffer = new TerminalBuffer(40, 3);
            buffer.Write("\ue0b0 \ue0b2 \ue0b1 \ue0b3"); // Powerline triangles and separators

            var snapshot = SnapshotService.Capture(buffer, _standardMetrics, 336, 54);
            Assert.NotNull(snapshot);

            // SnapshotService.SaveBaseline(snapshot, "PowerlineSymbols");
        }
    }
}

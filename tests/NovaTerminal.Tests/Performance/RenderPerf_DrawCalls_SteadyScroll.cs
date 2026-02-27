using NovaTerminal.Tests.Performance.Infra;
using Avalonia.Headless.XUnit;
using Xunit;
using Xunit.Abstractions;

namespace NovaTerminal.Tests.Performance
{
    [Collection("RendererStatistics")]
    public class RenderPerf_DrawCalls_SteadyScroll
    {
        private readonly ITestOutputHelper _output;

        public RenderPerf_DrawCalls_SteadyScroll(ITestOutputHelper output)
        {
            _output = output;
        }

        [AvaloniaFact]
        [Trait("Category", "Performance")]
        public void RenderPerf_DrawCalls_SteadyScroll_WithinConservativeCeilings()
        {
            const int warmupFrames = 40;
            const int measuredFrames = 160;
            const double avgDrawCallsTextCeiling = 250;
            const double p95DrawCallsTextCeiling = 600;
            const double avgDrawCallsTotalCeiling = 600;

            using RenderPerfRunResult run = RenderPerfSteadyScrollHarness.Run(warmupFrames, measuredFrames);

            if (run.Frames.Count < warmupFrames + measuredFrames)
            {
                Assert.Fail($"Insufficient metrics frames. expected>={warmupFrames + measuredFrames}, actual={run.Frames.Count}, path={run.OutputPath}");
            }

            var measured = RenderPerfJsonl.SkipWarmup(run.Frames, warmupFrames);
            double avgDrawCallsText = RenderPerfJsonl.Average(measured, f => f.DrawCallsText);
            double p95DrawCallsText = RenderPerfJsonl.Percentile(measured, f => f.DrawCallsText, p: 0.95);
            double avgDrawCallsTotal = RenderPerfJsonl.Average(measured, f => f.DrawCallsTotal);

            _output.WriteLine($"Metrics file: {run.OutputPath}");
            _output.WriteLine($"Frames total={run.Frames.Count}, warmup={warmupFrames}, measured={measured.Count}");
            _output.WriteLine($"AvgDrawCallsText={avgDrawCallsText:F2} (ceiling {avgDrawCallsTextCeiling:F2})");
            _output.WriteLine($"P95DrawCallsText={p95DrawCallsText:F2} (ceiling {p95DrawCallsTextCeiling:F2})");
            _output.WriteLine($"AvgDrawCallsTotal={avgDrawCallsTotal:F2} (ceiling {avgDrawCallsTotalCeiling:F2})");

            Assert.True(
                avgDrawCallsText <= avgDrawCallsTextCeiling,
                $"Avg text draw-call regression: {avgDrawCallsText:F2} > {avgDrawCallsTextCeiling:F2}.");
            Assert.True(
                p95DrawCallsText <= p95DrawCallsTextCeiling,
                $"P95 text draw-call regression: {p95DrawCallsText:F2} > {p95DrawCallsTextCeiling:F2}.");
            Assert.True(
                avgDrawCallsTotal <= avgDrawCallsTotalCeiling,
                $"Avg total draw-call regression: {avgDrawCallsTotal:F2} > {avgDrawCallsTotalCeiling:F2}.");
        }
    }
}

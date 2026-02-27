using NovaTerminal.Tests.Performance.Infra;
using Avalonia.Headless.XUnit;
using Xunit;
using Xunit.Abstractions;

namespace NovaTerminal.Tests.Performance
{
    [Collection("RendererStatistics")]
    public class RenderPerf_Allocations_SteadyScroll
    {
        private readonly ITestOutputHelper _output;

        public RenderPerf_Allocations_SteadyScroll(ITestOutputHelper output)
        {
            _output = output;
        }

        [AvaloniaFact]
        [Trait("Category", "Performance")]
        public void RenderPerf_Allocations_SteadyScroll_WithinConservativeCeilings()
        {
            const int warmupFrames = 40;
            const int measuredFrames = 160;
            const double avgAllocCeiling = 32_000;
            const double p95AllocCeiling = 96_000;

            using RenderPerfRunResult run = RenderPerfSteadyScrollHarness.Run(warmupFrames, measuredFrames);

            if (run.Frames.Count < warmupFrames + measuredFrames)
            {
                Assert.Fail($"Insufficient metrics frames. expected>={warmupFrames + measuredFrames}, actual={run.Frames.Count}, path={run.OutputPath}");
            }

            var measured = RenderPerfJsonl.SkipWarmup(run.Frames, warmupFrames);
            double avgAlloc = RenderPerfJsonl.Average(measured, f => f.AllocBytesThisFrame);
            double p95Alloc = RenderPerfJsonl.Percentile(measured, f => f.AllocBytesThisFrame, p: 0.95);

            _output.WriteLine($"Metrics file: {run.OutputPath}");
            _output.WriteLine($"Frames total={run.Frames.Count}, warmup={warmupFrames}, measured={measured.Count}");
            _output.WriteLine($"AvgAllocBytesPerFrame={avgAlloc:F2} (ceiling {avgAllocCeiling:F2})");
            _output.WriteLine($"P95AllocBytesPerFrame={p95Alloc:F2} (ceiling {p95AllocCeiling:F2})");

            Assert.True(
                avgAlloc <= avgAllocCeiling,
                $"Avg allocation regression: {avgAlloc:F2} > {avgAllocCeiling:F2} bytes/frame.");
            Assert.True(
                p95Alloc <= p95AllocCeiling,
                $"P95 allocation regression: {p95Alloc:F2} > {p95AllocCeiling:F2} bytes/frame.");
        }
    }
}

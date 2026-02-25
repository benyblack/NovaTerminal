using NovaTerminal.Core;
using System;
using System.Diagnostics;
using System.Text;
using NovaTerminal.Tests.Infra;
using Xunit;
using Xunit.Abstractions;

namespace NovaTerminal.Tests.Performance
{
    [Collection("RendererStatistics")]
    public class PerformanceTests
    {
        private readonly ITestOutputHelper _output;

        public PerformanceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        [Trait("Category", "Performance")]
        public void LargeThroughput_Benchmark()
        {
            const double defaultThresholdMbPerSec = 1.75;
            double thresholdMbPerSec = defaultThresholdMbPerSec;
            string? thresholdOverride = Environment.GetEnvironmentVariable("NOVATERM_PERF_THROUGHPUT_MBPS");
            if (!string.IsNullOrWhiteSpace(thresholdOverride) &&
                double.TryParse(thresholdOverride, out double parsedThreshold) &&
                parsedThreshold > 0)
            {
                thresholdMbPerSec = parsedThreshold;
            }

            // Generate 1MB of ANSI-heavy text
            var sb = new StringBuilder();
            for (int i = 0; i < 50000; i++)
            {
                sb.Append("\x1b[31mColor\x1b[0m ");
                sb.Append($"Line {i} Text ");
                if (i % 10 == 0) sb.Append("\r\n");
            }
            string data = sb.ToString();

            // Warmup to avoid first-call JIT skew in throughput measurements.
            var warmupBuffer = new TerminalBuffer(80, 24);
            var warmupParser = new AnsiParser(warmupBuffer);
            warmupParser.Process(data);

            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            RendererStatistics.Reset();
            var sw = Stopwatch.StartNew();
            parser.Process(data);
            sw.Stop();

            long bytes = Encoding.UTF8.GetByteCount(data);
            double seconds = sw.Elapsed.TotalSeconds;
            double mbPerSec = (bytes / 1024.0 / 1024.0) / seconds;

            _output.WriteLine($"Processed {bytes / 1024.0 / 1024.0:F2} MB in {seconds:F4}s");
            _output.WriteLine($"Throughput: {mbPerSec:F2} MB/s");
            _output.WriteLine($"Threshold: {thresholdMbPerSec:F2} MB/s");

            // Default threshold is tuned for reliability across dev/CI hosts.
            // For stricter perf gating, set NOVATERM_PERF_THROUGHPUT_MBPS in the environment.
            Assert.True(
                mbPerSec > thresholdMbPerSec,
                $"Performance regression! Throughput {mbPerSec:F2} MB/s is below threshold ({thresholdMbPerSec:F2} MB/s)");
        }

        [Fact]
        [Trait("Category", "Performance")]
        public void ReflowStress_Benchmark()
        {
            var buffer = new TerminalBuffer(80, 1000); // 1000 lines of history
            for (int i = 0; i < 1000; i++)
            {
                buffer.WriteContent($"Line {i:D4} XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX\r\n", false);
            }

            var sw = Stopwatch.StartNew();
            int iterations = 100;
            for (int i = 0; i < iterations; i++)
            {
                buffer.Resize(40 + (i % 40), 24);
            }
            sw.Stop();

            double avgMs = sw.Elapsed.TotalMilliseconds / iterations;
            _output.WriteLine($"Average Reflow Time (1000 lines): {avgMs:F2}ms");

            // Threshold: Expect avg reflow under 10ms for smooth resizing
            Assert.True(avgMs < 15, $"Reflow too slow! {avgMs:F2}ms > 15ms threshold.");
        }
    }
}

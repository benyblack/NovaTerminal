using NovaTerminal.Core;
using System;
using System.Diagnostics;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace NovaTerminal.Tests.Performance
{
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
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // Generate 1MB of ANSI-heavy text
            var sb = new StringBuilder();
            var random = new Random(42);
            for (int i = 0; i < 50000; i++)
            {
                sb.Append("\x1b[31mColor\x1b[0m ");
                sb.Append($"Line {i} Text ");
                if (i % 10 == 0) sb.Append("\r\n");
            }
            string data = sb.ToString();

            RendererStatistics.Reset();
            var sw = Stopwatch.StartNew();
            parser.Process(data);
            sw.Stop();

            long bytes = RendererStatistics.BytesProcessed;
            double seconds = sw.Elapsed.TotalSeconds;
            double mbPerSec = (bytes / 1024.0 / 1024.0) / seconds;

            _output.WriteLine($"Processed {bytes / 1024.0 / 1024.0:F2} MB in {seconds:F4}s");
            _output.WriteLine($"Throughput: {mbPerSec:F2} MB/s");

            // Threshold: Expect > 5MB/s in Debug, > 50MB/s in Release.
            // We set it to 5MB/s to avoid failing in developer/CI debug environments.
            Assert.True(mbPerSec > 5, $"Performance regression! Throughput {mbPerSec:F2} MB/s is below threshold (5 MB/s)");
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
            Assert.True(avgMs < 10, $"Reflow too slow! {avgMs:F2}ms > 10ms threshold.");
        }
    }
}

using NovaTerminal.Core;
using System;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace NovaTerminal.Tests.Performance
{
    public class LatencyTests
    {
        private readonly ITestOutputHelper _output;

        public LatencyTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        [Trait("Category", "Latency")]
        public void InputToBuffer_Latency_Test()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // Measure time to process a single character
            var sw = new Stopwatch();
            
            // Warmup
            parser.Process("A");

            long totalTicks = 0;
            int iterations = 1000;
            
            for (int i = 0; i < iterations; i++)
            {
                sw.Restart();
                parser.Process("X");
                sw.Stop();
                totalTicks += sw.ElapsedTicks;
            }

            double avgUs = (double)totalTicks / iterations / (Stopwatch.Frequency / 1_000_000.0);
            _output.WriteLine($"Average Input Processing Latency: {avgUs:F2}μs");

            // Threshold: Expect < 100μs for single char processing
            Assert.True(avgUs < 100, $"Input latency too high! {avgUs:F2}μs > 100μs threshold.");
        }
    }
}

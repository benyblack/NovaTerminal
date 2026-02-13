using NovaTerminal.Core;
using System;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace NovaTerminal.Tests.Performance
{
    public class MemoryLeakTest
    {
        private readonly ITestOutputHelper _output;

        public MemoryLeakTest(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        [Trait("Category", "Stress")]
        public void Resize_MemoryStability_StressTest()
        {
            var buffer = new TerminalBuffer(80, 24);
            long initialMemory = GC.GetTotalMemory(true);

            _output.WriteLine($"Initial memory: {initialMemory / 1024.0 / 1024.0:F2} MB");

            for (int i = 0; i < 1000; i++)
            {
                // Rapidly resize and write
                buffer.Resize(40 + (i % 80), 20 + (i % 40));
                buffer.Write($"Stress Line {i}\r\n");

                if (i % 100 == 0)
                {
                    _output.WriteLine($"Iteration {i}...");
                }
            }

            // Cleanup
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long finalMemory = GC.GetTotalMemory(true);
            _output.WriteLine($"Final memory after 1000 resizes: {finalMemory / 1024.0 / 1024.0:F2} MB");

            // Threshold: Memory should not grow unboundedly. 
            // 50MB of overhead is reasonable for fragmentation, but 500MB would be a leak.
            long diff = finalMemory - initialMemory;
            _output.WriteLine($"Growth: {diff / 1024.0 / 1024.0:F2} MB");

            Assert.True(diff < 50 * 1024 * 1024, $"Memory leak detected! Growth: {diff / 1024.0 / 1024.0:F2} MB");
        }
    }
}

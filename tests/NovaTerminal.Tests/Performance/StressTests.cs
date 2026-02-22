using NovaTerminal.Core;
using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace NovaTerminal.Tests.Performance
{
    public class StressTests
    {
        private readonly ITestOutputHelper _output;

        public StressTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        [Trait("Category", "Stress")]
        public async Task DataFlood_Backpressure_StressTest()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // 10MB of data to flood
            int totalBytes = 10 * 1024 * 1024;
            byte[] chunk = Encoding.UTF8.GetBytes(new string('X', 8192) + "\r\n");
            
            long initialMemory = GC.GetTotalMemory(true);
            var sw = Stopwatch.StartNew();

            int bytesSent = 0;
            while (bytesSent < totalBytes)
            {
                parser.Process(Encoding.UTF8.GetString(chunk));
                bytesSent += chunk.Length;
                
                // Yield occasionally to simulate async processing if needed
                if (bytesSent % (1024 * 1024) == 0)
                {
                    await Task.Yield();
                }
            }

            sw.Stop();
            long finalMemory = GC.GetTotalMemory(true);
            long diffMegaBytes = (finalMemory - initialMemory) / 1024 / 1024;

            _output.WriteLine($"Flooded {totalBytes / 1024 / 1024}MB in {sw.Elapsed.TotalSeconds:F2}s");
            _output.WriteLine($"Memory Delta: {diffMegaBytes} MB");

            // Assertions
            Assert.True(sw.Elapsed.TotalSeconds < 10, "Flood processing took too long (> 10s for 10MB)");
            // Allow some growth for scrollback, but prevent runaway leaks
            // 24 rows * 80 cols per line + history limit (default 1000)
            // Each cell is ~4-8 bytes. 1000 lines * 80 * 8 = 640KB. 
            // 10MB flood should be well within memory limits if history is capped.
            Assert.True(diffMegaBytes < 100, $"Potential memory leak detected! Delta: {diffMegaBytes}MB");
        }
    }
}
